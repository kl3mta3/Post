using System.Globalization;

namespace Post.Core;

/// <summary>
/// Turns opacity keyframes into <c>fade</c> filters where the shape allows it.
/// <c>geq</c> can express any curve but evaluates an expression per pixel per frame,
/// which on a full-frame overlay costs more than everything else in an export put
/// together. Nearly every real fade is a straight ramp in or out, and <c>fade</c>
/// does that for almost nothing.
/// </summary>
public static class OpacityFades
{
    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the fade filters for these keyframes, or null when the curve needs the
    /// general expression path. <paramref name="startSeconds"/> shifts keyframe offsets
    /// onto the timeline, since filters see composition time.
    /// </summary>
    public static List<string>? TryBuild(IReadOnlyList<AnimationKeyframe> keyframes, double startSeconds)
    {
        var points = keyframes.Where(item => item.Property == KeyframeProperty.Opacity)
            .OrderBy(item => item.Offset).ToArray();
        if (points.Length is < 2 or > 4) return null;

        // Only full fades map to the filter: it ramps between clear and opaque.
        static bool IsClear(double value) => value <= .001;
        static bool IsOpaque(double value) => value >= .999;

        var filters = new List<string>();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var from = points[i]; var to = points[i + 1];
            var start = startSeconds + from.Offset.TotalSeconds;
            var duration = (to.Offset - from.Offset).TotalSeconds;

            if (IsClear(from.Value) && IsOpaque(to.Value))
            {
                filters.Add($"fade=t=in:st={S(start)}:d={S(Math.Max(duration, .001))}:alpha=1");
            }
            else if (IsOpaque(from.Value) && IsClear(to.Value))
            {
                filters.Add($"fade=t=out:st={S(start)}:d={S(Math.Max(duration, .001))}:alpha=1");
            }
            else if (IsOpaque(from.Value) && IsOpaque(to.Value))
            {
                // A hold at full opacity between a fade in and a fade out needs no filter.
                continue;
            }
            else return null;   // partial or stepped opacity: leave it to the expression
        }
        return filters.Count > 0 ? filters : null;
    }
}
