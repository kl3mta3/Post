using System.IO.Compression;
using System.Security.Cryptography;

namespace Post.Core;

/// <summary>
/// Fetching ffmpeg when the machine has none.
///
/// Post cannot read, play or export a single frame without it, so an install that lacks
/// it is not a degraded Post, it is a dead one. Rather than saying so and stopping, this
/// downloads the build ffmpeg.org points Windows users at, checks it against the checksum
/// published beside it, and keeps the two executables it actually needs.
/// </summary>
public static class FfmpegInstaller
{
    /// <summary>The essentials build: ffmpeg, ffprobe and ffplay, without the extras.</summary>
    private const string ArchiveUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string ChecksumUrl = ArchiveUrl + ".sha256";
    private const string VersionUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";

    public static string SourceDescription => "gyan.dev, one of the two Windows builds ffmpeg.org links to";
    public static string SourceUrl => ArchiveUrl;

    /// <summary>What is about to be downloaded, when that can be found out cheaply.</summary>
    public static async Task<string?> LatestVersionAsync(HttpClient client, CancellationToken token = default)
    {
        try { return (await client.GetStringAsync(VersionUrl, token)).Trim(); }
        catch { return null; }
    }

    /// <summary>
    /// Downloads ffmpeg, verifies it, and puts ffmpeg.exe and ffprobe.exe where Post looks.
    /// Nothing is unpacked until the checksum matches, and the folder is only replaced once
    /// the new copy is in hand.
    /// </summary>
    public static async Task<FfmpegTools> InstallAsync(HttpClient client, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var archive = Path.Combine(Path.GetTempPath(), $"post-ffmpeg-{Guid.NewGuid():N}.zip");
        var staging = Path.Combine(Path.GetTempPath(), $"post-ffmpeg-{Guid.NewGuid():N}");
        try
        {
            var expected = (await client.GetStringAsync(ChecksumUrl, token)).Trim().Split(' ', '\t')[0];
            await DownloadAsync(ArchiveUrl, archive, client, progress, token);

            var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(archive), token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The ffmpeg download does not match the checksum published with it, so it has not been installed." +
                    $"{Environment.NewLine}{Environment.NewLine}expected {expected}{Environment.NewLine}     got {actual}");

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging);

            // The build nests everything under one folder; only two files are wanted.
            var target = FfmpegLocator.DownloadedToolsFolder;
            Directory.CreateDirectory(target);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var found = Directory.EnumerateFiles(staging, name, SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException($"The download did not contain {name}.");
                File.Copy(found, Path.Combine(target, name), overwrite: true);
            }

            return FfmpegLocator.TryFind()
                ?? throw new InvalidOperationException("ffmpeg was unpacked but still cannot be found.");
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
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
}
