using Post.Core;
using System.Windows.Shapes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Transitions on the timeline: putting one on a cut, editing it, and drawing the little
/// marker that shows where it is.
///
/// The rules about where one can go and how long it may be live in
/// <see cref="Post.Core.Transitions"/>; this is the part someone touches.
/// </summary>
public partial class MainWindow
{
    internal static string TransitionName(TransitionKind kind) => kind switch
    {
        TransitionKind.Dissolve => "Dissolve",
        TransitionKind.FadeToBlack => "Fade to black",
        TransitionKind.FadeFromBlack => "Fade from black",
        TransitionKind.WipeLeft => "Wipe left",
        TransitionKind.WipeRight => "Wipe right",
        TransitionKind.WipeUp => "Wipe up",
        TransitionKind.WipeDown => "Wipe down",
        TransitionKind.IrisIn => "Iris in",
        TransitionKind.IrisOut => "Iris out",
        TransitionKind.PushLeft => "Push left",
        _ => "Push right",
    };

    private static string AlignmentName(TransitionAlignment alignment) => alignment switch
    {
        TransitionAlignment.StartAtCut => "Start at cut",
        TransitionAlignment.EndAtCut => "End at cut",
        _ => "Center at cut",
    };

    /// <summary>Says what happened where Post already talks.</summary>
    private void Status(string message)
    {
        if (BusyText is not null) BusyText.Text = message;
        if (CompositionStatus is not null) CompositionStatus.ToolTip = message;
    }

    /// <summary>
    /// Puts a transition on the cut nearest the caret. Says why when it cannot, rather than
    /// doing nothing: a cut with no spare frames either side is the usual reason, and it is
    /// not obvious from looking at the timeline.
    /// </summary>
    private void AddTransition(TransitionKind kind)
    {
        var layer = FindLayer(_activeLayerId) ?? _composition.Layers.FirstOrDefault(item => item.Placements.Count > 1);
        if (layer is null) { Status($"Add two clips to a layer first, then a {TransitionName(kind).ToLowerInvariant()} can go on the cut between them."); return; }

        // The nearest cut to the caret, which is where someone means when they have just
        // parked it near one.
        var cut = layer.Placements
            .Select(item => item.End)
            .Where(end => Post.Core.Transitions.CutAt(layer, end) is not null)
            .OrderBy(end => (end - _editPosition).Duration())
            .FirstOrDefault();

        if (cut == default) { Status("There is no cut on this layer to put a transition on."); return; }

        if (Post.Core.Transitions.At(layer, cut) is not null)
        { Status("There is already a transition on that cut."); return; }

        if (Post.Core.Transitions.Fit(layer, cut, kind) is not { } transition)
        {
            Status($"Neither clip at {TimeText.Format(cut)} has any spare frames, so there is nothing to make a transition out of. Trim one of them back and try again.");
            return;
        }

        EnsureProjectHistory();
        layer.Transitions.Add(transition);
        CommitProjectEdit();
        RefreshLayerStack();
        InvalidateCompositionPreview();

        var shortened = transition.Duration < ClipTransition.DefaultDuration;
        Status(shortened
            ? $"{TransitionName(kind)} added at {TimeText.Format(cut)}, shortened to {transition.Duration.TotalSeconds:0.##}s — that is all the spare footage there is."
            : $"{TransitionName(kind)} added at {TimeText.Format(cut)}.");
    }

