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
/// <summary>Which channel of a stereo source a layer plays, centred rather than panned.</summary>
public enum AudioChannelSource { Both = 0, Left = 1, Right = 2 }
// Keep the numeric values stable because Post project JSON stores enums as numbers.
public enum GraphicsOverlayKind { Text = 0, Image = 1, SolidColor = 2, Gradient = 3, Lottie = 4 }
public enum GraphicGradientKind { Linear, Radial }
public enum KeyframeInterpolation { Linear, Discrete, Smooth }
// Keep the numeric values stable because Post project JSON stores enums as numbers: add to
// the end. Lut is the odd one — it holds a file, not a number, and never interpolates.
public enum KeyframeProperty { PositionX, PositionY, Scale, Opacity, Volume, Rotation, Lut }
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

    /// <summary>
    /// The LUT this keyframe switches to, for <see cref="KeyframeProperty.Lut"/>. Null is
    /// no LUT, which is how a clip starts and what it goes back to. Numeric properties
    /// leave it alone.
    /// </summary>
    public string? Text { get; set; }
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
    // Settable so a clip can be pointed at the file's new home without being replaced:
    // every placement, effect and keyframe refers to this object, and survives the relink.
    public required string SourcePath { get; set; }
    public required MediaInfo Media { get; set; }
    /// <summary>True when the source could not be found, so the clip stands in for it.</summary>
    public bool IsOffline { get; set; }
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
    /// <summary>Constant spin in degrees per second; negative turns anticlockwise.</summary>
    /// <summary>
    /// Its sound is not heard, though the clip is still here. Set when the audio has been
    /// split out onto a layer of its own, so the same sound is not played twice.
    /// </summary>
    public bool AudioMuted { get; set; }

    public double SpinDegreesPerSecond { get; set; }
    public TimeSpan AvailableDuration => Clip.SelectedDuration > InPoint ? Clip.SelectedDuration - InPoint : TimeSpan.Zero;
    public TimeSpan Duration => Length is { } length && length < AvailableDuration ? length : AvailableDuration;
    public TimeSpan End => Start + Duration;
}

// Keep the numeric values stable because Post project JSON stores enums as numbers.
public enum TransitionKind
{
    Dissolve = 0, FadeToBlack = 1,
    WipeLeft = 2, WipeRight = 3, WipeUp = 4, WipeDown = 5,
    IrisIn = 6, IrisOut = 7,
    PushLeft = 8, PushRight = 9,
    // Appended, because the numbers are what a saved project stores.
    FadeFromBlack = 10,
}

/// <summary>
/// Where a transition sits relative to its cut, the way Premiere puts it.
///
/// It is not decoration: it decides which side has to supply the spare frames. Centred needs
/// handles on both, starting at the cut needs them only from the outgoing clip, and ending at
/// the cut only from the incoming one — so a cut with nothing spare on one side can still
/// carry a transition by leaning on the other.
/// </summary>
public enum TransitionAlignment { CenterAtCut = 0, StartAtCut = 1, EndAtCut = 2 }

/// <summary>
/// A transition sitting over a cut between two clips.
///
/// It does not move either clip. Both stay where they were put, abutting at the cut, and the
/// transition reaches into the media each of them is not using — past the outgoing clip's
/// out-point, and before the incoming clip's in-point. An edit does not change length, and
/// nothing is trimmed, because a transition is not a reason to throw away somebody's frames.
///
/// What that costs is that the frames have to be there. When they are not, the transition is
/// the thing that gives: it shortens to whatever the media can supply.
/// </summary>
public sealed class ClipTransition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TransitionKind Kind { get; set; } = TransitionKind.Dissolve;

    /// <summary>Where the cut is. The transition is centred on it.</summary>
    public TimeSpan Cut { get; set; }

    public TimeSpan Duration { get; set; } = DefaultDuration;
    public TransitionAlignment Alignment { get; set; } = TransitionAlignment.CenterAtCut;

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(1);

    public TimeSpan Start => Alignment switch
    {
        TransitionAlignment.StartAtCut => Cut,
        TransitionAlignment.EndAtCut => Cut - Duration,
        _ => Cut - TimeSpan.FromTicks(Duration.Ticks / 2),
    };

    public TimeSpan End => Start + Duration;

    /// <summary>How far through, 0 to 1, at a point in project time.</summary>
    public double Progress(TimeSpan at) => Duration <= TimeSpan.Zero
        ? 1
        : Math.Clamp((at - Start).TotalSeconds / Duration.TotalSeconds, 0, 1);
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
    /// <summary>
    /// One channel of the source, played centred. Splitting a stereo layer gives two
    /// layers set this way, so each side is usable on its own rather than stuck in one
    /// ear. This is not the same as the L and R mute buttons, which pan.
    /// </summary>
    public AudioChannelSource ChannelSource { get; set; } = AudioChannelSource.Both;
    /// <summary>
    /// The point everything on this layer turns and scales about, as a fraction of the
    /// item's own box. The middle is 0.5, 0.5; a corner is 0 or 1.
    /// </summary>
    public double AnchorX { get; set; } = .5;
    public double AnchorY { get; set; } = .5;
    public TimelineLayerKind Kind { get; set; }
    public ObservableCollection<TimelinePlacement> Placements { get; } = [];
    public ObservableCollection<GraphicsOverlay> Graphics { get; } = [];
    public ObservableCollection<ClipTransition> Transitions { get; } = [];
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
    /// <summary>Text: white, and solid.</summary>
    public string Foreground { get; set; } = "#FFFFFFFF";

    /// <summary>
    /// Background: white, and invisible. Transparent black looks the same — nothing — but
    /// opens the picker on a colour with no brightness left in it, so raising the opacity
    /// gives a black plate and the brightness has to be found and raised as well. Starting
    /// from white means the one slider that was reached for does what it looks like it will.
    /// </summary>
    public string Background { get; set; } = "#00FFFFFF";
    public string FillColor1 { get; set; } = "#FFFFFFFF";
    public string FillColor2 { get; set; } = "#FF000000";
    public bool UseSecondFillColor { get; set; } = true;
    public GraphicGradientKind GradientKind { get; set; } = GraphicGradientKind.Linear;
    public double GradientAngle { get; set; }
    public double Opacity { get; set; } = 1;

    /// <summary>
    /// How round the background's corners are, from 0 for square to 1, where the radius is
    /// half the shorter side — a pill, or a circle on a square box. A fraction rather than
    /// a number of pixels, so a box keeps its shape when it is resized.
    /// </summary>
    public double CornerRadius { get; set; }

    /// <summary>The radius in pixels for a box of this size.</summary>
    public double CornerRadiusFor(double width, double height) =>
        Math.Clamp(CornerRadius, 0, 1) * Math.Min(width, height) / 2;

    public bool PreserveAspectRatio { get; set; } = true;
    public double X { get; set; } = .35;
    public double Y { get; set; } = .4;
    public double Width { get; set; } = .3;
    public double Height { get; set; } = .15;
    public TimeSpan Start { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);
    public ObservableCollection<AnimationKeyframe> Keyframes { get; } = [];
    /// <summary>Constant spin in degrees per second; negative turns anticlockwise.</summary>
    public double SpinDegreesPerSecond { get; set; }
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
