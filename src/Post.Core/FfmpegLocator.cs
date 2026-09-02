namespace Post.Core;
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);
public static class FfmpegLocator
{
    public static FfmpegTools Find(string? appDirectory = null)
    {
        appDirectory ??= AppContext.BaseDirectory;
        var roots = new[] { Path.Combine(appDirectory, "tools"), appDirectory, @"C:\Program Files\Ampwin\resources\bin", @"C:\Program Files\Krita (x64)\bin" };
        foreach (var root in roots.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            var ffmpeg = Path.Combine(root.Trim(), "ffmpeg.exe"); var ffprobe = Path.Combine(root.Trim(), "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new(ffmpeg, ffprobe);
        }
        throw new FileNotFoundException("FFmpeg and ffprobe were not found. Put both executables in the app's tools folder.");
    }
}
