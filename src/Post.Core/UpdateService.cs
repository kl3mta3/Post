using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Post.Core;
public sealed record UpdateInfo(Version Version, string Name, string DownloadUrl, string FileName, string WebUrl);
public sealed class UpdateService(HttpClient? client = null)
{
    public const string OfficialRepository = "kl3mta3/Post";
    public const string UpdateEndpoint = "https://download.post.lastweeksproject.com";

    /// <summary>
    /// The releases list, which names every asset. The download endpoint above redirects
    /// straight to the installer, so it cannot serve a portable copy: there is nothing
    /// there to choose between.
    /// </summary>
    public const string ReleasesEndpoint = "https://api.github.com/repos/" + OfficialRepository + "/releases/latest";
    private readonly HttpClient _client = client ?? new HttpClient();
    public Task<UpdateInfo?> CheckAsync(string endpoint, CancellationToken token = default)
        => CheckAsync(endpoint, InstallKind.Current, token);

    /// <summary>
    /// Looks for a newer Post, and picks the download that suits how this copy was put
    /// here: the installer for an installed one, the zip for a portable one.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string endpoint, PostInstallKind kind, CancellationToken token = default)
    {
        var wanted = InstallKind.ExtensionFor(kind);
        _client.DefaultRequestHeaders.UserAgent.Clear(); _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Post", "1.0"));
        using var request = CreateHttp2Request(endpoint);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "application/vnd.github+json", StringComparison.OrdinalIgnoreCase))
        {
            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
            if (string.IsNullOrWhiteSpace(fileName)) fileName = Path.GetFileName(response.RequestMessage?.RequestUri?.LocalPath);
            var installerVersion = VersionFromFileName(fileName);
            if (installerVersion is null || installerVersion <= CurrentVersion()) return null;
            // A single file endpoint cannot offer a choice, so it only serves the kind it holds.
            if (!(fileName ?? "").EndsWith(wanted, StringComparison.OrdinalIgnoreCase)) return null;
            return new(installerVersion, $"Post {installerVersion}", endpoint, fileName!, $"https://github.com/{OfficialRepository}/releases/latest");
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token)); var root = json.RootElement;
        var tag = (root.GetProperty("tag_name").GetString() ?? "0").TrimStart('v'); if (!Version.TryParse(tag, out var version)) return null;
        if (version <= CurrentVersion()) return null;
        var assets = root.GetProperty("assets").EnumerateArray();
        var asset = assets.FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined) return null;
        return new(version, root.GetProperty("name").GetString() ?? $"Post {version}", asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("name").GetString()!, root.GetProperty("html_url").GetString()!);
    }
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "Updates"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, update.FileName); using var request = CreateHttp2Request(update.DownloadUrl); using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength; await using var input = await response.Content.ReadAsStreamAsync(token); await using var output = File.Create(path);
        var buffer = new byte[81920]; long readTotal = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), token); readTotal += read; if (total > 0) progress?.Report(readTotal / (double)total); }
        return path;
    }
    private static Version CurrentVersion() => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
    private static HttpRequestMessage CreateHttp2Request(string url) => new(HttpMethod.Get, url) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
    private static Version? VersionFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (var part in Path.GetFileNameWithoutExtension(fileName).Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries).Reverse())
            if (Version.TryParse(part.TrimStart('v', 'V'), out var version)) return version;
        return null;
    }
    /// <summary>
    /// Runs the installer over an installed copy. The switches are Inno Setup's own:
    /// /S is NSIS's, which Inno ignores, so what used to happen was the whole wizard
    /// appearing instead of the silent update it was meant to be.
    /// </summary>
    public static void LaunchInstaller(string path) => Process.Start(new ProcessStartInfo(path,
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
}
