using System.Globalization;

namespace Post.Core;

public static class KeyframeEvaluator
{
    public static double Evaluate(IEnumerable<AnimationKeyframe> keyframes, KeyframeProperty property, TimeSpan offset, double fallback)
    {
        var points = keyframes.Where(item => item.Property == property).OrderBy(item => item.Offset).ToArray();
        if (points.Length == 0) return fallback;
        if (offset <= points[0].Offset) return points[0].Value;
        if (offset >= points[^1].Offset) return points[^1].Value;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var left = points[i]; var right = points[i + 1];
            if (offset > right.Offset) continue;
            if (left.Interpolation == KeyframeInterpolation.Discrete) return left.Value;
            var span = Math.Max(1, (right.Offset - left.Offset).Ticks);
            var amount = Math.Clamp((offset - left.Offset).Ticks / (double)span, 0, 1);
            if (left.Interpolation == KeyframeInterpolation.Smooth) amount = amount * amount * (3 - 2 * amount);
            return left.Value + (right.Value - left.Value) * amount;
        }
        return points[^1].Value;
    }

    public static AnimationKeyframe Upsert(ICollection<AnimationKeyframe> keyframes, KeyframeProperty property, TimeSpan offset, double value, KeyframeInterpolation interpolation, TimeSpan? maximum = null)
    {
        offset = offset < TimeSpan.Zero ? TimeSpan.Zero : maximum is { } max && offset > max ? max : offset;
        var tolerance = TimeSpan.FromMilliseconds(1);
        var existing = keyframes.FirstOrDefault(item => item.Property == property && (item.Offset - offset).Duration() <= tolerance);
        if (existing is not null) { existing.Value = value; existing.Interpolation = interpolation; return existing; }
        var created = new AnimationKeyframe { Property = property, Offset = offset, Value = value, Interpolation = interpolation };
        keyframes.Add(created); return created;
    }

    public static AnimationKeyframe UpsertWithBaseline(ICollection<AnimationKeyframe> keyframes, KeyframeProperty property, TimeSpan offset,
        double value, KeyframeInterpolation interpolation, double baseline, TimeSpan? maximum = null)
    {
        offset = offset < TimeSpan.Zero ? TimeSpan.Zero : maximum is { } max && offset > max ? max : offset;
        if (offset > TimeSpan.Zero && !keyframes.Any(item => item.Property == property))
            Upsert(keyframes, property, TimeSpan.Zero, baseline, KeyframeInterpolation.Discrete, maximum);
        return Upsert(keyframes, property, offset, value, interpolation, maximum);
    }

    public static string BuildFfmpegExpression(IEnumerable<AnimationKeyframe> keyframes, KeyframeProperty property, double fallback, string timeVariable = "t", double timeOffsetSeconds = 0)
    {
        var points = keyframes.Where(item => item.Property == property).OrderBy(item => item.Offset).ToArray();
        if (points.Length == 0) return Number(fallback);
        if (points.Length == 1) return Number(points[0].Value);
        var localTime = Math.Abs(timeOffsetSeconds) < .0000001 ? timeVariable : $"({timeVariable}-{Number(timeOffsetSeconds)})";
        var expression = Number(points[^1].Value);
        for (var i = points.Length - 2; i >= 0; i--)
        {
            var left = points[i]; var right = points[i + 1];
            var start = Number(left.Offset.TotalSeconds); var end = Number(right.Offset.TotalSeconds);
            string value;
            if (left.Interpolation == KeyframeInterpolation.Discrete) value = Number(left.Value);
            else
            {
                var u = $"max(0,min(1,({localTime}-{start})/max(0.000001,{end}-{start})))";
                if (left.Interpolation == KeyframeInterpolation.Smooth) u = $"(({u})*({u})*(3-2*({u})))";
                value = $"({Number(left.Value)}+({Number(right.Value)}-{Number(left.Value)})*({u}))";
            }
            expression = $"if(lt({localTime},{end}),{value},{expression})";
        }
        return $"if(lte({localTime},{Number(points[0].Offset.TotalSeconds)}),{Number(points[0].Value)},{expression})";
    }

    public static AnimationKeyframe Clone(AnimationKeyframe value, TimeSpan? offset = null) => new()
    {
        Id = value.Id,
        Property = value.Property,
        Offset = offset ?? value.Offset,
        Value = value.Value,
        Interpolation = value.Interpolation
    };

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
