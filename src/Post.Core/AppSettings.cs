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
    /// <summary>Which H.264 encoder exports use: "auto", "cpu", or an encoder name.</summary>
    public string VideoEncoder { get; init; } = "auto";
    /// <summary>
    /// Copy imported media into the project's own folder. Off by default: a project
    /// references its media, so this trades disk and import time for a self-contained
    /// folder, and edits to the original stop reaching the project.
    /// </summary>
    public bool CopyMediaOnImport { get; init; }
    /// <summary>Where those copies go, chosen when the setting is switched on.</summary>
    public string MediaCopyFolder { get; init; } = "";
    /// <summary>Whether to offer that choice on the first import of a project.</summary>
    public bool AskAboutCopyOnImport { get; init; } = true;
    public string[] RecentProjectPaths { get; init; } = [];
    /// <summary>Lottie animations imported in the Animations window.</summary>
    public string[] AnimationPaths { get; init; } = [];
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
