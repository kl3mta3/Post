using System.Collections.ObjectModel;

namespace Post.Core;

public sealed record MediaInfo(string Path, TimeSpan Duration, int Width, int Height, double FrameRate,
    string VideoCodec, string? AudioCodec, long SizeBytes)
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma"];
    public bool IsStillImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
    public bool IsAudioOnly => AudioExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
    public bool HasVideo => !IsStillImage && !IsAudioOnly && Width > 0 && Height > 0 && !string.IsNullOrWhiteSpace(VideoCodec);
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioCodec);
    public string Resolution => IsStillImage ? $"Image • {Width}×{Height}" : HasVideo ? $"{Width}×{Height}" : "Audio";
}

public sealed record CutRange(TimeSpan Start, TimeSpan End) { public TimeSpan Duration => End - Start; }
public enum ExportMode { Lossless, Discord20Mb, Discord10Mb, CustomSize, Gif }
public enum AspectPreset { Original, Landscape16x9, Vertical9x16, Square1x1, Portrait4x5, Standard4x3, Cinema21x9 }
// Keep the original numeric values stable because Post project JSON stores enums as numbers.
public enum TimelineLayerKind { Video = 0, Graphics = 1, Audio = 2 }
// Keep the numeric values stable because Post project JSON stores enums as numbers.
public enum GraphicsOverlayKind { Text = 0, Image = 1, SolidColor = 2, Gradient = 3, Lottie = 4 }
public enum GraphicGradientKind { Linear, Radial }
public enum KeyframeInterpolation { Linear, Discrete, Smooth }
public enum KeyframeProperty { PositionX, PositionY, Scale, Opacity, Volume }
// Keep the numeric values stable because Post project JSON stores enums as numbers.
public enum VideoEffectKind { Vignette = 0, Blur = 1, Sharpen = 2, ColorCorrection = 3, Lut = 4 }

/// <summary>
/// One entry in a clip's (or the whole timeline's) effect stack. A single flat
/// property bag covers every kind, matching how <see cref="GraphicsOverlay"/> works.
/// </summary>
public sealed class VideoEffect
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public VideoEffectKind Kind { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>Generic strength, 0–1: vignette falloff, blur radius, sharpen amount.</summary>
    public double Amount { get; set; } = .5;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public double Gamma { get; set; } = 1;
    public double Hue { get; set; }
    /// <summary>Colour lookup table (.cube) for <see cref="VideoEffectKind.Lut"/>.</summary>
    public string? FilePath { get; set; }

    public string DisplayName => Kind switch
    {
        VideoEffectKind.Vignette => "Vignette",
        VideoEffectKind.Blur => "Blur",
        VideoEffectKind.Sharpen => "Sharpen",
        VideoEffectKind.ColorCorrection => "Color correction",
        _ => "LUT",
    };

    public string Summary => Kind switch
    {
        VideoEffectKind.ColorCorrection => $"bright {Brightness:0.##}, contrast {Contrast:0.##}, saturation {Saturation:0.##}, gamma {Gamma:0.##}, hue {Hue:0}°",
        VideoEffectKind.Lut => FilePath is null ? "no file chosen" : Path.GetFileName(FilePath),
        _ => $"amount {Amount * 100:0}%",
    };

    public VideoEffect Clone() => new()
    {
        Id = Id, Kind = Kind, IsEnabled = IsEnabled, Amount = Amount, Brightness = Brightness,
        Contrast = Contrast, Saturation = Saturation, Gamma = Gamma, Hue = Hue, FilePath = FilePath,
    };

    /// <summary>Takes every value except the identity, so edits can be applied in place.</summary>
    public void CopyFrom(VideoEffect other)
    {
        Kind = other.Kind; IsEnabled = other.IsEnabled; Amount = other.Amount; Brightness = other.Brightness;
        Contrast = other.Contrast; Saturation = other.Saturation; Gamma = other.Gamma; Hue = other.Hue; FilePath = other.FilePath;
    }
}

public sealed class AnimationKeyframe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public KeyframeProperty Property { get; set; }
    public TimeSpan Offset { get; set; }
    public double Value { get; set; }
    public KeyframeInterpolation Interpolation { get; set; } = KeyframeInterpolation.Linear;
}

public sealed class MediaSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TimeSpan SourceStart { get; set; }
    public TimeSpan SourceEnd { get; set; }
    public TimeSpan Duration => SourceEnd - SourceStart;
}

public sealed class ClipItem
{
    public required string SourcePath { get; init; }
    public required MediaInfo Media { get; init; }
    public string DisplayName => Path.GetFileName(SourcePath);
    public TimeSpan? PendingCutStart { get; set; }
    public TimeSpan? PendingCutEnd { get; set; }
    public ObservableCollection<MediaSegment> Segments { get; } = [];
    public string? PreviewPath { get; set; }
    public TimeSpan SelectedDuration => TimeSpan.FromTicks(Segments.Sum(s => s.Duration.Ticks));
    public ClipSnapshot Snapshot() => new(PendingCutStart, PendingCutEnd, Segments.Select(s => new MediaSegment { Id = s.Id, SourceStart = s.SourceStart, SourceEnd = s.SourceEnd }).ToArray());
    public void Restore(ClipSnapshot value)
    {
        PendingCutStart = value.PendingCutStart; PendingCutEnd = value.PendingCutEnd;
        Segments.Clear(); foreach (var seg in value.Segments) Segments.Add(seg);
    }
}

