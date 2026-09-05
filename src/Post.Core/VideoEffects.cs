using System.Globalization;

namespace Post.Core;

/// <summary>Turns an effect stack into ffmpeg filter chain entries.</summary>
public static class VideoEffects
{
    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static bool HasAny(IEnumerable<VideoEffect>? effects) => Build(effects).Count > 0;

    /// <summary>Builds the filters for an effect stack, in order, skipping no-ops.</summary>
    public static List<string> Build(IEnumerable<VideoEffect>? effects)
    {
        var filters = new List<string>();
        if (effects is null) return filters;
        foreach (var effect in effects)
        {
            if (!effect.IsEnabled) continue;
            var amount = Math.Clamp(effect.Amount, 0, 1);
            switch (effect.Kind)
            {
                // The vignette angle is the lens aperture in radians: wider is darker.
                case VideoEffectKind.Vignette when amount > .01:
                    filters.Add($"vignette=a={S(amount * 1.2)}");
                    break;
                case VideoEffectKind.Blur when amount > .01:
                    filters.Add($"gblur=sigma={S(amount * 25)}:steps=2");
                    break;
                case VideoEffectKind.Sharpen when amount > .01:
                    filters.Add($"unsharp=5:5:{S(amount * 2)}:5:5:0");
                    break;
                case VideoEffectKind.ColorCorrection:
                    var brightness = Math.Clamp(effect.Brightness, -1, 1);
                    var contrast = Math.Clamp(effect.Contrast, 0, 3);
                    var saturation = Math.Clamp(effect.Saturation, 0, 3);
                    var gamma = Math.Clamp(effect.Gamma, .1, 3);
                    if (Math.Abs(brightness) > .001 || Math.Abs(contrast - 1) > .001 || Math.Abs(saturation - 1) > .001 || Math.Abs(gamma - 1) > .001)
                        filters.Add($"eq=brightness={S(brightness)}:contrast={S(contrast)}:saturation={S(saturation)}:gamma={S(gamma)}");
                    var hue = Math.Clamp(effect.Hue, -180, 180);
                    if (Math.Abs(hue) > .01) filters.Add($"hue=h={S(hue)}");
                    break;
                case VideoEffectKind.Lut when !string.IsNullOrWhiteSpace(effect.FilePath) && File.Exists(effect.FilePath):
                    filters.Add($"lut3d=file={EscapePath(effect.FilePath!)}");
                    break;
            }
        }
        return filters;
    }

    /// <summary>
    /// The LUTs a clip's keyframes switch between, as one filter per stretch.
    ///
    /// lut3d takes ffmpeg's enable=, so each one only touches the frames in its own window
    /// and passes everything else straight through — which is how a lookup table can change
    /// partway through a clip without cutting the clip up.
    ///
    /// <paramref name="offsetSeconds"/> is where the clip sits on the timeline: by this
    /// point in the graph the stream has been shifted to project time, so the windows have
    /// to be too.
    /// </summary>
    public static List<string> BuildLutKeyframes(
        IEnumerable<AnimationKeyframe>? keyframes, TimeSpan duration, double offsetSeconds = 0)
    {
        var filters = new List<string>();
        if (keyframes is null) return filters;

        foreach (var (start, end, lut) in KeyframeEvaluator.TextSpans(keyframes, KeyframeProperty.Lut, duration))
        {
            if (!File.Exists(lut)) continue;
            filters.Add($"lut3d=file={EscapePath(lut)}:enable='between(t,{S(start.TotalSeconds + offsetSeconds)},{S(end.TotalSeconds + offsetSeconds)})'");
        }
        return filters;
    }

    /// <summary>
    /// Filter options are split on ':' and filters on ',', and a Windows drive letter
    /// contains a colon, so the path has to be escaped for the filtergraph parser.
    /// </summary>
    public static string EscapePath(string path)
        => $"'{path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''")}'";
}
