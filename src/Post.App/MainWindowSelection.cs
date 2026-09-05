using Post.App.Plugins;
using Post.Core;
using Post.Plugins;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Post.App;

/// <summary>What is selected on the timeline, and the ways of changing it.</summary>
/// <remarks>
/// One thing selected is still the ordinary case, and the rest of the editor keeps reading
/// <c>_selectedPlacementId</c> and <c>_selectedGraphicId</c> for it: those stay the last
/// thing clicked. The set below is what everything else is measured against, and holds
/// exactly that one id when nothing more has been added to it.
///
/// Shift extends along a layer because that is where "between these two" means something.
/// Ctrl adds anywhere, because picking three unrelated clips is the other half of the job
/// and has no order to respect.
/// </remarks>
public partial class MainWindow
{
    private readonly HashSet<Guid> _selection = [];

    /// <summary>Where a shift-click measures from: the last thing clicked without shift.</summary>
    private Guid? _selectionAnchorId;

    private bool IsSelected(Guid id) => _selection.Contains(id);

    /// <summary>More than one thing selected, which is what changes what a menu offers.</summary>
    private bool HasMultipleSelected => _selection.Count > 1;

    private void ClearSelection()
    {
        _selection.Clear();
        _selectionAnchorId = null;
        _selectedPlacementId = null;
        _selectedGraphicId = null;
    }

