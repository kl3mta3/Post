using System.Text.Json;

namespace Post.Core;

public sealed record PostProjectDocument(int Version, string Name, double WorkspaceSeconds, bool RenderWorkspaceTailAsBlack, ProjectClipDocument[] Clips, ProjectLayerDocument[] Layers,
    ProjectEffectDocument[]? OutputEffects = null, ProjectEqualizerDocument? Equalizer = null);
public sealed record ProjectEqualizerDocument(bool IsEnabled, double GainDb, ProjectEqualizerBandDocument[] Bands);
public sealed record ProjectEqualizerBandDocument(double FrequencyHz, double GainDb, double Width);
public sealed record ProjectClipDocument(string SourcePath, ProjectSegmentDocument[] Segments);
public sealed record ProjectSegmentDocument(double StartSeconds, double EndSeconds);
public sealed record ProjectLayerDocument(string Name, bool IsVisible, bool IsMuted, ProjectPlacementDocument[] Placements,
    TimelineLayerKind Kind = TimelineLayerKind.Video, ProjectGraphicsDocument[]? Graphics = null,
    bool MuteLeftChannel = false, bool MuteRightChannel = false, double Volume = 1);
public sealed record ProjectPlacementDocument(int ClipIndex, double StartSeconds, double InSeconds = 0, double? DurationSeconds = null,
    ProjectKeyframeDocument[]? Keyframes = null, ProjectEffectDocument[]? Effects = null);
public sealed record ProjectEffectDocument(VideoEffectKind Kind, bool IsEnabled = true, double Amount = .5,
    double Brightness = 0, double Contrast = 1, double Saturation = 1, double Gamma = 1, double Hue = 0, string? FilePath = null);
public sealed record ProjectGraphicsDocument(GraphicsOverlayKind Kind, string Text, string? ImagePath, string FontFamily,
    double FontSize, string Foreground, string Background, double Opacity, bool PreserveAspectRatio,
    double X, double Y, double Width, double Height, double StartSeconds, double DurationSeconds,
    ProjectKeyframeDocument[]? Keyframes = null, string FillColor1 = "#FFFFFFFF", string FillColor2 = "#FF000000",
    bool UseSecondFillColor = true, GraphicGradientKind GradientKind = GraphicGradientKind.Linear, double GradientAngle = 0);
public sealed record ProjectKeyframeDocument(KeyframeProperty Property, double OffsetSeconds, double Value,
    KeyframeInterpolation Interpolation = KeyframeInterpolation.Linear);

public static class PostProjectStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task SaveAsync(string path, PostProjectDocument project, CancellationToken token = default)
    {
        if (!Path.GetExtension(path).Equals(".post", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(path).Equals(".clipedit", StringComparison.OrdinalIgnoreCase)) path += ".post";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(project, Options), token);
    }

    public static async Task<PostProjectDocument> LoadAsync(string path, CancellationToken token = default)
    {
        var project = JsonSerializer.Deserialize<PostProjectDocument>(await File.ReadAllTextAsync(path, token), Options)
            ?? throw new InvalidDataException("The Post project file is empty or invalid.");
        if (project.Version != 1) throw new InvalidDataException($"Unsupported Post project version: {project.Version}.");
        return project;
    }
}