    /// <summary>
    /// Shows a transition on the preview: how much of this clip is visible at this moment,
    /// and through what shape.
    ///
    /// The export drives the incoming clip's alpha per pixel; here the same thing is a plain
    /// opacity for the ones that fade evenly, and an opacity mask — a gradient with a hard
    /// edge in it — for the ones with a shape. Both come out of the same reading of how far
    /// through the transition we are.
    /// </summary>
    private static double ApplyTransition(UIElement element, TimelineLayer layer, TimelinePlacement placement, TimeSpan projectTime)
    {
        element.OpacityMask = null;

        var incoming = layer.Transitions.FirstOrDefault(item =>
            (item.Cut - placement.Start).Duration() <= Post.Core.Transitions.Tolerance && projectTime >= item.Start && projectTime < item.End);
        var outgoing = layer.Transitions.FirstOrDefault(item =>
            (item.Cut - placement.End).Duration() <= Post.Core.Transitions.Tolerance && projectTime >= item.Start && projectTime < item.End);

        // Outside its own span with no transition covering it, this clip is only on screen
        // because of the widened reach — so it should not be seen at all.
        if (incoming is null && outgoing is null)
            return projectTime >= placement.Start && projectTime < placement.End ? 1 : 0;

        if (outgoing is not null)
        {
            // Only the fades take the outgoing clip down; otherwise the incoming one covers
            // it, exactly as the overlay does on export.
            var progress = outgoing.Progress(projectTime);
            return outgoing.Kind is TransitionKind.FadeToBlack or TransitionKind.FadeFromBlack
                ? Math.Clamp(1 - progress * 2, 0, 1)
                : 1;
        }

        var through = incoming!.Progress(projectTime);
        switch (incoming.Kind)
        {
            case TransitionKind.Dissolve:
                return through;
            case TransitionKind.FadeToBlack:
            case TransitionKind.FadeFromBlack:
                return Math.Clamp((through - .5) * 2, 0, 1);
            case TransitionKind.PushLeft:
            case TransitionKind.PushRight:
                return 1;   // a move, not a reveal; the transform carries it
            default:
                element.OpacityMask = MaskFor(incoming.Kind, through);
                return 1;
        }
    }

    /// <summary>
    /// How loud this clip should be at this moment, given any transition it is part of.
    /// Sound cross-fades whether the picture is dissolving, wiping or pushing.
    /// </summary>
    private static double TransitionAudioGain(TimelineLayer layer, TimelinePlacement placement, TimeSpan projectTime)
    {
        var gain = 1d;
        foreach (var transition in layer.Transitions)
        {
            if (projectTime < transition.Start || projectTime >= transition.End) continue;
            if ((transition.Cut - placement.Start).Duration() <= Post.Core.Transitions.Tolerance)
                gain *= TransitionFilters.AudioGain(transition, outgoing: false, projectTime);
            if ((transition.Cut - placement.End).Duration() <= Post.Core.Transitions.Tolerance)
                gain *= TransitionFilters.AudioGain(transition, outgoing: true, projectTime);
        }
        return gain;
    }

    /// <summary>
    /// How far sideways a push has moved this clip, as a fraction of the frame. The incoming
    /// one slides in from an edge and the outgoing one slides out of the other.
    /// </summary>
    private static double TransitionPush(TimelineLayer layer, TimelinePlacement placement, TimeSpan projectTime)
    {
        var offset = 0d;
        foreach (var transition in layer.Transitions)
        {
            if (transition.Kind is not (TransitionKind.PushLeft or TransitionKind.PushRight)) continue;
            if (projectTime < transition.Start || projectTime >= transition.End) continue;

            var through = transition.Progress(projectTime);
            var left = transition.Kind == TransitionKind.PushLeft;

            if ((transition.Cut - placement.Start).Duration() <= Post.Core.Transitions.Tolerance)
                offset += left ? 1 - through : through - 1;
            if ((transition.Cut - placement.End).Duration() <= Post.Core.Transitions.Tolerance)
                offset += left ? -through : through;
        }
        return offset;
    }

    /// <summary>
    /// The shape a wipe or an iris reveals through: a gradient with its stops almost on top
    /// of each other, which is a moving edge with just enough softness not to crawl.
    /// </summary>
    private static Brush MaskFor(TransitionKind kind, double through)
    {
        const double softness = .04;
        var at = through * (1 + softness * 2) - softness;

        var stops = new GradientStopCollection
        {
            new(Colors.White, Math.Clamp(at - softness, 0, 1)),
            new(Colors.Transparent, Math.Clamp(at + softness, 0, 1)),
        };

        if (kind is TransitionKind.IrisIn or TransitionKind.IrisOut)
        {
            // Out from the middle, or in from the edges.
            var radial = new RadialGradientBrush(stops) { GradientOrigin = new Point(.5, .5), Center = new Point(.5, .5), RadiusX = .75, RadiusY = .75 };
            if (kind == TransitionKind.IrisOut) radial.GradientStops = Flip(stops);
            return radial;
        }

        var (start, end) = kind switch
        {
            TransitionKind.WipeLeft => (new Point(1, .5), new Point(0, .5)),
            TransitionKind.WipeUp => (new Point(.5, 1), new Point(.5, 0)),
            TransitionKind.WipeDown => (new Point(.5, 0), new Point(.5, 1)),
            _ => (new Point(0, .5), new Point(1, .5)),   // wipe right
        };
        return new LinearGradientBrush(stops) { StartPoint = start, EndPoint = end };
    }