    /// <summary>
    /// What a click on a clip or overlay does, given what was held down. Returns false when
    /// the click should not go on to start a drag — a ctrl-click that deselected something
    /// is not the beginning of moving it.
    /// </summary>
    private bool SelectFromClick(TimelineLayer layer, Guid id, bool isGraphic)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl)
        {
            if (!_selection.Remove(id))
            {
                _selection.Add(id);
                _selectionAnchorId = id;
                Primary(layer, id, isGraphic);
                return true;
            }

            // Something was taken out of the set. The primary has to follow if it was that.
            if (_selectedPlacementId == id) _selectedPlacementId = null;
            if (_selectedGraphicId == id) _selectedGraphicId = null;
            RefreshLayerStack();
            return false;
        }

        if (shift && _selectionAnchorId is { } anchor && anchor != id)
        {
            SelectRange(layer, anchor, id);
            Primary(layer, id, isGraphic);
            return true;
        }

        // Pressing on something already part of a group keeps the group, so the press can
        // become a drag of all of it. Collapsing here instead would mean a plain drag never
        // moved more than the one clip under the cursor. If it turns out not to be a drag,
        // the release collapses it — which is what a plain click is supposed to do.
        if (_selection.Count > 1 && _selection.Contains(id))
        {
            _collapsePending = id;
            Primary(layer, id, isGraphic);
            return true;
        }

        _collapsePending = null;
        _selection.Clear();
        _selection.Add(id);
        _selectionAnchorId = id;
        Primary(layer, id, isGraphic);
        return true;
    }

    /// <summary>A press on a selected item that has not yet turned out to be a drag.</summary>
    private Guid? _collapsePending;

    /// <summary>The press was a click after all: keep only what was clicked.</summary>
    private void CollapseSelectionIfPending()
    {
        if (_collapsePending is not { } id) return;
        _collapsePending = null;

        _selection.Clear();
        _selection.Add(id);
        _selectionAnchorId = id;
        RefreshLayerStack();
        ReportSelection();
    }

    /// <summary>It was a drag, so the group stands.</summary>
    private void KeepSelectionAfterDrag() => _collapsePending = null;

    private void Primary(TimelineLayer layer, Guid id, bool isGraphic)
    {
        _activeLayerId = layer.Id;
        _selectedPlacementId = isGraphic ? null : id;
        _selectedGraphicId = isGraphic ? id : null;
    }

    /// <summary>
    /// Everything on one layer between two of its items, in time order. Both ends are
    /// included; anything already selected elsewhere is left alone, so shift after ctrl
    /// adds a run rather than throwing away what was picked.
    /// </summary>
    private void SelectRange(TimelineLayer layer, Guid from, Guid to)
    {
        var order = InTimeOrder(layer);
        var first = order.FindIndex(item => item.Id == from);
        var last = order.FindIndex(item => item.Id == to);

        // The anchor may be on another layer, in which case there is no run to take.
        if (first < 0 || last < 0)
        {
            _selection.Add(to);
            return;
        }

        if (first > last) (first, last) = (last, first);
        for (var index = first; index <= last; index++) _selection.Add(order[index].Id);
    }

    /// <summary>Everything a layer holds, clips and overlays alike, ordered by when it starts.</summary>
    private static List<(Guid Id, TimeSpan Start)> InTimeOrder(TimelineLayer layer) =>
    [
        .. layer.Placements.Select(item => (item.Id, item.Start))
            .Concat(layer.Graphics.Select(item => (item.Id, item.Start)))
            .OrderBy(item => item.Start)
    ];

    private void SelectAllOnLayer(TimelineLayer layer)
    {
        _selection.Clear();
        foreach (var (id, _) in InTimeOrder(layer)) _selection.Add(id);

        _activeLayerId = layer.Id;
        _selectionAnchorId = _selection.FirstOrDefault();
        _selectedPlacementId = layer.Placements.FirstOrDefault()?.Id;
        _selectedGraphicId = _selectedPlacementId is null ? layer.Graphics.FirstOrDefault()?.Id : null;

        RefreshLayerStack();
        ReportSelection();
    }

    /// <summary>What Edit ▸ Select offers: everything, or everything of one kind.</summary>
    private enum SelectionKind { Everything, Video, Audio, Graphics }

    private void SelectEverythingOfKind(SelectionKind kind)
    {
        _selection.Clear();
        _selectedPlacementId = null;
        _selectedGraphicId = null;

        foreach (var layer in _composition.Layers)
        {
            var wantsClips = kind switch
            {
                SelectionKind.Everything => true,
                SelectionKind.Video => layer.Kind == TimelineLayerKind.Video,
                SelectionKind.Audio => layer.Kind == TimelineLayerKind.Audio,
                _ => false,
            };
            var wantsGraphics = kind is SelectionKind.Everything or SelectionKind.Graphics;

            if (wantsClips)
                foreach (var placement in layer.Placements)
                {
                    _selection.Add(placement.Id);
                    _selectedPlacementId ??= placement.Id;
                }

            if (wantsGraphics)
                foreach (var graphic in layer.Graphics)
                {
                    _selection.Add(graphic.Id);
                    if (_selectedPlacementId is null) _selectedGraphicId ??= graphic.Id;
                }
        }

        _selectionAnchorId = _selection.FirstOrDefault();
        RefreshLayerStack();
        ReportSelection();
    }

    private void SelectEverything_Click(object sender, RoutedEventArgs e) => SelectEverythingOfKind(SelectionKind.Everything);
    private void SelectVideo_Click(object sender, RoutedEventArgs e) => SelectEverythingOfKind(SelectionKind.Video);
    private void SelectAudio_Click(object sender, RoutedEventArgs e) => SelectEverythingOfKind(SelectionKind.Audio);
    private void SelectGraphics_Click(object sender, RoutedEventArgs e) => SelectEverythingOfKind(SelectionKind.Graphics);

    private void SelectNothing_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        RefreshLayerStack();
        ReportSelection();
    }

    /// <summary>Says how much is selected, where Post already says things.</summary>
    private void ReportSelection()
    {
        if (CompositionStatus is null) return;
        CompositionStatus.ToolTip = _selection.Count switch
        {
            0 => "Nothing selected.",
            1 => "One item selected.",
            _ => $"{_selection.Count} items selected.",
        };
    }

    // ---- what is actually selected, resolved against the composition ----------

    /// <summary>One selected thing, and the layer it sits on.</summary>
    private readonly record struct Selected(TimelineLayer Layer, TimelinePlacement? Placement, GraphicsOverlay? Graphic)
    {
        public Guid Id => Placement?.Id ?? Graphic?.Id ?? Guid.Empty;
        public bool IsGraphic => Graphic is not null;
        public TimeSpan Start => Placement?.Start ?? Graphic?.Start ?? TimeSpan.Zero;
    }

    private List<Selected> SelectedItems()
    {
        var found = new List<Selected>();
        foreach (var layer in _composition.Layers)
        {
            foreach (var placement in layer.Placements)
                if (_selection.Contains(placement.Id)) found.Add(new Selected(layer, placement, null));

            foreach (var graphic in layer.Graphics)
                if (_selection.Contains(graphic.Id)) found.Add(new Selected(layer, null, graphic));
        }
        return [.. found.OrderBy(item => item.Start)];
    }

    /// <summary>The overlays selected, when they are all overlays and there is more than one.</summary>
    private List<(TimelineLayer Layer, GraphicsOverlay Graphic)> SelectedGraphics()
        => [.. SelectedItems().Where(item => item.Graphic is not null).Select(item => (item.Layer, item.Graphic!))];

    /// <summary>
    /// Edits every selected overlay at once, in the ordinary overlay editor: the same
    /// draggable preview, the same corner to resize by, the same fonts and colors.
    ///
    /// It edits a stand-in rather than one of the real overlays, and what comes back is
    /// copied onto all of them. The words are the one thing left alone — each overlay
    /// keeps its own — so the stand-in says what it says and the text box is dead.
    /// </summary>
    private void EditSelectedOverlays()
    {
        var overlays = SelectedGraphics().Select(item => item.Graphic).ToArray();
        if (overlays.Length == 0) return;

        var first = overlays[0];
        var standIn = new GraphicsOverlay
        {
            Kind = first.Kind,
            Text = "Text will be here.",
            ImagePath = first.ImagePath,
            FontFamily = first.FontFamily,
            FontSize = first.FontSize,
            Foreground = first.Foreground,
            Background = first.Background,
            FillColor1 = first.FillColor1,
            FillColor2 = first.FillColor2,
            UseSecondFillColor = first.UseSecondFillColor,
            GradientKind = first.GradientKind,
            GradientAngle = first.GradientAngle,
            Opacity = first.Opacity,
            PreserveAspectRatio = first.PreserveAspectRatio,
            X = first.X, Y = first.Y, Width = first.Width, Height = first.Height,
        };

        var editor = new GraphicsOverlayEditor(standIn, lockText: true)
        {
            Owner = this,
            Title = $"Edit {overlays.Length} overlays",
        };
        if (editor.ShowDialog() != true) return;

        EnsureProjectHistory();
        foreach (var overlay in overlays)
        {
            overlay.FontFamily = standIn.FontFamily;
            overlay.FontSize = standIn.FontSize;
            overlay.Foreground = standIn.Foreground;
            overlay.Background = standIn.Background;
            overlay.FillColor1 = standIn.FillColor1;
            overlay.FillColor2 = standIn.FillColor2;
            overlay.UseSecondFillColor = standIn.UseSecondFillColor;
            overlay.GradientKind = standIn.GradientKind;
            overlay.GradientAngle = standIn.GradientAngle;
            overlay.Opacity = standIn.Opacity;
            overlay.PreserveAspectRatio = standIn.PreserveAspectRatio;
            overlay.X = standIn.X;
            overlay.Y = standIn.Y;
            overlay.Width = standIn.Width;
            overlay.Height = standIn.Height;

            // Its own words are untouched, but what it renders to is now out of date.
            overlay.RenderedImagePath = null;
        }

        InvalidateCompositionPreview();
        CommitProjectEdit();
        RefreshLayerStack();
        UpdateLiveGraphics(_sequencePosition);
        ShowGraphicsWithoutMedia();
    }

    /// <summary>
    /// Takes a clip's sound onto an audio layer of its own, leaving the picture where it
    /// is. The clip keeps its audio track — nothing is re-encoded — it simply stops being
    /// heard from the video layer, so the same sound is not played twice.
    /// </summary>
    private void SplitAudioToItsOwnLayer(TimelineLayer layer, TimelinePlacement placement)
    {
        if (!placement.Clip.Media.HasAudio)
        {
            MessageBox.Show(this, $"{placement.Clip.DisplayName} has no audio to split off.", "Split audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (placement.AudioMuted)
        {
            MessageBox.Show(this, "That clip's audio is already on a layer of its own.", "Split audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureProjectHistory();

        var audio = new TimelineLayer
        {
            Name = $"{placement.Clip.DisplayName} audio",
            Kind = TimelineLayerKind.Audio,
        };
        // Directly under the layer it came from, where it is expected to be.
        _composition.Layers.Insert(Math.Min(_composition.Layers.Count, _composition.Layers.IndexOf(layer) + 1), audio);

        var copy = TimelineOperations.AddPlacement(audio, placement.Clip, placement.Start);
        copy.InPoint = placement.InPoint;
        copy.Length = placement.Length;

        // The picture keeps its own sound on file; it is only stopped from being heard.
        placement.AudioMuted = true;

        _activeLayerId = audio.Id;
        _selection.Clear();
        _selection.Add(copy.Id);
        _selectionAnchorId = copy.Id;
        _selectedPlacementId = copy.Id;
        _selectedGraphicId = null;

        InvalidateCompositionPreview();
        CommitProjectEdit();
        RefreshLayerStack();
        DrawCuts();
    }

    // ---- showing where a drag would land -------------------------------------

    /// <summary>The outlines drawn on the layer being dragged over, and the lane holding them.</summary>
    private readonly List<(Canvas Lane, UIElement Ghost)> _dragGhosts = [];

    /// <summary>What was faded on the layer being dragged from, to be put back afterwards.</summary>
    private readonly List<FrameworkElement> _fadedForDrag = [];

    /// <summary>
    /// Takes away the outlines and un-fades the originals. Called whenever the drag ends,
    /// however it ends: a cancelled drag has to leave the timeline as it found it.
    /// </summary>
    private void ClearDragGhosts()
    {
        foreach (var (lane, ghost) in _dragGhosts) lane.Children.Remove(ghost);
        _dragGhosts.Clear();

        foreach (var element in _fadedForDrag) element.Opacity = 1;
        _fadedForDrag.Clear();
    }

    /// <summary>
    /// Draws where a run would land on a layer that is not its own. The clips themselves
    /// cannot simply be moved onto another lane mid-drag — they are the real thing, and a
    /// cancelled drag would have to put them all back — so an outline is drawn there and
    /// the originals are faded to show they are the ones on the move.
    /// </summary>
    private void ShowDragOnAnotherLayer(
        Canvas sourceLane, Canvas targetLane, TimelinePlacement leader,
        List<(TimelinePlacement Placement, TimeSpan Gap)> carried, TimeSpan proposed, TimeSpan display)
    {
        var run = carried.Select(item => (item.Placement, item.Gap)).Append((leader, TimeSpan.Zero)).ToArray();

        foreach (var (placement, gap) in run)
        {
            var start = proposed + gap;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;

            var ghost = new Border
            {
                Width = Math.Max(6, placement.Duration.TotalSeconds / display.TotalSeconds * targetLane.Width),
                Height = 52,
                CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromRgb(102, 218, 255)),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(60, 102, 218, 255)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ghost, start.TotalSeconds / display.TotalSeconds * targetLane.Width);
            Canvas.SetTop(ghost, 10);
            targetLane.Children.Add(ghost);
            _dragGhosts.Add((targetLane, ghost));
        }

        var travelling = run.Select(item => item.Item1).ToHashSet();
        foreach (var element in sourceLane.Children.OfType<FrameworkElement>())
        {
            if (element.Tag is not TimelinePlacement placement || !travelling.Contains(placement)) continue;
            element.Opacity = .35;
            _fadedForDrag.Add(element);
        }
    }

    // ---- copying and pasting several at once ---------------------------------

    /// <summary>
    /// One copied item, and how far it sat after the earliest of them. The offset is what
    /// makes a paste keep the shape of what was copied rather than piling everything on
    /// the caret.
    /// </summary>
    private sealed record Copied(
        TimeSpan Offset,
        Guid SourceLayerId,
        TimelineLayerKind Kind,
        ClipItem? Clip,
        TimeSpan InPoint,
        TimeSpan Length,
        KeyframeState[] Keyframes,
        GraphicsOverlay? Graphic);

    private List<Copied> _selectionClipboard = [];

    /// <summary>
    /// Copies the whole selection. Returns false when there is nothing but a single item,
    /// which the ordinary one-item clipboard handles better than this would.
    /// </summary>
    private bool CopySelectionMany()
    {
        var items = SelectedItems();
        if (items.Count < 2) return false;

        var earliest = items.Min(item => item.Start);
        _selectionClipboard =
        [
            .. items.Select(item => new Copied(
                item.Start - earliest,
                item.Layer.Id,
                item.Layer.Kind,
                item.Placement?.Clip,
                item.Placement?.InPoint ?? TimeSpan.Zero,
                item.Placement?.Duration ?? TimeSpan.Zero,
                item.Placement is null ? [] : [.. item.Placement.Keyframes.Select(ToState)],
                item.Graphic is null ? null : CloneGraphic(item.Graphic)))
        ];

        // The one-item clipboard would otherwise paste alongside this and duplicate one.
        _placementClipboard = null;
        _graphicClipboard = null;
        return true;
    }

    /// <summary>
    /// Pastes them at the caret, keeping the gaps between them and the layers they came
    /// from: two clips copied from two layers arrive on two layers, still lined up.
    /// </summary>
    private bool PasteSelectionMany()
    {
        if (_selectionClipboard.Count == 0) return false;

        EnsureProjectHistory();
        var landed = new Dictionary<Guid, TimelineLayer>();
        var added = new List<Guid>();

        foreach (var item in _selectionClipboard)
        {
            var layer = LayerToPasteOnto(item, landed);
            var start = _editPosition + item.Offset;

            if (item.Graphic is not null)
            {
                var copy = CloneGraphic(item.Graphic);
                copy.Start = start;
                layer.Graphics.Add(copy);
                added.Add(copy.Id);
                ExtendWorkspace(copy.End);
            }
            else if (item.Clip is not null)
            {
                var placement = TimelineOperations.AddPlacement(layer, item.Clip, start);
                placement.InPoint = item.InPoint;
                placement.Length = item.Length;
                foreach (var keyframe in item.Keyframes) placement.Keyframes.Add(FromState(keyframe));
                added.Add(placement.Id);
                ExtendWorkspace(placement.End);
            }
        }

        // What was pasted becomes what is selected, so it can be moved straight away.
        _selection.Clear();
        foreach (var id in added) _selection.Add(id);
        _selectionAnchorId = added.FirstOrDefault();

        InvalidateCompositionPreview();
        CommitProjectEdit();
        RefreshLayerStack();
        DrawCuts();
        UpdateLiveGraphics(_sequencePosition);
        ReportSelection();
        return true;
    }

    /// <summary>
    /// Where one copied item goes. Each layer it was copied from maps to one layer here,
    /// so the arrangement survives; the first reuses the active layer when the kind fits,
    /// rather than making a new one beside an empty one.
    /// </summary>
    private TimelineLayer LayerToPasteOnto(Copied item, Dictionary<Guid, TimelineLayer> landed)
    {
        if (landed.TryGetValue(item.SourceLayerId, out var already)) return already;

        TimelineLayer layer;
        if (item.Kind == TimelineLayerKind.Graphics)
        {
            layer = CreateGraphicsLayer(item.Graphic?.Kind ?? GraphicsOverlayKind.Text);
        }
        else if (landed.Count == 0
                 && _composition.Layers.FirstOrDefault(candidate => candidate.Id == _activeLayerId && candidate.Kind == item.Kind) is { } active)
        {
            layer = active;
        }
        else
        {
            layer = new TimelineLayer
            {
                Name = item.Kind == TimelineLayerKind.Audio ? $"Audio {_composition.Layers.Count + 1}" : $"Layer {_composition.Layers.Count + 1}",
                Kind = item.Kind,
            };
            _composition.Layers.Add(layer);
        }

        landed[item.SourceLayerId] = layer;
        return layer;
    }

    // ---- dragging several along one layer ------------------------------------

    /// <summary>
    /// Everything else selected on the same layer as the clip being dragged, and how far
    /// each sits from it. Empty unless the whole selection is on that one layer: moving
    /// clips between layers is a different problem, and doing half of it would be worse
    /// than not offering it.
    /// </summary>
    private List<(TimelinePlacement Placement, TimeSpan Gap)> DraggableWith(TimelinePlacement dragged, TimelineLayer layer)
    {
        if (!HasMultipleSelected || !IsSelected(dragged.Id)) return [];

        var items = SelectedItems();
        if (items.Any(item => item.Layer.Id != layer.Id || item.Placement is null)) return [];

        return
        [
            .. items
                .Where(item => item.Placement!.Id != dragged.Id)
                .Select(item => (item.Placement!, item.Placement!.Start - dragged.Start))
        ];
    }

    /// <summary>
    /// Moves the rest of the selection by however far the dragged clip actually went.
    /// Nothing is allowed before zero, so the group stops as one when its earliest member
    /// reaches the start rather than bunching up against it.
    /// </summary>

    /// <summary>Removes everything selected, as one undo step rather than one per item.</summary>
    private void RemoveSelected()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;

        EnsureProjectHistory();
        foreach (var item in items)
        {
            if (item.Placement is not null) TimelineOperations.RemovePlacement(_composition, item.Placement.Id);
            else if (item.Graphic is not null) item.Layer.Graphics.Remove(item.Graphic);
        }

        ClearSelection();
        InvalidateCompositionPreview();
        CommitProjectEdit();
        RefreshLayerStack();
        DrawCuts();
        UpdateLiveGraphics(_sequencePosition);
    }

    /// <summary>
    /// The menu for a selection of several things.
    ///
    /// Everything is listed every time and greyed when it does not apply, so the shape of
    /// the menu does not shift about as the selection changes; the tooltip on a greyed item
    /// says which part of the selection is in the way.
    /// </summary>
    private ContextMenu CreateSelectionMenu()
    {
        var items = SelectedItems();
        var graphics = items.Where(item => item.Graphic is not null).ToArray();
        var clips = items.Where(item => item.Placement is not null).ToArray();

        var allText = graphics.Length == items.Count && graphics.All(item => item.Graphic!.Kind == GraphicsOverlayKind.Text);
        var allClips = clips.Length == items.Count;
        var oneLayer = items.Select(item => item.Layer.Id).Distinct().Count() == 1;

        var mixed = $"The selection holds {(graphics.Length > 0 && clips.Length > 0 ? "clips and overlays" : "more than one kind of thing")}.";

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{items.Count} items selected", IsEnabled = false });
        menu.Items.Add(new Separator());

        menu.Items.Add(Offer("Edit overlays…", allText,
            graphics.Length == 0 ? "Only text overlays can be edited together." : allText ? null : mixed,
            EditSelectedOverlays));

        menu.Items.Add(Offer("Effects…", allClips && oneLayer,
            !allClips ? "Effects apply to clips, and the selection holds overlays."
                      : "Select clips on one layer to give them the same effects.",
            () =>
            {
                _selectedPlacementId = clips[0].Placement!.Id;
                _activeLayerId = clips[0].Layer.Id;
                EnsureEffectsWindow();
            }));

        menu.Items.Add(new Separator());
        menu.Items.Add(Offer($"Remove {items.Count} items", true, null, RemoveSelected));

        AddPluginSelectionCommands(menu, items);
        return menu;
    }

    /// <summary>
    /// What plugins offer for a selection of several things. Listed whether they apply or
    /// not, like everything else here: a plugin that cannot work on many says so by
    /// declining, and the entry greys rather than disappearing.
    /// </summary>
    private void AddPluginSelectionCommands(ContextMenu menu, List<Selected> items)
    {
        if (_pluginSelectionCommands.Count == 0) return;

        var context = new SelectionContext(
            [.. items.Where(item => item.Placement is not null).Select(item => new ClipContext(
                item.Placement!.Id, item.Layer.Id, item.Placement.Clip.SourcePath, item.Placement.Start,
                item.Placement.Duration, item.Placement.Clip.Media.HasAudio, item.Placement.Clip.Media.HasVideo,
                item.Placement.InPoint, items.Count))],
            [.. items.Where(item => item.Graphic is not null).Select(item => new TextContext(
                item.Graphic!.Id, item.Layer.Id, item.Graphic.Text ?? "", item.Graphic.Start,
                item.Graphic.Duration, items.Count))]);

        menu.Items.Add(new Separator());
        foreach (var (pluginName, command) in _pluginSelectionCommands)
        {
            var allowed = Safe(command.AppliesTo, context);
            var item = Offer(command.Header, allowed,
                $"{pluginName} does not offer this for what is selected.",
                async () => await RunPluginCommandAsync(pluginName, () => command.Invoke(context)));
            item.ToolTip ??= $"From the {pluginName} plugin";
            menu.Items.Add(item);
        }
    }

    private static bool Safe(Func<SelectionContext, bool> predicate, SelectionContext context)
    {
        try { return predicate(context); } catch { return false; }
    }

    /// <summary>
    /// Adds an item to a menu, shown either way, and greyed with the reason when it cannot
    /// be used. A menu that changes shape between selections is harder to learn than one
    /// that greys; a greyed item that does not say why reads as broken.
    /// </summary>
    private static MenuItem Offer(string header, bool enabled, string? whyNot, Action invoke)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (!enabled && !string.IsNullOrWhiteSpace(whyNot)) item.ToolTip = whyNot;
        if (enabled) item.Click += (_, _) => invoke();
        // A disabled MenuItem swallows hover, so the reason has to be allowed through.
        if (!enabled) ToolTipService.SetShowOnDisabled(item, true);
        return item;
    }
}
