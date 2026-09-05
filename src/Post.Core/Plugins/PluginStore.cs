using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Post.Core.Plugins;

/// <summary>
/// The plugins on this machine: what is installed, and putting one there or taking it
/// away again.
///
/// Installing means downloading an archive and unpacking code that will later run inside
/// Post with everything Post can reach. The checksum below proves the file arrived as it
/// was published; it cannot say the code is safe. That is what review on the repository
/// is for, and why nothing installs or updates without being asked.
/// </summary>
public static class PluginStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string Folder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "plugins");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string FolderFor(string id) => Path.Combine(Folder, SafeId(id));

    /// <summary>Every plugin installed here, read from the manifest kept beside each.</summary>
    public static IReadOnlyList<PluginManifest> Installed()
    {
        var found = new List<PluginManifest>();
        try
        {
            foreach (var folder in Directory.GetDirectories(Folder))
            {
                // A copy an update replaced still has its manifest, and is on its way out.
                if (Path.GetFileName(folder).Contains(Retired, StringComparison.Ordinal)) continue;

                var manifest = Path.Combine(folder, "plugin.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifest), Json) is { } value && value.CanBeLoaded)
                        found.Add(value);
                }
                catch { }
            }
        }
        catch { }
        return [.. found.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static PluginManifest? InstalledVersionOf(string id)
        => Installed().FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetches, checks and unpacks a plugin. The archive is verified before a single file
    /// is written, and the previous copy is only replaced once the new one is in hand.
    /// </summary>
    public static async Task InstallAsync(PluginManifest manifest, HttpClient client, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!manifest.CanBeInstalled) throw new InvalidOperationException("That plugin's description does not say where to fetch it, or what it should hash to.");

        var archive = Path.Combine(Path.GetTempPath(), $"post-plugin-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"post-plugin-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);

            // Loose files are the usual way: the repository holds them in a folder, so
            // there is no archive and no release to cut. Each is checked on its own.
            if (manifest.HasFiles) await FetchFilesAsync(manifest, staging, client, progress, token);
            else
            {
                await DownloadAsync(manifest.Download, archive, client, progress, token);

                var actual = await Sha256Async(archive, token);
                if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The download does not match the checksum {manifest.Name} publishes, so it has not been installed." +
                        $"{Environment.NewLine}{Environment.NewLine}expected {manifest.Sha256}{Environment.NewLine}     got {actual}");

                ExtractSafely(archive, staging);
            }

            var target = FolderFor(manifest.Id);
            Retire(target);
            Directory.Move(staging, target);

            // The manifest is kept beside it, so what is installed can be named later.
            await File.WriteAllTextAsync(Path.Combine(target, "plugin.json"), JsonSerializer.Serialize(manifest, Json), token);
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>The mark on a folder that has been replaced but could not be deleted yet.</summary>
    private const string Retired = ".retired-";

    /// <summary>
    /// Moves an installed copy out of the way so a new one can take its place.
    ///
    /// Deleting it is the obvious thing and does not work: a plugin Post has loaded holds
    /// its own .dll open, and Windows refuses to delete a loaded assembly — which is the
    /// whole reason updating a plugin used to fail with "access is denied". Renaming one is
    /// allowed, so the old copy is renamed and swept up later, once nothing has it open.
    /// </summary>
    private static void Retire(string target)
    {
        if (!Directory.Exists(target)) return;

        try { Directory.Delete(target, true); return; }
        catch (Exception) when (Directory.Exists(target)) { }

        var retired = $"{target}{Retired}{DateTime.UtcNow:yyyyMMddHHmmss}";
        Directory.Move(target, retired);
        TryDelete(retired);
    }

    /// <summary>
    /// Clears out copies replaced by an update whose files were still open at the time.
    /// Called at startup, when the previous run's locks are gone.
    /// </summary>
    public static void SweepRetired()
    {
        foreach (var folder in Directory.EnumerateDirectories(Folder, $"*{Retired}*"))
            TryDelete(folder);
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); } catch { }
    }

    public static bool Remove(string id)
    {
        try
        {
            var folder = FolderFor(id);
            if (!Directory.Exists(folder)) return false;
            // Same reason as an update: a loaded plugin cannot be deleted, only renamed
            // aside and swept up next time Post starts.
            Retire(folder);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fetches a plugin held as loose files. A file naming its way out of the folder is
    /// refused for the same reason an archive entry is: a path like ..\..\Windows is not a
    /// file name, and nothing downloaded gets to decide where it lands.
    ///
    /// One checksum covers all of them together, and it is checked before any of it is
    /// kept: the files land in a staging folder, and a plugin that does not add up is
    /// thrown away there rather than half-installed.
    /// </summary>
    private static async Task FetchFilesAsync(
        PluginManifest manifest, string staging, HttpClient client, IProgress<double>? progress, CancellationToken token)
    {
        var root = Path.GetFullPath(staging + Path.DirectorySeparatorChar);
        var landed = new List<string>();
        var done = 0;

        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(staging, file));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{manifest.Name} lists a file that would be written outside its own folder ({file}), so it has not been installed.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await DownloadAsync(UrlFor(manifest, file), target, client, null, token);
            landed.Add(target);

            progress?.Report((double)++done / manifest.Files.Length);
        }

        var actual = await Sha256Async(manifest.Files, landed, token);
        if (!actual.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"What arrived does not match the checksum {manifest.Name} publishes, so it has not been installed." +
                $"{Environment.NewLine}{Environment.NewLine}expected {manifest.Sha256}{Environment.NewLine}     got {actual}");
    }

    /// <summary>
    /// The checksum to publish for a folder of files: the same calculation installing does,
    /// so whoever puts a plugin in the repository can produce the number without guessing
    /// at how it is worked out.
    /// </summary>
    public static Task<string> ChecksumAsync(string folder, IEnumerable<string> files, CancellationToken token = default)
    {
        var names = files.ToArray();
        var paths = names.Select(name => Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar))).ToList();
        return Sha256Async(names, paths, token);
    }

    /// <summary>
    /// One checksum over a whole plugin: each file's name and then its contents, in the
    /// order the manifest lists them. The name is included so that swapping two files
    /// around, or renaming one, does not go unnoticed.
    /// </summary>
    private static async Task<string> Sha256Async(string[] names, List<string> paths, CancellationToken token)
    {
        using var hash = SHA256.Create();
        var buffer = new byte[81920];

        for (var index = 0; index < paths.Count; index++)
        {
            var label = Encoding.UTF8.GetBytes(names[index].Replace('\\', '/') + "\n");
            hash.TransformBlock(label, 0, label.Length, null, 0);

            await using var stream = File.OpenRead(paths[index]);
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
                hash.TransformBlock(buffer, 0, read, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash ?? []);
    }

    /// <summary>Where one file comes from: what the index says, or the plugin's folder in the repository.</summary>
    private static string UrlFor(PluginManifest manifest, string file)
    {
        if (file.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return file;

        if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
            throw new InvalidOperationException($"{manifest.Name} lists files but nothing says where to fetch them from.");

        return $"{manifest.BaseUrl.TrimEnd('/')}/{file.Replace('\\', '/').TrimStart('/')}";
    }

    private static async Task DownloadAsync(string url, string target, HttpClient client, IProgress<double>? progress, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;

        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(target);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            written += read;
            if (total > 0) progress?.Report(Math.Clamp((double)written / total, 0, 1));
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    /// <summary>
    /// Unpacks an archive, refusing entries that point outside the folder. A zip can name
    /// a path like ..\..\Windows\System32, and unpacking that blindly writes wherever the
    /// archive says rather than where it was told.
    /// </summary>
    private static void ExtractSafely(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The archive tries to write outside its own folder ({entry.FullName}), so it has not been installed.");

            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>An id is a folder name, so it may not wander out of the plugins folder.</summary>
    private static string SafeId(string id)
    {
        var cleaned = new string([.. id.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)]);
        cleaned = cleaned.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "plugin" : cleaned;
    }
}