    private static GradientStopCollection Flip(GradientStopCollection stops) =>
        [.. stops.Select(stop => new GradientStop(stop.Color == Colors.White ? Colors.Transparent : Colors.White, stop.Offset))];

    /// <summary>
    /// Draws each transition across the cut it sits on: a band the width of its duration,
    /// with the bowtie every editor uses so it reads as a transition and not a clip.
    /// </summary>
    private void AddTransitionMarkers(Canvas lane, TimelineLayer layer, TimeSpan display, double laneWidth, double height)
    {
        if (display <= TimeSpan.Zero) return;

        foreach (var transition in layer.Transitions)
        {
            var left = transition.Start.TotalSeconds / display.TotalSeconds * laneWidth;
            var width = Math.Max(6, transition.Duration.TotalSeconds / display.TotalSeconds * laneWidth);

            var band = new Border
            {
                Width = width, Height = height,
                Background = new SolidColorBrush(Color.FromArgb(150, 250, 190, 90)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 211, 120)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                ToolTip = $"{TransitionName(transition.Kind)} · {transition.Duration.TotalSeconds:0.##}s · {AlignmentName(transition.Alignment).ToLowerInvariant()}"
                          + Environment.NewLine + "Right-click to edit",
                Tag = transition,
                Child = new Path
                {
                    // The bowtie: two triangles meeting in the middle.
                    Data = Geometry.Parse("M0,0 L1,0.5 L0,1 Z M1.6,0 L0.6,0.5 L1.6,1 Z"),
                    Stretch = Stretch.Fill, Fill = new SolidColorBrush(Color.FromArgb(190, 40, 26, 4)),
                    Margin = new Thickness(2),
                },
            };

            band.ContextMenu = CreateTransitionMenu(layer, transition);
            band.MouseRightButtonUp += (_, e) => e.Handled = false;

            Panel.SetZIndex(band, 60);
            Canvas.SetLeft(band, Math.Max(0, left));
            Canvas.SetTop(band, 0);
            lane.Children.Add(band);
        }
    }

    /// <summary>The right-click menu on a transition: what it is, how long, and where it sits.</summary>
    private ContextMenu CreateTransitionMenu(TimelineLayer layer, ClipTransition transition)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = new TextBlock { Text = $"{TransitionName(transition.Kind)} · {transition.Duration.TotalSeconds:0.##}s", FontWeight = FontWeights.SemiBold },
            IsHitTestVisible = false,
        });
        menu.Items.Add(new Separator());

        var edit = new MenuItem { Header = "Edit transition…" };
        edit.Click += (_, _) => EditTransition(layer, transition);
        menu.Items.Add(edit);

        var kinds = new MenuItem { Header = "Change to" };
        foreach (var kind in Enum.GetValues<TransitionKind>())
        {
            var item = new MenuItem { Header = TransitionName(kind), IsCheckable = true, IsChecked = kind == transition.Kind };
            item.Click += (_, _) =>
            {
                EnsureProjectHistory(); transition.Kind = kind; CommitProjectEdit();
                RefreshLayerStack(); InvalidateCompositionPreview();
            };
            kinds.Items.Add(item);
        }
        menu.Items.Add(kinds);

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Remove transition" };
        remove.Click += (_, _) =>
        {
            EnsureProjectHistory(); layer.Transitions.Remove(transition); CommitProjectEdit();
            RefreshLayerStack(); InvalidateCompositionPreview();
        };
        menu.Items.Add(remove);
        return menu;
    }

    private void EditTransition(TimelineLayer layer, ClipTransition transition)
    {
        var dialog = new TransitionEditor(this, layer, transition);
        if (dialog.ShowDialog() != true) return;

        EnsureProjectHistory();
        transition.Kind = dialog.Kind;
        transition.Alignment = dialog.Alignment;

        // Whatever was asked for, the media still decides: the longest it may be depends on
        // which way it leans, so this is re-checked against the alignment just chosen.
        var longest = Post.Core.Transitions.LongestAt(layer, transition.Cut, dialog.Alignment);
        transition.Duration = dialog.Duration < longest ? dialog.Duration : longest;
        CommitProjectEdit();

        RefreshLayerStack();
        InvalidateCompositionPreview();

        if (transition.Duration < dialog.Duration)
            Status($"Shortened to {transition.Duration.TotalSeconds:0.##}s — that is all the spare footage {AlignmentName(dialog.Alignment).ToLowerInvariant()} allows.");
    }
}

