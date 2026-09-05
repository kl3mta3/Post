namespace Post.Core;

/// <summary>
/// Where a transition can go, and how long it is allowed to be.
///
/// A transition is centred on a cut and reaches into the media either side of it: the frames
/// past the outgoing clip's out-point, and the frames before the incoming clip's in-point.
/// Neither clip moves and neither is trimmed. When the media does not have those frames, the
/// transition shortens to what is there — the edit is the thing being protected, not the
/// requested duration.
/// </summary>
public static class Transitions
{
    /// <summary>Two clips count as meeting at a cut if they are this close.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(40);

    /// <summary>The pair of clips meeting at this point, outgoing first.</summary>
    public static (TimelinePlacement Outgoing, TimelinePlacement Incoming)? CutAt(TimelineLayer layer, TimeSpan at)
    {
        foreach (var outgoing in layer.Placements)
        {
            if ((outgoing.End - at).Duration() > Tolerance) continue;
            foreach (var incoming in layer.Placements)
            {
                if (ReferenceEquals(incoming, outgoing)) continue;
                if ((incoming.Start - outgoing.End).Duration() <= Tolerance) return (outgoing, incoming);
            }
        }
        return null;
    }

    /// <summary>
    /// The frames the outgoing clip has past its out-point: what a transition can borrow to
    /// keep showing it after the cut.
    /// </summary>
    public static TimeSpan HandleAfter(TimelinePlacement placement)
    {
        var used = placement.InPoint + placement.Duration;
        var total = placement.Clip.SelectedDuration;
        return total > used ? total - used : TimeSpan.Zero;
    }

    /// <summary>The frames the incoming clip has before its in-point.</summary>
    public static TimeSpan HandleBefore(TimelinePlacement placement) =>
        placement.InPoint > TimeSpan.Zero ? placement.InPoint : TimeSpan.Zero;

    /// <summary>
    /// The longest transition this cut can carry. It is centred, so each side supplies half,
    /// and the shorter handle decides. Zero means the media has nothing spare and a
    /// transition here would have to invent frames.
    /// </summary>
    public static TimeSpan LongestAt(TimelineLayer layer, TimeSpan at,
        TransitionAlignment alignment = TransitionAlignment.CenterAtCut)
    {
        if (CutAt(layer, at) is not { } pair) return TimeSpan.Zero;

        // Which side has to find the spare frames depends on where the transition sits.
        // Centred, both do and each supplies half; leaning one way, only one does.
        var spare = alignment switch
        {
            TransitionAlignment.StartAtCut => HandleAfter(pair.Outgoing),
            TransitionAlignment.EndAtCut => HandleBefore(pair.Incoming),
            _ => TimeSpan.FromTicks(Min(HandleAfter(pair.Outgoing), HandleBefore(pair.Incoming)).Ticks * 2),
        };

        // Neither clip may be swallowed whole either: a transition cannot be longer than the
        // clip it is coming out of, or the one it is going into.
        var shortest = Min(pair.Outgoing.Duration, pair.Incoming.Duration);
        return TimeSpan.FromTicks(Math.Min(spare.Ticks, shortest.Ticks));
    }

    /// <summary>
    /// The alignment that gives the longest transition at this cut — which is how a cut with
    /// nothing spare on one side still gets one, by leaning on the other.
    /// </summary>
    public static TransitionAlignment BestAlignmentAt(TimelineLayer layer, TimeSpan at)
    {
        var options = new[] { TransitionAlignment.CenterAtCut, TransitionAlignment.StartAtCut, TransitionAlignment.EndAtCut };
        return options.OrderByDescending(alignment => LongestAt(layer, at, alignment).Ticks).First();
    }

    /// <summary>
    /// Fits a transition to a cut, shortening it to what the media can supply and leaning it
    /// whichever way gives the most room. Returns null when there is no cut there, or nothing
    /// spare on either side to make one from.
    /// </summary>
    public static ClipTransition? Fit(TimelineLayer layer, TimeSpan at, TransitionKind kind,
        TimeSpan? requested = null, TransitionAlignment? alignment = null)
    {
        if (CutAt(layer, at) is not { } pair) return null;

        var placed = alignment ?? BestAlignmentAt(layer, at);
        var longest = LongestAt(layer, at, placed);
        if (longest <= TimeSpan.Zero) return null;

        var wanted = requested ?? ClipTransition.DefaultDuration;
        return new ClipTransition
        {
            Kind = kind,
            Cut = pair.Outgoing.End,
            Alignment = placed,
            Duration = Min(wanted, longest),
        };
    }

    /// <summary>
    /// How far outside its own span this clip has to be shown, because a transition either
    /// side of it is borrowing frames. Capped by what the media actually holds, so this never
    /// asks for footage that is not there.
    /// </summary>
    public static (TimeSpan Before, TimeSpan After) ReachFor(TimelineLayer layer, TimelinePlacement placement)
    {
        var before = TimeSpan.Zero;
        var after = TimeSpan.Zero;

        foreach (var transition in layer.Transitions)
        {
            // A transition on this clip's own start borrows from before its in-point; one on
            // its end borrows from after its out-point.
            if ((transition.Cut - placement.Start).Duration() <= Tolerance)
            {
                var wanted = transition.Cut - transition.Start;
                if (wanted > before) before = wanted;
            }
            if ((transition.Cut - placement.End).Duration() <= Tolerance)
            {
                var wanted = transition.End - transition.Cut;
                if (wanted > after) after = wanted;
            }
        }

        return (Cap(before, HandleBefore(placement)), Cap(after, HandleAfter(placement)));
    }

    private static TimeSpan Cap(TimeSpan wanted, TimeSpan available) =>
        wanted <= TimeSpan.Zero ? TimeSpan.Zero : wanted < available ? wanted : available;

    /// <summary>The transition covering this moment on this layer, if there is one.</summary>
    public static ClipTransition? At(TimelineLayer layer, TimeSpan at) =>
        layer.Transitions.FirstOrDefault(item => at >= item.Start && at < item.End);

    /// <summary>
    /// Where in its own source each side should be read during the transition. The outgoing
    /// clip carries on past its out-point; the incoming one starts before its in-point.
    /// </summary>
    public static (TimeSpan Outgoing, TimeSpan Incoming) SourceTimes(
        ClipTransition transition, TimelinePlacement outgoing, TimelinePlacement incoming, TimeSpan at)
    {
        var fromCut = at - transition.Cut;
        return (outgoing.InPoint + outgoing.Duration + fromCut, incoming.InPoint + fromCut);
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}
