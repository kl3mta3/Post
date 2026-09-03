using System.Globalization;
using System.Text;

namespace Post.Core;

/// <summary>
/// A colour grade, evaluated in C# so the same maths can drive both the on-screen
/// preview and an exported .cube LUT. Every value is neutral at its default.
/// </summary>
public sealed record ColorGrade
{
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1;
    public double Saturation { get; init; } = 1;
    public double Gamma { get; init; } = 1;
    /// <summary>Hue rotation in degrees.</summary>
    public double Hue { get; init; }
    /// <summary>-1 cool to +1 warm.</summary>
    public double Temperature { get; init; }
    /// <summary>-1 green to +1 magenta.</summary>
    public double Tint { get; init; }
    public double GainRed { get; init; } = 1;
    public double GainGreen { get; init; } = 1;
    public double GainBlue { get; init; } = 1;
    /// <summary>Raises the blacks, for a faded or filmic look.</summary>
    public double Lift { get; init; }
    /// <summary>Split toning: -1 pushes shadows cool, +1 warm.</summary>
    public double ShadowTone { get; init; }
    /// <summary>Split toning: -1 pushes highlights cool, +1 warm.</summary>
    public double HighlightTone { get; init; }

    public bool IsNeutral =>
        Math.Abs(Brightness) < .001 && Math.Abs(Contrast - 1) < .001 && Math.Abs(Saturation - 1) < .001 &&
        Math.Abs(Gamma - 1) < .001 && Math.Abs(Hue) < .01 && Math.Abs(Temperature) < .001 && Math.Abs(Tint) < .001 &&
        Math.Abs(GainRed - 1) < .001 && Math.Abs(GainGreen - 1) < .001 && Math.Abs(GainBlue - 1) < .001 &&
        Math.Abs(Lift) < .001 && Math.Abs(ShadowTone) < .001 && Math.Abs(HighlightTone) < .001;

    /// <summary>Applies the grade to one linear 0–1 RGB triple.</summary>
    public (double R, double G, double B) Apply(double r, double g, double b)
    {
        // White balance first, the way a camera would have captured it.
        var temperature = Math.Clamp(Temperature, -1, 1); var tint = Math.Clamp(Tint, -1, 1);
        r *= 1 + .3 * temperature; b *= 1 - .3 * temperature;
        g *= 1 - .2 * tint; r *= 1 + .1 * tint; b *= 1 + .1 * tint;
        r *= Math.Clamp(GainRed, 0, 4); g *= Math.Clamp(GainGreen, 0, 4); b *= Math.Clamp(GainBlue, 0, 4);

        var brightness = Math.Clamp(Brightness, -1, 1);
        r += brightness; g += brightness; b += brightness;

        var contrast = Math.Clamp(Contrast, 0, 3);
        r = (r - .5) * contrast + .5; g = (g - .5) * contrast + .5; b = (b - .5) * contrast + .5;

        var saturation = Math.Clamp(Saturation, 0, 3);
        var luma = .2126 * r + .7152 * g + .0722 * b;
        r = luma + (r - luma) * saturation; g = luma + (g - luma) * saturation; b = luma + (b - luma) * saturation;

        var hue = Math.Clamp(Hue, -180, 180);
        if (Math.Abs(hue) > .01)
        {
            // Rotate around the luma axis in YIQ, which is what ffmpeg's hue filter does.
            var angle = hue * Math.PI / 180; var cos = Math.Cos(angle); var sin = Math.Sin(angle);
            var y = .299 * r + .587 * g + .114 * b;
            var i = .596 * r - .274 * g - .322 * b;
            var q = .211 * r - .523 * g + .312 * b;
            var ri = i * cos - q * sin; var rq = i * sin + q * cos;
            r = y + .956 * ri + .621 * rq; g = y - .272 * ri - .647 * rq; b = y - 1.106 * ri + 1.703 * rq;
        }

        // Split toning: warm or cool the ends of the range independently, weighted by
        // luma, which is what gives looks like teal shadows against orange highlights.
        var shadowTone = Math.Clamp(ShadowTone, -1, 1); var highlightTone = Math.Clamp(HighlightTone, -1, 1);
        if (Math.Abs(shadowTone) > .001 || Math.Abs(highlightTone) > .001)
        {
            var weight = Math.Clamp(.2126 * r + .7152 * g + .0722 * b, 0, 1);
            var tone = shadowTone * (1 - weight) + highlightTone * weight;
            r += tone * .18; b -= tone * .18;
        }

        var lift = Math.Clamp(Lift, 0, 1);
        if (lift > .001) { r = lift + r * (1 - lift); g = lift + g * (1 - lift); b = lift + b * (1 - lift); }

        var gamma = Math.Clamp(Gamma, .1, 3);
        if (Math.Abs(gamma - 1) > .001)
        {
            r = Math.Pow(Math.Clamp(r, 0, 1), 1 / gamma); g = Math.Pow(Math.Clamp(g, 0, 1), 1 / gamma); b = Math.Pow(Math.Clamp(b, 0, 1), 1 / gamma);
        }
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    /// <summary>Writes the grade as a .cube LUT. Red varies fastest, as the format requires.</summary>
    public void SaveCube(string path, string title = "Post grade", int size = 33)
    {
        size = Math.Clamp(size, 2, 64);
        var text = new StringBuilder();
        text.AppendLine($"TITLE \"{title.Replace('"', '\'')}\"");
        text.AppendLine($"LUT_3D_SIZE {size.ToString(CultureInfo.InvariantCulture)}");
        text.AppendLine("DOMAIN_MIN 0.0 0.0 0.0");
        text.AppendLine("DOMAIN_MAX 1.0 1.0 1.0");
        var step = 1d / (size - 1);
        for (var blue = 0; blue < size; blue++)
            for (var green = 0; green < size; green++)
                for (var red = 0; red < size; red++)
                {
                    var (r, g, b) = Apply(red * step, green * step, blue * step);
                    text.AppendLine($"{Number(r)} {Number(g)} {Number(b)}");
                }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text.ToString());
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
