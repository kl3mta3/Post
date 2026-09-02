using System.Globalization;
using System.Text.Json;
namespace Post.Core;
public sealed class MediaProbeService(FfmpegTools tools, IProcessRunner runner)
{
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".flv", ".m4v"];
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"];
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"];
    public static readonly string[] SupportedExtensions = [.. VideoExtensions, .. AudioExtensions, .. ImageExtensions];
    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        var result = await runner.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format=duration,size:stream=codec_type,codec_name,width,height,r_frame_rate,duration", "-of", "json", path], token);
        result.EnsureSuccess("Media inspection"); using var json = JsonDocument.Parse(result.StandardOutput);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio"); var format = json.RootElement.GetProperty("format");
        if (video.ValueKind == JsonValueKind.Undefined && audio.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The file contains no supported audio, video, or image stream.");
        var duration = ReadNumber(format, "duration") ?? streams.Select(stream => ReadNumber(stream, "duration")).FirstOrDefault(value => value is > 0) ?? (ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? 5 : 0);
        if (duration <= 0) throw new InvalidDataException("The media duration could not be determined.");
        var parts = (video.ValueKind == JsonValueKind.Undefined ? "0/1" : video.GetProperty("r_frame_rate").GetString() ?? "30/1").Split('/');
        var rate = double.Parse(parts[0], CultureInfo.InvariantCulture) / Math.Max(1, double.Parse(parts.ElementAtOrDefault(1) ?? "1", CultureInfo.InvariantCulture));
        var size = format.TryGetProperty("size", out var el) ? long.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture) : new FileInfo(path).Length;
        return new(path, TimeSpan.FromSeconds(duration), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("width").GetInt32(), video.ValueKind == JsonValueKind.Undefined ? 0 : video.GetProperty("height").GetInt32(), rate, video.ValueKind == JsonValueKind.Undefined ? "" : video.GetProperty("codec_name").GetString() ?? "unknown", audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("codec_name").GetString(), size);
    }

    private static double? ReadNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