/// <summary>Type, length and where it sits against the cut.</summary>
internal sealed class TransitionEditor : Window
{
    private readonly ComboBox _kind = new() { MinWidth = 220 };
    private readonly ComboBox _alignment = new() { MinWidth = 220 };
    private readonly Slider _duration = new() { Minimum = .1, Maximum = 5, TickFrequency = .05 };
    private readonly TextBlock _room = new() { Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private readonly TimelineLayer _layer;
    private readonly ClipTransition _transition;

    public TransitionKind Kind => (TransitionKind)(_kind.SelectedItem as ComboBoxItem)!.Tag;
    public TransitionAlignment Alignment => (TransitionAlignment)(_alignment.SelectedItem as ComboBoxItem)!.Tag;
    public TimeSpan Duration => TimeSpan.FromSeconds(_duration.Value);

    public TransitionEditor(Window owner, TimelineLayer layer, ClipTransition transition)
    {
        _layer = layer; _transition = transition;

        Title = "Transition";
        Owner = owner; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(Label("Type"));
        foreach (var kind in Enum.GetValues<TransitionKind>())
        {
            var item = new ComboBoxItem { Content = MainWindow.TransitionName(kind), Tag = kind };
            _kind.Items.Add(item);
            if (kind == transition.Kind) _kind.SelectedItem = item;
        }
        panel.Children.Add(_kind);

        panel.Children.Add(Label("Alignment"));
        foreach (var alignment in Enum.GetValues<TransitionAlignment>())
        {
            var item = new ComboBoxItem { Content = Name(alignment), Tag = alignment };
            _alignment.Items.Add(item);
            if (alignment == transition.Alignment) _alignment.SelectedItem = item;
        }
        panel.Children.Add(_alignment);

        panel.Children.Add(Label("Duration"));
        var readout = new TextBlock { Foreground = Brushes.LightGray, FontSize = 11 };
        _duration.Value = Math.Clamp(transition.Duration.TotalSeconds, .1, 5);
        readout.Text = $"{_duration.Value:0.##}s";
        _duration.ValueChanged += (_, e) => { readout.Text = $"{e.NewValue:0.##}s"; ShowRoom(); };
        _alignment.SelectionChanged += (_, _) => ShowRoom();
        panel.Children.Add(_duration);
        panel.Children.Add(readout);
        panel.Children.Add(_room);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 84, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Padding = new Thickness(10, 5, 10, 5), IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        ShowRoom();
    }

    /// <summary>
    /// How much spare footage this cut has for the alignment currently chosen — said before
    /// the OK is pressed rather than after, so a shortening is not a surprise.
    /// </summary>
    private void ShowRoom()
    {
        if (_alignment.SelectedItem is not ComboBoxItem { Tag: TransitionAlignment alignment }) return;
        var longest = Post.Core.Transitions.LongestAt(_layer, _transition.Cut, alignment);

        _room.Text = longest <= TimeSpan.Zero
            ? "There are no spare frames for this alignment."
            : _duration.Value > longest.TotalSeconds
                ? $"The media only has {longest.TotalSeconds:0.##}s spare this way, so it will be shortened to that. Neither clip is trimmed."
                : $"{longest.TotalSeconds:0.##}s of spare footage is available this way.";
    }

    private static string Name(TransitionAlignment alignment) => alignment switch
    {
        TransitionAlignment.StartAtCut => "Start at cut — uses only the outgoing clip's spare frames",
        TransitionAlignment.EndAtCut => "End at cut — uses only the incoming clip's spare frames",
        _ => "Center at cut — takes half from each",
    };

    private static TextBlock Label(string text) => new()
    { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
}
