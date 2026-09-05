namespace Post.Core;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

public static class FfmpegLocator
{
    /// <summary>
    /// Where Post puts ffmpeg when it fetches its own copy. Beside the settings rather
    /// than beside the program, because an installed app usually sits somewhere the user
    /// cannot write to.
    /// </summary>
    public static string DownloadedToolsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "tools");

    /// <summary>
    /// Finds ffmpeg and ffprobe, or returns null. Every place they might reasonably be is
    /// tried: beside the program, the copy Post downloaded, a couple of other apps known
    /// to ship them, and anywhere on PATH.
    /// </summary>
    public static FfmpegTools? TryFind(string? appDirectory = null)
    {
        appDirectory ??= AppContext.BaseDirectory;
        string[] roots =
        [
            Path.Combine(appDirectory, "tools"),
            appDirectory,
            DownloadedToolsFolder,
            @"C:\Program Files\Ampwin\resources\bin",
            @"C:\Program Files\Krita (x64)\bin",
        ];

        foreach (var root in roots.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var ffmpeg = Path.Combine(root.Trim(), "ffmpeg.exe");
                var ffprobe = Path.Combine(root.Trim(), "ffprobe.exe");
                if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new FfmpegTools(ffmpeg, ffprobe);
            }
            catch { }   // a malformed PATH entry is not worth failing over
        }
        return null;
    }

    public static FfmpegTools Find(string? appDirectory = null)
        => TryFind(appDirectory) ?? throw new FileNotFoundException("FFmpeg and ffprobe were not found. Put both executables in the app's tools folder.");
}
