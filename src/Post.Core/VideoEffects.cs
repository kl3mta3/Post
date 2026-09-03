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
    /// Filter options are split on ':' and filters on ',', and a Windows drive letter
    /// contains a colon, so the path has to be escaped for the filtergraph parser.
    /// </summary>
    public static string EscapePath(string path)
        => $"'{path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''")}'";
}
