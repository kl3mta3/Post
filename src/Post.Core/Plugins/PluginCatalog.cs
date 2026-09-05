using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Post.Core.Plugins;

/// <summary>
/// The list of plugins available from the repository. Each folder there holds a
/// plugin.json describing one plugin; anything else in the repository is ignored.
///
/// GitHub allows sixty unauthenticated calls an hour from one address and listing costs
/// one call per plugin plus one, so the answer is kept and reused rather than fetched
/// every time the window opens.
/// </summary>
public sealed class PluginCatalog(HttpClient? client = null)
{
    public const string Repository = "kl3mta3/Post-Plugins";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _client = client ?? CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub refuses calls with no user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Post-PluginManager");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static string CachePath => Path.Combine(PluginStore.Folder, "catalog.json");

    /// <summary>Where a person can go and read the repository themselves.</summary>
    public static string RepositoryUrl => $"https://github.com/{Repository}";

    /// <summary>
    /// One entry from GitHub's contents listing. The download link has to be named: the
    /// field is download_url, and matching without regard to case does not bridge the
    /// underscore, so leaving it to convention silently yields null and the shelf reads
    /// as empty.
    /// </summary>
    private sealed record Entry(
        string Name, string Path, string Type,
        [property: JsonPropertyName("download_url")] string? DownloadUrl);
    private sealed record Cached(DateTimeOffset FetchedUtc, PluginManifest[] Plugins);

    /// <summary>
    /// The index at the top of the repository. "addons" is accepted alongside "plugins"
    /// so an index written for one of these shelves reads on the other.
    /// </summary>
    private sealed record IndexFile(PluginManifest[]? Plugins, PluginManifest[]? Addons)
    {
        public PluginManifest[] All => Plugins ?? Addons ?? [];
    }

    /// <summary>
    /// Every plugin the repository offers. A recent answer is reused; a failed call falls
    /// back to whatever was last known rather than showing nothing at all.
    /// </summary>
    public async Task<IReadOnlyList<PluginManifest>> ListAsync(bool refresh = false, CancellationToken token = default)
    {
        if (!refresh && ReadCache() is { } cached && DateTimeOffset.UtcNow - cached.FetchedUtc < CacheLifetime)
            return cached.Plugins;

        try
        {
            var found = await FetchAsync(token);
            WriteCache(found);
            return found;
        }
        catch when (ReadCache() is not null)
        {
            return ReadCache()!.Plugins;   // offline, or out of calls: last known is better than nothing
        }
    }

    private async Task<PluginManifest[]> FetchAsync(CancellationToken token)
    {
        var root = await _client.GetFromJsonAsync<Entry[]>($"https://api.github.com/repos/{Repository}/contents/", Json, token) ?? [];

        // One index at the top is the cheap answer: a whole shelf in a single call, rather
        // than two per plugin against an hourly limit of sixty.
        if (root.FirstOrDefault(item => item.Type == "file" && item.Name.Equals("index.json", StringComparison.OrdinalIgnoreCase))
                is { DownloadUrl: { } indexUrl })
        {
            try
            {
                var index = await _client.GetFromJsonAsync<IndexFile>(indexUrl, Json, token);
                var listed = (index?.All ?? []).Where(item => item.CanBeInstalled).Select(Resolve).ToArray();
                if (listed.Length > 0) return [.. listed.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // A broken index should not hide a repository that can still be walked.
            }
        }

        return await ScanFoldersAsync(root, token);
    }

    /// <summary>
    /// Fills in what an index need not repeat: which folder a plugin sits in, and where
    /// its files are fetched from. An index can say either itself and be believed.
    /// </summary>
    private static PluginManifest Resolve(PluginManifest manifest)
    {
        var folder = string.IsNullOrWhiteSpace(manifest.Folder) ? manifest.Id : manifest.Folder;
        return manifest with
        {
            Folder = folder,
            BaseUrl = string.IsNullOrWhiteSpace(manifest.BaseUrl)
                ? $"https://raw.githubusercontent.com/{Repository}/HEAD/{Uri.EscapeDataString(folder)}"
                : manifest.BaseUrl,
            SourceUrl = $"{RepositoryUrl}/tree/main/{folder}",
        };
    }

    /// <summary>
    /// Walking the repository folder by folder, for a shelf with no index at the top.
    /// </summary>
    private async Task<PluginManifest[]> ScanFoldersAsync(Entry[] root, CancellationToken token)
    {
        var plugins = new List<PluginManifest>();

        foreach (var folder in root.Where(item => item.Type == "dir"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var contents = await _client.GetFromJsonAsync<Entry[]>(
                    $"https://api.github.com/repos/{Repository}/contents/{Uri.EscapeDataString(folder.Path)}", Json, token) ?? [];
                var manifestEntry = contents.FirstOrDefault(item =>
                    item.Type == "file" && item.Name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry?.DownloadUrl is not { } url) continue;

                var manifest = await _client.GetFromJsonAsync<PluginManifest>(url, Json, token);
                if (manifest is null || !manifest.CanBeInstalled) continue;
                plugins.Add(Resolve(manifest with
                {
                    Folder = string.IsNullOrWhiteSpace(manifest.Folder) ? folder.Path : manifest.Folder,
                }));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // One bad folder should not hide the rest of the shelf.
            }
        }
        return [.. plugins.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static Cached? ReadCache()
    {
        try
        {
            return File.Exists(CachePath) ? JsonSerializer.Deserialize<Cached>(File.ReadAllText(CachePath), Json) : null;
        }
        catch { return null; }
    }

    private static void WriteCache(PluginManifest[] plugins)
    {
        try
        {
            Directory.CreateDirectory(PluginStore.Folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new Cached(DateTimeOffset.UtcNow, plugins),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
