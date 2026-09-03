using System.Globalization;

namespace Post.Core;

/// <summary>
/// Turns an item's spin and rotation keyframes into the filters that carry them out.
///
/// ffmpeg's rotate filter always turns a frame about its own centre, so rotating about
/// an anchor means padding the picture until the anchor <em>is</em> the centre, turning
/// it, and then shifting the overlay back by however much padding went on the top and
/// left. That is what <see cref="Plan"/> works out.
/// </summary>
public static class Rotation
{
    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// The filters to insert before the overlay, and the offset the overlay position has
    /// to be shifted by to keep the anchor where it was.
    /// </summary>
    public readonly record struct Plan(IReadOnlyList<string> Filters, int OffsetX, int OffsetY, int Width, int Height)
    {
        public static Plan None(int width, int height) => new([], 0, 0, width, height);
        public bool IsEmpty => Filters.Count == 0;
    }

    /// <summary>True when this item turns at all, and so needs the padding treatment.</summary>
    public static bool Turns(IEnumerable<AnimationKeyframe> keyframes, double spinDegreesPerSecond)
        => Math.Abs(spinDegreesPerSecond) > .0001 || keyframes.Any(item => item.Property == KeyframeProperty.Rotation);

    /// <summary>
    /// The angle in radians as an ffmpeg expression, combining a constant spin with any
    /// rotation keyframes. Both are in degrees the way the editor states them.
    /// </summary>
    public static string AngleExpression(IReadOnlyList<AnimationKeyframe> keyframes, double spinDegreesPerSecond, double startSeconds)
    {
        var parts = new List<string>();
        if (Math.Abs(spinDegreesPerSecond) > .0001) parts.Add($"(t-{S(startSeconds)})*{S(spinDegreesPerSecond)}");
        if (keyframes.Any(item => item.Property == KeyframeProperty.Rotation))
            parts.Add($"({KeyframeEvaluator.BuildFfmpegExpression(keyframes, KeyframeProperty.Rotation, 0, "t", startSeconds)})");
        if (parts.Count == 0) return "0";
        return $"({string.Join('+', parts)})*PI/180";
    }

    /// <summary>
    /// Builds the pad-rotate pair for an item of this size turning about an anchor given
    /// as a fraction of its own box.
    /// </summary>
    public static Plan Build(int width, int height, double anchorX, double anchorY, string angleExpression, int maximumSide)
    {
        var pivotX = Math.Clamp(anchorX, 0, 1) * width;
        var pivotY = Math.Clamp(anchorY, 0, 1) * height;

        // The canvas has to reach as far from the anchor as the furthest corner does, in
        // every direction, or the corners clip as the picture comes round.
        var reach = Math.Sqrt(Math.Pow(Math.Max(pivotX, width - pivotX), 2) + Math.Pow(Math.Max(pivotY, height - pivotY), 2));
        var side = (int)Math.Ceiling(reach) * 2;
        side += side % 2;                                   // encoders want even dimensions
        side = Math.Min(side, maximumSide);

        var offsetX = (int)Math.Round(side / 2d - pivotX);
        var offsetY = (int)Math.Round(side / 2d - pivotY);
        var filters = new List<string>
        {
            $"pad={side}:{side}:{offsetX}:{offsetY}:color=0x00000000",
            $"rotate=a='{angleExpression}':c=none:ow={side}:oh={side}",
        };
        return new Plan(filters, offsetX, offsetY, side, side);
    }
}
