using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Colours used by the code-built windows. Secondary text sat at about 3.2:1 against
/// Post's panels when it was plain grey, under the 4.5:1 small text needs, so the hint
/// colour is a lifted slate that still reads as secondary.
/// </summary>
internal static class Theme
{
    public static readonly SolidColorBrush Hint = Freeze(new SolidColorBrush(Color.FromRgb(157, 178, 204)));

    private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }
}
