namespace Post.Core;

/// <summary>One of the ready-made looks offered in the effects browser.</summary>
public sealed record LookStyle(string Name, string Description, ColorGrade Grade);

/// <summary>
/// Ready-made grades. They are generated to .cube files on demand rather than shipped
/// as assets, so applying one produces an ordinary LUT effect that renders and exports
/// like any other, and can be opened in the grading window afterwards.
/// </summary>
public static class LookStyles
{
    public static IReadOnlyList<LookStyle> All { get; } =
    [
        new("Cinematic Teal & Orange", "Cool shadows against warm highlights, the standard film look.",
            new ColorGrade { Contrast = 1.15, Saturation = 1.1, ShadowTone = -.7, HighlightTone = .6 }),
        new("Warm Sunset", "Golden-hour warmth with a gentle lift in the midtones.",
            new ColorGrade { Temperature = .45, Saturation = 1.12, Gamma = 1.05, HighlightTone = .35 }),
        new("Cool Moonlight", "Night-time blue with deeper contrast.",
            new ColorGrade { Temperature = -.5, Contrast = 1.12, Gamma = .95, Saturation = .9, ShadowTone = -.4 }),
        new("Vintage Film", "Faded blacks, softer contrast and a warm print tone.",
            new ColorGrade { Lift = .08, Contrast = .92, Saturation = .85, HighlightTone = .3, Temperature = .15 }),
        new("Black & White", "Neutral monochrome with a slight contrast lift.",
            new ColorGrade { Saturation = 0, Contrast = 1.12 }),
        new("High Contrast Punch", "Crisper blacks and stronger colour for social clips.",
            new ColorGrade { Contrast = 1.35, Saturation = 1.18 }),
        new("Bleach Bypass", "Desaturated and harsh, the silver-retention look.",
            new ColorGrade { Saturation = .35, Contrast = 1.3, Gamma = 1.08 }),
        new("Faded Matte", "Lifted blacks and muted colour for a soft, matte finish.",
            new ColorGrade { Lift = .12, Saturation = .8, Contrast = .95 }),
    ];

    public static LookStyle? Find(string name) => All.FirstOrDefault(style => style.Name == name);

    /// <summary>
    /// Writes a style to a .cube in the given folder, reusing the file when it is
    /// already there so repeated use of a look does not pile up copies.
    /// </summary>
    public static string EnsureCube(LookStyle style, string folder, int size = 33)
    {
        var safe = string.Concat(style.Name.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
        var path = Path.Combine(folder, $"style-{safe}-{size}.cube");
        if (!File.Exists(path)) style.Grade.SaveCube(path, style.Name, size);
        return path;
    }
}
