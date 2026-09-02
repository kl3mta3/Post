using System.Text.Json;

namespace Post.Core;
public sealed record AppSettings
{
    public string DefaultOutputFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    public bool AutoCopyExports { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public double PreviewVolume { get; init; } = .8;
    public string DefaultVideoFormat { get; init; } = "mp4";
    public string DefaultAudioFormat { get; init; } = "mp3";
    public int VideoQualityCrf { get; init; } = 18;
    public int AudioBitrateKbps { get; init; } = 192;
    public string[] RecentProjectPaths { get; init; } = [];
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "settings.json");
    private static string LegacySettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipEdit", "settings.json");
    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
