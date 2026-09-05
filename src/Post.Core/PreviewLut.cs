namespace Post.Core;

/// <summary>
/// Bakes an effect stack's colour work — corrections and LUTs, in order — into a single
/// lookup table laid out as a strip. The preview shader then needs one texture sample
/// instead of the whole colour pipeline, which is what makes a LUT previewable at all.
/// </summary>
public static class PreviewLut
{
    /// <summary>Lattice size. 17 is enough for a preview and keeps the strip small.</summary>
    public const int DefaultSize = 17;

    /// <summary>True when this effect contributes to the colour table.</summary>
    public static bool IsColorEffect(VideoEffect effect)
        => effect.IsEnabled && effect.Kind is VideoEffectKind.ColorCorrection or VideoEffectKind.Lut;

    public static ColorGrade ToGrade(VideoEffect effect) => new()
    {
        Brightness = effect.Brightness, Contrast = effect.Contrast, Saturation = effect.Saturation,
        Gamma = effect.Gamma, Hue = effect.Hue,
    };

    /// <summary>
    /// Builds a BGRA strip of <paramref name="size"/> slices, each size×size, laid out
    /// left to right by blue. Returns null when the stack has no colour work to do.
    /// </summary>
    public static byte[]? BuildStrip(IEnumerable<VideoEffect> effects, int size = DefaultSize)
        => BuildStrip(effects, null, size);

    /// <summary>
    /// The same, with a grade being worked on laid over the top. That is what makes the
    /// Color Grading panel live: the grade is baked into this strip and sampled by the
    /// shader, using the very maths the export bakes into its .cube.
    /// </summary>
    public static byte[]? BuildStrip(IEnumerable<VideoEffect> effects, ColorGrade? working, int size = DefaultSize)
    {
        var stages = new List<Func<(double R, double G, double B), (double R, double G, double B)>>();
        foreach (var effect in effects)
        {
            if (!IsColorEffect(effect)) continue;
            if (effect.Kind == VideoEffectKind.ColorCorrection)
            {
                var grade = ToGrade(effect);
                if (grade.IsNeutral) continue;
                stages.Add(colour => grade.Apply(colour.R, colour.G, colour.B));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(effect.FilePath) || !File.Exists(effect.FilePath)) continue;
                CubeLut table;
                try { table = CubeLut.Load(effect.FilePath); } catch { continue; }
                stages.Add(colour => table.Sample(colour.R, colour.G, colour.B));
            }
        }
        // Last, so it sits on top of whatever the clip already carries — the same place it
        // would land as a LUT added to the end of the stack.
        if (working is { IsNeutral: false }) stages.Add(colour => working.Apply(colour.R, colour.G, colour.B));

        if (stages.Count == 0) return null;

        var strip = new byte[size * size * size * 4];
        var step = 1d / (size - 1);
        var width = size * size;
        for (var blue = 0; blue < size; blue++)
            for (var green = 0; green < size; green++)
                for (var red = 0; red < size; red++)
                {
                    var colour = (R: red * step, G: green * step, B: blue * step);
                    foreach (var stage in stages) colour = stage(colour);
                    // The slice for this blue value sits at x = blue * size.
                    var index = (green * width + blue * size + red) * 4;
                    strip[index + 0] = Channel(colour.B);
                    strip[index + 1] = Channel(colour.G);
                    strip[index + 2] = Channel(colour.R);
                    strip[index + 3] = 255;
                }
        return strip;
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