public sealed class TimelinePlacement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ClipItem Clip { get; init; }
    public TimeSpan Start { get; set; }
    public TimeSpan InPoint { get; set; }
    public TimeSpan? Length { get; set; }
    public ObservableCollection<AnimationKeyframe> Keyframes { get; } = [];
    public ObservableCollection<VideoEffect> Effects { get; } = [];
    public TimeSpan AvailableDuration => Clip.SelectedDuration > InPoint ? Clip.SelectedDuration - InPoint : TimeSpan.Zero;
    public TimeSpan Duration => Length is { } length && length < AvailableDuration ? length : AvailableDuration;
    public TimeSpan End => Start + Duration;
}

public sealed class TimelineLayer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public bool IsVisible { get; set; } = true;
    public bool IsMuted { get; set; }
    public bool MuteLeftChannel { get; set; }
    public bool MuteRightChannel { get; set; }
    /// <summary>How loud this layer sits in the mix, 0 to 2, multiplying its clips' own volume.</summary>
    public double Volume { get; set; } = 1;
    public TimelineLayerKind Kind { get; set; }
    public ObservableCollection<TimelinePlacement> Placements { get; } = [];
    public ObservableCollection<GraphicsOverlay> Graphics { get; } = [];
}

public sealed class GraphicsOverlay
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public GraphicsOverlayKind Kind { get; set; } = GraphicsOverlayKind.Text;
    public string Text { get; set; } = "Text";
    public string? ImagePath { get; set; }
    public string? RenderedImagePath { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 72;
    public string Foreground { get; set; } = "#FFFFFFFF";
    public string Background { get; set; } = "#00000000";
    public string FillColor1 { get; set; } = "#FFFFFFFF";
    public string FillColor2 { get; set; } = "#FF000000";
    public bool UseSecondFillColor { get; set; } = true;
    public GraphicGradientKind GradientKind { get; set; } = GraphicGradientKind.Linear;
    public double GradientAngle { get; set; }
    public double Opacity { get; set; } = 1;
    public bool PreserveAspectRatio { get; set; } = true;
    public double X { get; set; } = .35;
    public double Y { get; set; } = .4;
    public double Width { get; set; } = .3;
    public double Height { get; set; } = .15;
    public TimeSpan Start { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);
    public ObservableCollection<AnimationKeyframe> Keyframes { get; } = [];
    public TimeSpan End => Start + Duration;
}

public sealed class TimelineComposition
{
    public ObservableCollection<TimelineLayer> Layers { get; } = [];
    /// <summary>Effects applied to the finished frame, after every layer is composited.</summary>
    public ObservableCollection<VideoEffect> OutputEffects { get; } = [];
    /// <summary>Equalizer applied to the mixed audio of the whole timeline.</summary>
    public AudioEqualizer Equalizer { get; } = AudioEqualizer.Flat();
    public TimeSpan WorkspaceDuration { get; set; } = TimeSpan.FromMinutes(1);
    public bool RenderWorkspaceTailAsBlack { get; set; }
    public TimeSpan ContentDuration => Layers
        .Where(layer => layer.IsVisible)
        .SelectMany(layer => layer.Placements.Select(placement => placement.End).Concat(layer.Graphics.Select(graphic => graphic.End)))
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();
    public TimeSpan OutputDuration => RenderWorkspaceTailAsBlack
        ? (WorkspaceDuration > ContentDuration ? WorkspaceDuration : ContentDuration)
        : ContentDuration;
    public TimeSpan DisplayDuration => WorkspaceDuration > ContentDuration ? WorkspaceDuration : ContentDuration;
    public bool HasVisibleMedia => Layers.Any(layer => layer.IsVisible && layer.Placements.Count > 0);
    public bool HasVisibleGraphics => Layers.Any(layer => layer.IsVisible && layer.Graphics.Count > 0);
}

public sealed record ClipSnapshot(TimeSpan? PendingCutStart, TimeSpan? PendingCutEnd, MediaSegment[] Segments);
public sealed record ExportOptions
{
    public ExportMode Mode { get; init; } = ExportMode.Lossless;
    public AspectPreset Aspect { get; init; } = AspectPreset.Original;
    public double Speed { get; init; } = 1;
    public double Volume { get; init; } = 1;
    public int CustomSizeMb { get; init; } = 25;
    public double CropZoom { get; init; } = 1;
    public double PanX { get; init; }
    public double PanY { get; init; }
    public bool CopyToClipboard { get; init; } = true;
    public bool ReplaceOriginal { get; init; }
    public int VideoQualityCrf { get; init; } = 18;
    public int AudioBitrateKbps { get; init; } = 192;
    /// <summary>
    /// Effects for exports that have no timeline of their own (single clip, batch, montage).
    /// Timeline exports carry their effects on the placements and the composition instead.
    /// </summary>
    public IReadOnlyList<VideoEffect> Effects { get; init; } = [];
    /// <summary>Equalizer for exports with no timeline of their own.</summary>
    public AudioEqualizer? Equalizer { get; init; }
}
public sealed record ExportProgress(double Fraction, string Stage);
public static class TimeText
{
    public static string Format(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss\.fff") : value.ToString(@"mm\:ss\.fff");
}
