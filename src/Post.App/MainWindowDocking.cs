using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Post.App;

/// <summary>
/// Keeps the dockable panes behaving: the sizes they start at, the arrangement carried
/// between sessions, and the preview surviving being dragged into a floating window.
/// </summary>
public partial class MainWindow
{
    private static string DockLayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "layout.xml");

    private string? _defaultDockLayout;
    private readonly Dictionary<string, object> _paneContent = new(StringComparer.Ordinal);
    private TimeSpan? _previewPositionBeforeMove;
    private Uri? _previewSourceBeforeMove;
    private bool _previewWasPlayingBeforeMove;

    private void Docking_Loaded(object sender, RoutedEventArgs e)
    {
        if (_defaultDockLayout is not null) return;   // panes are re-created on restore

        // Remember what each pane holds, because restoring a saved arrangement rebuilds
        // the panes and asks for their content back by id.
        foreach (var anchorable in Docking.Layout.Descendents().OfType<LayoutAnchorable>())
            if (anchorable.ContentId is { } id && anchorable.Content is { } content)
                _paneContent[id] = content;

        _defaultDockLayout = SerializeDockLayout();
        if (!RestoreDockLayout()) ApplyDefaultPaneSizes();
        WatchPreviewForMoves();
    }

    /// <summary>
    /// The starting proportions. These are set here rather than in the markup because
    /// AvalonDock gives every pane an equal share of its panel until a real size is
    /// assigned to the pane itself.
    /// </summary>
    private void ApplyDefaultPaneSizes()
    {
        // AvalonDock finishes attaching its panes after this point and discards sizes set
        // too early, so wait for the first render and then set them.
        void Apply(object? sender, EventArgs e)
        {
            ContentRendered -= Apply;
            ApplyPaneSizesNow();
            Dispatcher.BeginInvoke(ApplyPaneSizesNow, System.Windows.Threading.DispatcherPriority.Background);
        }
        if (IsLoaded && PresentationSource.FromVisual(this) is not null) Apply(this, EventArgs.Empty);
        else ContentRendered += Apply;
    }

    private void ApplyPaneSizesNow()
    {
        // The row holding media and preview takes the same share as the layers below it.
        foreach (var panel in Docking.Layout.Descendents().OfType<LayoutPanel>())
            if (panel.Orientation == System.Windows.Controls.Orientation.Horizontal)
                panel.DockHeight = new GridLength(1, GridUnitType.Star);

        foreach (var pane in Docking.Layout.Descendents().OfType<LayoutAnchorablePane>())
        {
            switch (pane.Children.FirstOrDefault()?.ContentId)
            {
                case "media": pane.DockWidth = new GridLength(1, GridUnitType.Star); pane.DockMinWidth = 170; break;
                case "preview": pane.DockWidth = new GridLength(4, GridUnitType.Star); pane.DockMinWidth = 360; break;
                case "layers": pane.DockHeight = new GridLength(1, GridUnitType.Star); pane.DockMinHeight = 190; break;
                case "effects" or "equalizer": pane.DockWidth = new GridLength(360); pane.DockMinWidth = 330; break;
            }
        }
    }

    /// <summary>
    /// Wraps a pane's contents so they scroll. Docked into a narrow column, the effects
    /// browser is taller and wider than the space it gets, and without this the bottom of
    /// it simply could not be reached.
    /// </summary>
    private static ScrollViewer ScrollHost(FrameworkElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        // Horizontal scrolling would hand the contents unbounded width to measure in, so
        // every wrapping paragraph would ask for its full unwrapped length and the pane
        // would open far wider than it needs. Width is the viewport's; text wraps into it.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>Builds the contents of a tool pane the first time it is asked for.</summary>
    private FrameworkElement BuildToolPane(string contentId) => contentId switch
    {
        "effects" => BuildEffectsPanel(),
        "equalizer" => BuildEqualizerPanel(),
        _ => throw new ArgumentOutOfRangeException(nameof(contentId), contentId, "No such tool pane."),
    };

    /// <summary>
    /// Opens a tool pane, creating it on first use and floating it so it arrives as its
    /// own window. Docking it afterwards is a drag, and where it is docked is remembered.
    /// Panes are added on demand rather than declared and hidden, because hiding the last
    /// one in a pane removes that pane and leaves nowhere to put it back.
    /// </summary>
    private void OpenToolPane(string contentId, string title, double width, double height)
    {
        if (Pane(contentId) is { } existing)
        {
            existing.Show();
            existing.IsActive = true;
            return;
        }

        var anchorable = new LayoutAnchorable
        {
            Title = title, ContentId = contentId, Content = ScrollHost(BuildToolPane(contentId)),
            CanClose = true, CanAutoHide = false, FloatingWidth = width, FloatingHeight = height,
            // Without a position of its own a floating pane lands in the screen's corner.
            FloatingLeft = Left + Math.Max(0, (ActualWidth - width) / 2),
            FloatingTop = Top + Math.Max(0, (ActualHeight - height) / 2),
        };
        _paneContent[contentId] = anchorable.Content;
        anchorable.AddToLayout(Docking, AnchorableShowStrategy.Most | AnchorableShowStrategy.Right);
        anchorable.Float();
        anchorable.IsActive = true;
        anchorable.Hiding += ToolPaneHiding;
    }

    /// <summary>Puts a tool pane away, closing its floating window if it is in one.</summary>
    private void ClosePane(string contentId)
    {
        Pane(contentId)?.Hide();
        CloseEmptyFloatingWindows();
    }

    /// <summary>
    /// Hiding a pane hides the pane, not the window it floats in, which left the shortcut
    /// looking like it had done nothing while the window sat there. Anything left holding
    /// no visible pane is closed.
    /// </summary>
    private void CloseEmptyFloatingWindows()
    {
        foreach (var floater in Docking.FloatingWindows.ToArray())
        {
            var occupied = floater.Model?.Descendents().OfType<LayoutAnchorable>().Any(item => !item.IsHidden) ?? false;
            if (!occupied) floater.Close();
        }
    }

    private void ToolPaneHiding(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not LayoutAnchorable anchorable) return;
        if (anchorable.ContentId == "effects" && EffectsWindowMenuItem is not null) EffectsWindowMenuItem.IsChecked = false;
        if (anchorable.ContentId == "equalizer" && EqualizerWindowMenuItem is not null) EqualizerWindowMenuItem.IsChecked = false;
    }

    private string SerializeDockLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(Docking).Serialize(writer);
        return writer.ToString();
    }

    private void LoadDockLayout(TextReader reader)
    {
        var serializer = new XmlLayoutSerializer(Docking);
        // Content lives in the markup, not the layout file, so hand each pane back what
        // it had. A pane whose id is unknown is dropped rather than left blank.
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (args.Model.ContentId is not { } id) { args.Cancel = true; return; }
            if (!_paneContent.TryGetValue(id, out var content))
            {
                if (id is not ("effects" or "equalizer")) { args.Cancel = true; return; }
                content = _paneContent[id] = ScrollHost(BuildToolPane(id));   // reopened from a saved arrangement
                if (args.Model is LayoutAnchorable restored) restored.Hiding += ToolPaneHiding;
            }
            args.Content = content;
        };
        serializer.Deserialize(reader);
    }

    /// <summary>Loads the saved arrangement, reporting whether there was one to load.</summary>
    private bool RestoreDockLayout()
    {
        if (!File.Exists(DockLayoutPath)) return false;
        try
        {
            using var reader = new StreamReader(DockLayoutPath);
            LoadDockLayout(reader);
            return true;
        }
        catch { ResetDockLayout(); return true; }   // a layout from an older build is not worth keeping
    }

    private void SaveDockLayout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DockLayoutPath)!);
            File.WriteAllText(DockLayoutPath, SerializeDockLayout());
        }
        catch { }
    }

    private LayoutAnchorable? Pane(string contentId) => Docking.Layout.Descendents()
        .OfType<LayoutAnchorable>().FirstOrDefault(item => item.ContentId == contentId);

    /// <summary>Shows or hides one pane by its id, for the View and Tools menus.</summary>
    private void ShowPane(string contentId, bool show)
    {
        if (Pane(contentId) is not { } anchorable) return;
        if (show) { anchorable.Show(); anchorable.IsActive = true; } else anchorable.Hide();
    }

    /// <summary>
    /// True when a pane is on screen at all, docked or floating. IsHidden is the reliable
    /// test: a floating pane is very much visible but does not always report IsVisible.
    /// </summary>
    private bool IsPaneVisible(string contentId) => Pane(contentId) is { IsHidden: false };

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveDockLayout();
        base.OnClosing(e);
    }

    /// <summary>Puts every pane back where it started, and forgets the saved arrangement.</summary>
    private void ResetDockLayout()
    {
        try { if (File.Exists(DockLayoutPath)) File.Delete(DockLayoutPath); } catch { }
        if (_defaultDockLayout is null) return;
        using var reader = new StringReader(_defaultDockLayout);
        LoadDockLayout(reader);
        ApplyDefaultPaneSizes();
    }

    /// <summary>
    /// Dragging the preview into a floating window re-parents the player, and a
    /// MediaElement loses its source and position when it leaves the visual tree. This
    /// catches it on the way out and puts it back on the way in.
    /// </summary>
    private void WatchPreviewForMoves()
    {
        Player.Unloaded += (_, _) =>
        {
            if (Player.Source is null) return;
            _previewSourceBeforeMove = Player.Source;
            _previewPositionBeforeMove = Player.Position;
            _previewWasPlayingBeforeMove = _playing;
        };
        Player.Loaded += (_, _) =>
        {
            if (_previewSourceBeforeMove is null) return;
            var source = _previewSourceBeforeMove; var position = _previewPositionBeforeMove;
            var resume = _previewWasPlayingBeforeMove;
            _previewSourceBeforeMove = null; _previewPositionBeforeMove = null; _previewWasPlayingBeforeMove = false;

            // The element is not ready to seek in the same beat it is attached.
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (Player.Source is null) Player.Source = source;
                    if (position is { } value) Player.Position = value;
                    if (resume) Player.Play(); else Player.Pause();
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }
}
