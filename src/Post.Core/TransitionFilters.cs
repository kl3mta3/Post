using System.Globalization;

namespace Post.Core;

/// <summary>
/// What a transition becomes in the filter graph.
///
/// Every one of them is the same idea: during the transition window, both clips are on
/// screen, and the incoming one is revealed over the outgoing one by a function of where the
/// pixel is and how far through we are. That function is the only thing that differs between
/// a dissolve, a wipe and an iris.
///
/// The clips are already overlaid in order, so the incoming clip is on top and only its alpha
/// has to be driven — the outgoing one shows through wherever the incoming one is not yet
/// opaque. Fades are the exception: they go through black, so both sides fade against the
/// base rather than against each other.
/// </summary>
public static class TransitionFilters
{
    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>How much extra footage each side has to show for this transition.</summary>
    public static (TimeSpan Outgoing, TimeSpan Incoming) Reach(ClipTransition transition)
    {
        // Whatever falls after the cut is the outgoing clip carrying on; whatever falls
        // before it is the incoming clip starting early.
        var after = transition.End - transition.Cut;
        var before = transition.Cut - transition.Start;
        return (after > TimeSpan.Zero ? after : TimeSpan.Zero, before > TimeSpan.Zero ? before : TimeSpan.Zero);
    }

    /// <summary>
    /// How far through the transition, as an ffmpeg expression in composition time. Clamped,
    /// because a filter is asked about frames outside its window too.
    /// </summary>
    /// <remarks>
    /// The time variable is T, not t: inside geq the lower-case one does not exist, and it
    /// only means anything to the enable= option outside it. And there is no clip(), so the
    /// clamp is spelled out.
    /// </remarks>
    private static string Progress(ClipTransition transition, string time = "T") =>
        $"min(max(({time}-{S(transition.Start.TotalSeconds)})/{S(Math.Max(.0001, transition.Duration.TotalSeconds))},0),1)";

    /// <summary>
    /// The alpha the incoming clip should have, per pixel, through the transition — the whole
    /// difference between one transition and another.
    /// </summary>
    public static string IncomingAlpha(ClipTransition transition)
    {
        var p = Progress(transition);
        return transition.Kind switch
        {
            // Straight cross-fade.
            TransitionKind.Dissolve => p,

            // A hard edge sweeping across, with a soft margin so it does not crawl.
            TransitionKind.WipeLeft => Edge($"(1-(X/W))", p),
            TransitionKind.WipeRight => Edge("(X/W)", p),
            TransitionKind.WipeUp => Edge("(1-(Y/H))", p),
            TransitionKind.WipeDown => Edge("(Y/H)", p),

            // Distance from the middle, so the edge is a circle. Normalised by the half
            // diagonal, which is the furthest a corner can be.
            TransitionKind.IrisIn => Edge($"(1-{Radius})", p),
            TransitionKind.IrisOut => Edge(Radius, p),

            // Pushes are a move, not a reveal: the incoming clip is simply opaque.
            TransitionKind.PushLeft or TransitionKind.PushRight => "1",

            // Through black: the incoming clip appears only in the second half, and then
            // fades up from nothing.
            _ => $"min(max(({p}-0.5)*2,0),1)",
        };
    }

    /// <summary>
    /// The alpha the outgoing clip should have. Only fades need it: everything else leaves
    /// the outgoing clip alone and lets the incoming one cover it.
    /// </summary>
    public static string? OutgoingAlpha(ClipTransition transition) =>
        transition.Kind is TransitionKind.FadeToBlack or TransitionKind.FadeFromBlack
            ? $"min(max(1-({Progress(transition)})*2,0),1)"
            : null;

    private const string Radius = "(hypot(X/W-0.5,Y/H-0.5)/0.7071)";

    /// <summary>
    /// A moving edge: fully on where the position is behind the sweep, fully off ahead of it,
    /// with a little softness between so the boundary does not shimmer.
    /// </summary>
    private static string Edge(string position, string progress)
    {
        const double softness = .04;
        return $"min(max(((({progress})*(1+{S(softness * 2)})-{S(softness)}-({position}))/{S(softness)}),0),1)";
    }

    /// <summary>
    /// The geq filter that applies an alpha expression for the length of a transition, and
    /// leaves every other frame untouched.
    /// </summary>
    public static string AlphaFilter(string alpha, ClipTransition transition) =>
        $"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({alpha})'" +
        $":enable='between(t,{S(transition.Start.TotalSeconds)},{S(transition.End.TotalSeconds)})'";

    /// <summary>
    /// How far the incoming clip is pushed sideways, in fractions of the frame, for the push
    /// transitions. Zero for everything else.
    /// </summary>
    /// <remarks>
    /// In lower-case t, because these go to the overlay filter's x and y, where the geq
    /// spelling means nothing. The same difference that stops a graph parsing at all.
    /// </remarks>
    public static string? PushOffset(ClipTransition transition) => transition.Kind switch
    {
        TransitionKind.PushLeft => Gated(transition, $"(1-{Progress(transition, "t")})"),
        TransitionKind.PushRight => Gated(transition, $"({Progress(transition, "t")}-1)"),
        _ => null,
    };

    /// <summary>
    /// And the outgoing clip slides out the other way. A push where only the new picture
    /// moves is not a push, it is the new one arriving over a still one.
    /// </summary>
    public static string? OutgoingPushOffset(ClipTransition transition) => transition.Kind switch
    {
        TransitionKind.PushLeft => Gated(transition, $"(0-{Progress(transition, "t")})"),
        TransitionKind.PushRight => Gated(transition, Progress(transition, "t")),
        _ => null,
    };

    /// <summary>
    /// Nothing outside the transition's own window, because an overlay position is evaluated
    /// on every frame of the clip and not just the ones being transitioned.
    /// </summary>
    private static string Gated(ClipTransition transition, string offset) =>
        $"(if(between(t,{S(transition.Start.TotalSeconds)},{S(transition.End.TotalSeconds)}),{offset},0))";

    /// <summary>
    /// How loud this side should be through a transition, 0 to 1. Sound cross-fades whatever
    /// the picture is doing: a hard cut under a dissolve is the thing everybody hears.
    /// </summary>
    public static double AudioGain(ClipTransition transition, bool outgoing, TimeSpan at)
    {
        var through = transition.Progress(at);
        return Math.Clamp(outgoing ? 1 - through : through, 0, 1);
    }

    /// <summary>
    /// The same ramp as an expression for the volume filter.
    /// </summary>
    /// <param name="streamStartSeconds">
    /// Where this clip's audio stream begins on the timeline. The volume filter runs after
    /// asetpts, so its t counts from the start of the stream rather than from the start of
    /// the composition, and the two have to be reconciled here.
    /// </param>
    /// <remarks>
    /// No gating: before the transition the clamped progress is already 0 and after it 1, so
    /// each side sits at full volume everywhere its own stream exists and the ramp only bites
    /// inside the window.
    /// </remarks>
    public static string AudioRamp(ClipTransition transition, bool outgoing, double streamStartSeconds)
    {
        var through = $"min(max((t+{S(streamStartSeconds)}-{S(transition.Start.TotalSeconds)})" +
                      $"/{S(Math.Max(.0001, transition.Duration.TotalSeconds))},0),1)";
        return outgoing ? $"(1-{through})" : $"({through})";
    }
}
