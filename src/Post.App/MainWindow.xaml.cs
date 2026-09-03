using Post.Core;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Post.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan QuickGifMaximumDuration = TimeSpan.FromSeconds(15);
    private const string LayerDropHandledFormat = "Post.LayerDropHandled";
    private sealed class ProjectSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = "Untitled Project";
        public string? FilePath { get; set; }
        public List<ClipItem> Clips { get; } = [];
        public Dictionary<ClipItem, UndoHistory<ClipSnapshot>> Histories { get; } = [];
        public UndoHistory<ProjectEditSnapshot> ProjectHistory { get; } = new(50);
        public TimelineComposition Composition { get; } = new();
        public ClipItem? Current { get; set; }
        public bool CompositionInitialized { get; set; }
        public Guid? AutomaticPlacementId { get; set; }
    }

    private sealed record ProjectEditSnapshot(ClipState[] Clips, LayerState[] Layers, TimeSpan WorkspaceDuration,
        bool RenderWorkspaceTailAsBlack, Guid? ActiveLayerId, Guid? SelectedPlacementId, Guid? SelectedGraphicId,
        Guid? AutomaticPlacementId, TimeSpan SequencePosition, TimeSpan EditPosition, VideoEffect[] OutputEffects, AudioEqualizer Equalizer);
    private sealed record ClipState(ClipItem Clip, ClipSnapshot Snapshot);
    private sealed record LayerState(Guid Id, string Name, bool IsVisible, bool IsMuted, bool MuteLeftChannel, bool MuteRightChannel, TimelineLayerKind Kind,
        PlacementState[] Placements, GraphicState[] Graphics, double Volume = 1);
    private sealed record PlacementState(Guid Id, ClipItem Clip, TimeSpan Start, TimeSpan InPoint, TimeSpan? Length, KeyframeState[] Keyframes, VideoEffect[] Effects);
    private sealed record GraphicState(Guid Id, GraphicsOverlayKind Kind, string Text, string? ImagePath,
        string? RenderedImagePath, string FontFamily, double FontSize, string Foreground, string Background,
        string FillColor1, string FillColor2, bool UseSecondFillColor, GraphicGradientKind GradientKind, double GradientAngle,
        double Opacity, bool PreserveAspectRatio, double X, double Y, double Width, double Height,
        TimeSpan Start, TimeSpan Duration, KeyframeState[] Keyframes);
    private sealed record KeyframeState(Guid Id, KeyframeProperty Property, TimeSpan Offset, double Value, KeyframeInterpolation Interpolation);
    private sealed record PlacementClipboard(ClipItem Clip, TimelineLayerKind Kind, TimeSpan InPoint, TimeSpan Length, KeyframeState[] Keyframes);

    private readonly FfmpegTools _tools;
    private readonly MediaProbeService _probe;
    private readonly MediaEngine _engine;
    private readonly List<ProjectSession> _projects = [];
    private ProjectSession _project = new();
    private List<ClipItem> _clips => _project.Clips;
    private Dictionary<ClipItem, UndoHistory<ClipSnapshot>> _histories => _project.Histories;
    private TimelineComposition _composition => _project.Composition;
    private Point? _dragStart;
    private MediaSegment? _dragSegment;
    private Point? _trayDragStart;
    private ClipItem? _trayDragClip;
    private bool _trayDragInProgress;
    private readonly HashSet<ClipItem> _selectedMedia = [];
    private ClipItem? _mediaSelectionAnchor;
    private Point? _placementDragStart;
    private TimelinePlacement? _dragPlacement;
    private TimeSpan _placementDragOffset;
    private TimeSpan _placementDragOriginalStart;
    private Guid? _placementDragOriginalLayerId;
    private Guid? _selectedPlacementId;
    private ImageSource? _waveformImage;
    private readonly Dictionary<ClipItem, ImageSource?> _clipWaveforms = [];
    private readonly Dictionary<ClipItem, int> _clipWaveformWidths = [];
    private readonly Dictionary<ClipItem, ImageSource?> _clipFilmstrips = [];
    private readonly Dictionary<ClipItem, int> _clipFilmstripCounts = [];
    private readonly Dictionary<ClipItem, Task> _clipVisualTasks = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _filmstripZoomTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string _cache = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "Cache");
    private AppSettings _settings = AppSettings.Load();
    private ClipItem? _current { get => _project.Current; set => _project.Current = value; }
    private bool _playing, _loop, _timelineUpdating, _muted;
    private double _timelineZoom = 1;
    private AspectPreset _aspect = AspectPreset.Original;
    private ExportMode _mode = ExportMode.Lossless;
    private CancellationTokenSource? _work;
    private TimeSpan _position;
    private TimeSpan _sequencePosition;
    private TimeSpan _editPosition;
    private TimeSpan _viewStart;
    private int _activeSegmentIndex;
    private Guid? _selectedSegmentId;
    private DateTime _ignorePlayerPositionUntil;
    private Guid? _activeLayerId;
    private bool _compositionInitialized { get => _project.CompositionInitialized; set => _project.CompositionInitialized = value; }
    private bool _compositionPreviewActive, _playWhenMediaOpened;
    private bool _livePreviewActive;
    private bool _projectMediaPreviewActive;
    private bool _projectMediaAudioPreviewActive;
    private Guid? _soloPreviewLayerId;
    private Guid? _soloPreviewPlacementId;
    private readonly Stopwatch _livePreviewClock = new();
    private TimeSpan _livePreviewStart;
    private readonly Dictionary<Guid, MediaElement> _livePlayers = [];
    private readonly MediaPlayer _audioPreviewPlayer = new();
    private readonly Dictionary<Guid, MediaPlayer> _liveAudioPlayers = [];
    private readonly Dictionary<Guid, TimeSpan> _liveDesiredPositions = [];
    private readonly HashSet<Guid> _liveOpenedPlayers = [];
    private readonly HashSet<Guid> _livePlayingIds = [];
    private readonly Dictionary<Guid, DateTime> _liveLastCorrections = [];
    private readonly Dictionary<Guid, TaskCompletionSource<bool>> _liveReady = [];
    private readonly Dictionary<Guid, (Canvas Lane, FrameworkElement Marker)> _layerPlayheads = [];
    private readonly Dictionary<Guid, (Canvas Lane, FrameworkElement Marker)> _layerEditCarets = [];
    private Canvas? _layerTimeRulerLane;
    private FrameworkElement? _layerTimeRulerPlayhead;
    private FrameworkElement? _layerTimeRulerEditCaret;
    private readonly Image _scrubFrameView = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, Stretch = Stretch.Uniform };
    private readonly Canvas _liveGraphicsHost = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _scrubFrameWork;
    private long _scrubFrameRequest;
    private GraphicsOverlay? _dragGraphic;
    private TimeSpan _graphicDragOffset;
    private Guid? _selectedGraphicId;
    private bool _startingLivePreview;
    private double _exportVolume = 1;
    private double _exportSpeed = 1;
    private int _customSizeMb = 25;
    private double _layerHeaderWidth = 215;
    private int _visualZoomGeneration;
    private PlacementClipboard? _placementClipboard;
    private GraphicsOverlay? _graphicClipboard;
    private double _mediaPanelWidth = 280;
    private GridLength _layersPanelHeight = new(300);
    private KeyframeEditor? _keyframeWindow;
    private EffectsWindow? _effectsWindow;

    public MainWindow()
    {
        _projects.Add(_project);
        InitializeComponent();
        Panel.SetZIndex(_scrubFrameView, 40); PreviewSurface.Children.Add(_scrubFrameView);
        Panel.SetZIndex(_liveGraphicsHost, 50); PreviewSurface.Children.Add(_liveGraphicsHost);
        Player.ScrubbingEnabled = true;
        Panel.SetZIndex(WaveformCanvas, 3); Panel.SetZIndex(CutOverlay, 4);
        _tools = FfmpegLocator.Find(); var runner = new ProcessRunner(); _probe = new(_tools, runner); _engine = new(_tools, runner); _engine.EncoderPreference = _settings.VideoEncoder;
        EnsureProjectHistory(); InitializeExportJobs();
        _timer.Tick += Timer_Tick; _timer.Start(); PreviewVolume.Value = _settings.PreviewVolume;
        _audioPreviewPlayer.MediaOpened += AudioPreviewPlayer_MediaOpened;
        _audioPreviewPlayer.MediaEnded += (_, _) => { if (_projectMediaAudioPreviewActive) { if (_loop) { _audioPreviewPlayer.Position = TimeSpan.Zero; _audioPreviewPlayer.Play(); } else Pause(); } };
        _audioPreviewPlayer.MediaFailed += (_, e) => { if (_projectMediaAudioPreviewActive) { _playing = false; PlayButton.Content = "▶"; MessageBox.Show(this, $"Audio preview failed.\n{e.ErrorException?.Message}", "Post", MessageBoxButton.OK, MessageBoxImage.Error); } };
        _filmstripZoomTimer.Tick += async (_, _) => { _filmstripZoomTimer.Stop(); var generation = _visualZoomGeneration; await RefreshZoomFilmstripsAsync(generation); };
        Loaded += async (_, _) =>
        {
            try { ShellIntegration.EnsureRegistered(); } catch { }
            TimelineArea.SizeChanged += (_, _) => DrawCuts();
            SourceTimelineScroll.SizeChanged += (_, _) => UpdateTimelineWidths();
            CompositionScroll.SizeChanged += (_, _) => RefreshLayerStack();
            EnsureComposition(); RefreshTray(); UpdateTimelineWidths();
            var paths = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            var projects = paths.Where(IsProjectFile).ToArray(); if (projects.Length > 0) await OpenProjectFilesAsync(projects);
            var media = paths.Except(projects, StringComparer.OrdinalIgnoreCase).ToArray(); if (media.Length > 0) await LoadFilesAsync(media);
        };
    }

    private static T? FindVisualChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T logicalValue && predicate(logicalValue)) return logicalValue;
            if (FindVisualChild<T>(child, predicate) is { } logicalNested) return logicalNested;
        }
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value && predicate(value)) return value;
            if (FindVisualChild<T>(child, predicate) is { } nested) return nested;
        }
        return null;
    }

    private static void ApplyPrimaryButtonColors(Button button)
    {
        button.Background = new SolidColorBrush(Color.FromRgb(24, 101, 120));
        button.Foreground = Brushes.White;
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(75, 207, 220));
        button.FontWeight = FontWeights.SemiBold;
    }

    private async Task LoadFilesAsync(IEnumerable<string> paths, bool initializeComposition = true)
    {
        var files = paths.Where(MediaProbeService.IsSupported).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (files.Length == 0) return;
        await RunBusyAsync("Inspecting clips…", async token =>
        {
            foreach (var path in files)
            {
                if (_clips.Any(c => c.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                var media = await _probe.ProbeAsync(path, token); var clip = new ClipItem { SourcePath = path, Media = media };
                clip.Segments.Add(new MediaSegment { SourceStart = TimeSpan.Zero, SourceEnd = media.Duration });
                _clips.Add(clip); var history = new UndoHistory<ClipSnapshot>(30); history.Push(clip.Snapshot()); _histories[clip] = history;
            }
            if (initializeComposition) EnsureComposition();
            CommitProjectEdit(); RefreshTray(); if (_current is null && _clips.FirstOrDefault(clip => !clip.Media.IsStillImage) is { } previewable) await SwitchClipAsync(previewable, token);
        });
        if (_current is null && _composition.HasVisibleGraphics) await StartLivePreviewAsync(false);
    }

    private void RefreshTray()
    {
        RefreshProjectTabs(); RefreshProjectMedia();
    }

    private void RefreshProjectTabs()
    {
        if (ProjectTabBar is null) return; ProjectTabBar.Children.Clear();
        foreach (var project in _projects)
        {
            var button = new Button { Content = project.Name, Tag = project, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(2, 0, 2, 0), Background = (Brush)FindResource("Panel2Brush"), BorderBrush = (Brush)FindResource("BorderBrush"), Foreground = Brushes.White, FontWeight = FontWeights.Normal, ToolTip = project.FilePath ?? "Unsaved Post project" };
            if (ReferenceEquals(project, _project)) ApplyPrimaryButtonColors(button);
            button.Click += async (_, _) => await SwitchProjectAsync(project);
            var menu = new ContextMenu(); var close = new MenuItem { Header = "Close project tab" }; close.Click += async (_, _) => await CloseProjectAsync(project); menu.Items.Add(close); button.ContextMenu = menu; ProjectTabBar.Children.Add(button);
        }
    }

    private void RefreshProjectMedia()
    {
        _selectedMedia.RemoveWhere(clip => !_clips.Contains(clip));
        if (ProjectMediaList is null) return; ProjectMediaList.Children.Clear();
        foreach (var clip in _clips)
        {
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); content.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(18, 46, 72)), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Child = CreateMediaTypeIcon(clip) };
            var details = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch }; details.Children.Add(new TextBlock { Text = clip.DisplayName, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch }); details.Children.Add(new TextBlock { Text = $"{clip.Media.Resolution}  •  {clip.Media.Duration:mm\\:ss}", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch }); Grid.SetColumn(details, 1); content.Children.Add(icon); content.Children.Add(details);
            var button = new Button { Content = content, Tag = clip, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6), Margin = new Thickness(1, 2, 1, 2), ToolTip = $"Click to preview\nCtrl+Click selects individual media\nShift+Click selects a range\nDrag the selection into Layers\n{clip.SourcePath}" };
            ApplyMediaSelectionVisual(button, clip);
            button.Click += async (_, _) => { if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) await PreviewProjectMediaAsync(clip); };
            button.PreviewMouseLeftButtonDown += (_, e) => { SelectProjectMedia(clip); _trayDragStart = e.GetPosition(button); _trayDragClip = clip; };
            button.PreviewMouseMove += (_, e) =>
            {
                if (_trayDragInProgress || _trayDragStart is not { } start || _trayDragClip != clip || e.LeftButton != MouseButtonState.Pressed || Math.Abs(e.GetPosition(button).X - start.X) < 5) return;
                var selected = _clips.Where(item => _selectedMedia.Contains(item)).ToArray(); if (selected.Length == 0) selected = [clip];
                _trayDragInProgress = true; _trayDragStart = null; _trayDragClip = null;
                try { DragDrop.DoDragDrop(button, selected.Length == 1 ? selected[0] : selected, DragDropEffects.Copy); }
                finally { _trayDragInProgress = false; }
            };
            var menu = new ContextMenu();
            var previewLabel = clip.Media.IsStillImage ? "Preview Image" : clip.Media.HasVideo ? "Preview Video" : "Preview Audio";
            var preview = new MenuItem { Header = previewLabel }; preview.Click += async (_, _) => await PreviewProjectMediaAsync(clip); menu.Items.Add(preview); menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "Remove media from project" }; remove.Click += (_, _) => { if (MessageBox.Show(this, $"Remove {clip.DisplayName} from this project?\nThe source file will not be deleted.", "Remove project media", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) RemoveClip(clip); }; menu.Items.Add(remove); button.ContextMenu = menu;
            ProjectMediaList.Children.Add(button);
        }
    }

    private void SelectProjectMedia(ClipItem clip)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (shift && _mediaSelectionAnchor is not null && _clips.Contains(_mediaSelectionAnchor))
        {
            if (!ctrl) _selectedMedia.Clear(); var start = _clips.IndexOf(_mediaSelectionAnchor); var end = _clips.IndexOf(clip); if (start > end) (start, end) = (end, start);
            for (var i = start; i <= end; i++) _selectedMedia.Add(_clips[i]);
        }
        else if (ctrl)
        {
            if (!_selectedMedia.Add(clip)) _selectedMedia.Remove(clip); _mediaSelectionAnchor = clip;
        }
        else
        {
            if (!_selectedMedia.Contains(clip) || _selectedMedia.Count <= 1) { _selectedMedia.Clear(); _selectedMedia.Add(clip); }
            _mediaSelectionAnchor = clip;
        }
        foreach (var button in ProjectMediaList.Children.OfType<Button>()) if (button.Tag is ClipItem item) ApplyMediaSelectionVisual(button, item);
    }

    private void ApplyMediaSelectionVisual(Button button, ClipItem clip)
    {
        var selected = _selectedMedia.Contains(clip); button.Background = selected ? new SolidColorBrush(Color.FromRgb(25, 61, 96)) : (Brush)FindResource("Panel2Brush");
        button.BorderBrush = selected ? (Brush)FindResource("BlueBrush") : (Brush)FindResource("BorderBrush"); button.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    private FrameworkElement CreateMediaTypeIcon(ClipItem clip)
    {
        var blue = (Brush)FindResource("BlueBrush");
        if (!clip.Media.HasVideo && !clip.Media.IsStillImage)
            return new TextBlock { Text = "♫", FontSize = 19, Foreground = blue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var canvas = new Canvas { Width = 24, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Rectangle { Width = 23, Height = 18, RadiusX = 1.5, RadiusY = 1.5, Stroke = blue, StrokeThickness = 1.7 });
        if (clip.Media.IsStillImage)
        {
            canvas.Children.Add(new Ellipse { Width = 4, Height = 4, Stroke = blue, StrokeThickness = 1.4, Margin = new Thickness(15, 3, 0, 0) });
            canvas.Children.Add(new System.Windows.Shapes.Path { Stroke = blue, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, Data = Geometry.Parse("M 3,15 L 8,10 L 12,13 L 17,7 L 21,11") });
        }
        else
        {
            canvas.Children.Add(new Rectangle { Width = 13, Height = 13, Stroke = blue, StrokeThickness = 1.3, Margin = new Thickness(5, 2.5, 0, 0) });
            foreach (var y in new[] { 2.0, 7.0, 12.0 })
            {
                canvas.Children.Add(new Rectangle { Width = 2.5, Height = 3, Fill = blue, Margin = new Thickness(1, y, 0, 0) });
                canvas.Children.Add(new Rectangle { Width = 2.5, Height = 3, Fill = blue, Margin = new Thickness(19.5, y, 0, 0) });
            }
        }
        return canvas;
    }

    private async Task SwitchProjectAsync(ProjectSession project)
    {
        if (ReferenceEquals(project, _project)) return;
        Pause(); StopLivePreview(); Player.Stop(); _project = project; _compositionPreviewActive = false; _activeLayerId = _composition.Layers.FirstOrDefault()?.Id; EnsureComposition(); RefreshTray();
        if (_current is not null) await SwitchClipAsync(_current, CancellationToken.None);
        else
        {
            Player.Source = null; DropPanel.Visibility = Visibility.Visible; MarkerText.Text = "KEEP: 00:00.000"; CurrentText.Text = "00:00.000"; DurationText.Text = " / 00:00"; _waveformImage = null; DrawCuts(); RefreshLayerStack();
        }
    }

    private async Task CloseProjectAsync(ProjectSession project)
    {
        if (project.FilePath is null && project.Clips.Count > 0 && MessageBox.Show(this, $"Close {project.Name} without saving its project file?\nSource media will not be deleted.", "Close project", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var wasCurrent = ReferenceEquals(project, _project); _projects.Remove(project);
        if (_projects.Count == 0) _projects.Add(new ProjectSession { Name = "Untitled Project" });
        if (wasCurrent) { _project = _projects[0]; await SwitchProjectContentsAsync(); } else RefreshProjectTabs();
    }

    private async Task SwitchProjectContentsAsync()
    {
        Pause(); StopLivePreview(); Player.Stop(); _compositionPreviewActive = false; _activeLayerId = _composition.Layers.FirstOrDefault()?.Id; EnsureComposition(); RefreshTray();
        if (_current is not null) await SwitchClipAsync(_current, CancellationToken.None); else { Player.Source = null; DropPanel.Visibility = Visibility.Visible; _waveformImage = null; DrawCuts(); RefreshLayerStack(); }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var project = new ProjectSession { Name = $"Untitled Project {_projects.Count + 1}" }; _projects.Add(project); _project = project; EnsureProjectHistory(); await SwitchProjectContentsAsync();
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Post Project|*.post|Legacy ClipEdit Project|*.clipedit|All files|*.*" }; if (dialog.ShowDialog(this) != true) return;
        await OpenProjectFilesAsync(dialog.FileNames);
    }

    private async Task OpenProjectFilesAsync(IEnumerable<string> paths)
    {
        var projectPaths = paths.Where(path => File.Exists(path) && IsProjectFile(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (projectPaths.Length == 0) return;
        var loaded = new List<ProjectSession>(); var missing = new List<string>();
        await RunBusyAsync("Opening projects…", async token =>
        {
            foreach (var path in projectPaths)
            {
                var dto = await PostProjectStore.LoadAsync(path, token);
                var project = new ProjectSession { Name = string.IsNullOrWhiteSpace(dto.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : dto.Name, FilePath = path, CompositionInitialized = true };
                var clipMap = new ClipItem?[dto.Clips.Length];
                for (var i = 0; i < dto.Clips.Length; i++)
                {
                    var saved = dto.Clips[i]; if (!File.Exists(saved.SourcePath)) { missing.Add(saved.SourcePath); continue; }
                    var media = await _probe.ProbeAsync(saved.SourcePath, token); var clip = new ClipItem { SourcePath = saved.SourcePath, Media = media };
                    foreach (var segment in saved.Segments) { var start = ClampTime(TimeSpan.FromSeconds(segment.StartSeconds), TimeSpan.Zero, media.Duration); var end = ClampTime(TimeSpan.FromSeconds(segment.EndSeconds), start, media.Duration); if (end > start) clip.Segments.Add(new MediaSegment { SourceStart = start, SourceEnd = end }); }
                    if (clip.Segments.Count == 0) clip.Segments.Add(new MediaSegment { SourceStart = TimeSpan.Zero, SourceEnd = media.Duration });
                    project.Clips.Add(clip); clipMap[i] = clip; var history = new UndoHistory<ClipSnapshot>(30); history.Push(clip.Snapshot()); project.Histories[clip] = history;
                }
                project.Composition.WorkspaceDuration = TimeSpan.FromSeconds(Math.Max(1, dto.WorkspaceSeconds)); project.Composition.RenderWorkspaceTailAsBlack = dto.RenderWorkspaceTailAsBlack;
                foreach (var savedLayer in dto.Layers)
                {
                    var legacyAudioMute = savedLayer.Kind == TimelineLayerKind.Audio && savedLayer.IsMuted && !savedLayer.MuteLeftChannel && !savedLayer.MuteRightChannel;
                    var layer = new TimelineLayer { Name = savedLayer.Name, IsVisible = savedLayer.IsVisible, IsMuted = savedLayer.Kind == TimelineLayerKind.Audio ? false : savedLayer.IsMuted, MuteLeftChannel = savedLayer.MuteLeftChannel || legacyAudioMute, MuteRightChannel = savedLayer.MuteRightChannel || legacyAudioMute, Kind = savedLayer.Kind, Volume = savedLayer.Volume }; project.Composition.Layers.Add(layer);
                    foreach (var savedPlacement in savedLayer.Placements) if (savedPlacement.ClipIndex >= 0 && savedPlacement.ClipIndex < clipMap.Length && clipMap[savedPlacement.ClipIndex] is { } clip) { var placement = TimelineOperations.AddPlacement(layer, clip, TimeSpan.FromSeconds(Math.Max(0, savedPlacement.StartSeconds))); placement.InPoint = TimeSpan.FromSeconds(Math.Max(0, savedPlacement.InSeconds)); placement.Length = savedPlacement.DurationSeconds is { } length ? TimeSpan.FromSeconds(Math.Max(0, length)) : null; foreach (var keyframe in savedPlacement.Keyframes ?? []) placement.Keyframes.Add(FromDocument(keyframe, placement.Duration)); foreach (var effect in savedPlacement.Effects ?? []) placement.Effects.Add(FromDocument(effect)); }
                    foreach (var saved in savedLayer.Graphics ?? [])
                    {
                        if (saved.Kind is GraphicsOverlayKind.Image or GraphicsOverlayKind.Lottie && (string.IsNullOrWhiteSpace(saved.ImagePath) || !File.Exists(saved.ImagePath))) { if (!string.IsNullOrWhiteSpace(saved.ImagePath)) missing.Add(saved.ImagePath); continue; }
                        var graphic = new GraphicsOverlay { Kind = saved.Kind, Text = saved.Text, ImagePath = saved.ImagePath, FontFamily = saved.FontFamily, FontSize = saved.FontSize, Foreground = saved.Foreground, Background = saved.Background, FillColor1 = saved.FillColor1, FillColor2 = saved.FillColor2, UseSecondFillColor = saved.UseSecondFillColor, GradientKind = saved.GradientKind, GradientAngle = saved.GradientAngle, Opacity = saved.Opacity, PreserveAspectRatio = saved.PreserveAspectRatio, X = saved.X, Y = saved.Y, Width = saved.Width, Height = saved.Height, Start = TimeSpan.FromSeconds(Math.Max(0, saved.StartSeconds)), Duration = TimeSpan.FromSeconds(Math.Max(.1, saved.DurationSeconds)) };
                        foreach (var keyframe in saved.Keyframes ?? []) graphic.Keyframes.Add(FromDocument(keyframe, graphic.Duration)); layer.Graphics.Add(graphic);
                    }
                }
                foreach (var effect in dto.OutputEffects ?? []) project.Composition.OutputEffects.Add(FromDocument(effect));
                if (dto.Equalizer is { } savedEqualizer && savedEqualizer.Bands.Length > 0)
                {
                    project.Composition.Equalizer.IsEnabled = savedEqualizer.IsEnabled; project.Composition.Equalizer.GainDb = savedEqualizer.GainDb;
                    project.Composition.Equalizer.Bands.Clear();
                    foreach (var band in savedEqualizer.Bands) project.Composition.Equalizer.Bands.Add(new AudioEqualizerBand { FrequencyHz = band.FrequencyHz, GainDb = band.GainDb, Width = band.Width });
                }
                NormalizeGraphicsLayers(project.Composition);
                project.Current = project.Clips.FirstOrDefault(clip => !clip.Media.IsStillImage); project.ProjectHistory.Push(CaptureProjectSnapshot(project)); RememberRecentProject(path); loaded.Add(project);
            }
        });
        if (loaded.Count == 0) return;
        if (_projects.Count == 1 && _project.FilePath is null && _project.Clips.Count == 0) _projects.Clear();
        _projects.AddRange(loaded); _project = loaded[^1]; await SwitchProjectContentsAsync();
        if (missing.Count > 0) MessageBox.Show(this, $"The project opened, but {missing.Distinct(StringComparer.OrdinalIgnoreCase).Count()} source file(s) could not be found. Their timeline placements were skipped.", "Missing project media", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(_project);

    private async void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        var oldPath = _project.FilePath; _project.FilePath = null; await SaveProjectAsync(_project);
        if (_project.FilePath is null) _project.FilePath = oldPath;
    }

    private void RecentProjectsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RecentProjectsMenu.Items.Clear();
        var paths = _projects.Select(project => project.FilePath).Concat(_settings.RecentProjectPaths).Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().Take(10).ToArray();
        if (paths.Length == 0) { RecentProjectsMenu.Items.Add(new MenuItem { Header = "No recent projects", IsEnabled = false, Style = (Style)FindResource("DesktopMenuItemStyle") }); return; }
        foreach (var path in paths)
        {
            var item = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(path), ToolTip = path, Style = (Style)FindResource("DesktopMenuItemStyle") };
            item.Click += async (_, _) => await OpenProjectFilesAsync([path]); RecentProjectsMenu.Items.Add(item);
        }
    }

    private static void NormalizeGraphicsLayers(TimelineComposition composition)
    {
        foreach (var layer in composition.Layers.Where(item => item.Kind == TimelineLayerKind.Graphics && item.Graphics.Count > 0))
            if (string.IsNullOrWhiteSpace(layer.Name) || layer.Name == "Layer") layer.Name = GraphicKindName(layer.Graphics[0].Kind);
    }

    private async Task SaveProjectAsync(ProjectSession project)
    {
        var path = project.FilePath;
        if (path is null)
        {
            var dialog = new SaveFileDialog { FileName = $"{project.Name}.post", Filter = "Post Project|*.post", DefaultExt = "post", AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder }; if (dialog.ShowDialog(this) != true) return; path = dialog.FileName;
        }
        var clipIndexes = project.Clips.Select((clip, index) => (clip, index)).ToDictionary(item => item.clip, item => item.index);
        var dto = new PostProjectDocument(1, System.IO.Path.GetFileNameWithoutExtension(path), project.Composition.WorkspaceDuration.TotalSeconds, project.Composition.RenderWorkspaceTailAsBlack,
            project.Clips.Select(clip => new ProjectClipDocument(clip.SourcePath, clip.Segments.Select(segment => new ProjectSegmentDocument(segment.SourceStart.TotalSeconds, segment.SourceEnd.TotalSeconds)).ToArray())).ToArray(),
            project.Composition.Layers.Select(layer => new ProjectLayerDocument(layer.Name, layer.IsVisible, layer.IsMuted,
                layer.Placements.Where(placement => clipIndexes.ContainsKey(placement.Clip)).Select(placement => new ProjectPlacementDocument(clipIndexes[placement.Clip], placement.Start.TotalSeconds, placement.InPoint.TotalSeconds, placement.Duration.TotalSeconds, placement.Keyframes.Select(ToDocument).ToArray(), placement.Effects.Select(ToDocument).ToArray())).ToArray(),
                layer.Kind, layer.Graphics.Select(graphic => new ProjectGraphicsDocument(graphic.Kind, graphic.Text, graphic.ImagePath, graphic.FontFamily, graphic.FontSize, graphic.Foreground, graphic.Background, graphic.Opacity, graphic.PreserveAspectRatio, graphic.X, graphic.Y, graphic.Width, graphic.Height, graphic.Start.TotalSeconds, graphic.Duration.TotalSeconds, graphic.Keyframes.Select(ToDocument).ToArray(), graphic.FillColor1, graphic.FillColor2, graphic.UseSecondFillColor, graphic.GradientKind, graphic.GradientAngle)).ToArray(), layer.MuteLeftChannel, layer.MuteRightChannel, layer.Volume)).ToArray(),
            project.Composition.OutputEffects.Select(ToDocument).ToArray(),
            new ProjectEqualizerDocument(project.Composition.Equalizer.IsEnabled, project.Composition.Equalizer.GainDb,
                project.Composition.Equalizer.Bands.Select(band => new ProjectEqualizerBandDocument(band.FrequencyHz, band.GainDb, band.Width)).ToArray()));
        await PostProjectStore.SaveAsync(path, dto); project.FilePath = path; project.Name = System.IO.Path.GetFileNameWithoutExtension(path); RememberRecentProject(path); RefreshProjectTabs();
    }

    private static ProjectEffectDocument ToDocument(VideoEffect item) => new(item.Kind, item.IsEnabled, item.Amount, item.Brightness, item.Contrast, item.Saturation, item.Gamma, item.Hue, item.FilePath);
    private static VideoEffect FromDocument(ProjectEffectDocument item) => new() { Kind = item.Kind, IsEnabled = item.IsEnabled, Amount = item.Amount, Brightness = item.Brightness, Contrast = item.Contrast, Saturation = item.Saturation, Gamma = item.Gamma, Hue = item.Hue, FilePath = item.FilePath };
    private static ProjectKeyframeDocument ToDocument(AnimationKeyframe item) => new(item.Property, item.Offset.TotalSeconds, item.Value, item.Interpolation);
    private static AnimationKeyframe FromDocument(ProjectKeyframeDocument item, TimeSpan duration) => new() { Property = item.Property, Offset = ClampTime(TimeSpan.FromSeconds(Math.Max(0, item.OffsetSeconds)), TimeSpan.Zero, duration), Value = item.Value, Interpolation = item.Interpolation };

    private void RememberRecentProject(string path)
    {
        _settings = _settings with { RecentProjectPaths = new[] { path }.Concat(_settings.RecentProjectPaths).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() }; _settings.Save();
    }

    private void EnsureComposition()
    {
        if (_clips.Count == 0) { RefreshLayerStack(); return; }
        if (!_compositionInitialized && _clips.Count > 0)
        {
            InitializeCompositionFrom(_clips[0]);
            _compositionInitialized = true;
        }
        RefreshLayerStack();
    }

    private void InitializeCompositionFrom(ClipItem item)
    {
        if (_composition.Layers.Count > 0) return;
        if (item.Media.IsStillImage)
        {
            var layer = new TimelineLayer { Name = "Image 1", Kind = TimelineLayerKind.Graphics };
            var graphic = CreateImageGraphic(item.SourcePath, TimeSpan.Zero); layer.Graphics.Add(graphic); _composition.Layers.Add(layer);
            _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _composition.WorkspaceDuration = graphic.Duration; return;
        }
        var audioOnly = !item.Media.HasVideo && item.Media.HasAudio;
        var mediaLayer = new TimelineLayer { Name = audioOnly ? "Audio 1" : "Layer 1", Kind = audioOnly ? TimelineLayerKind.Audio : TimelineLayerKind.Video };
        _composition.Layers.Add(mediaLayer); _activeLayerId = mediaLayer.Id; _project.AutomaticPlacementId = TimelineOperations.AddPlacement(mediaLayer, item, TimeSpan.Zero).Id;
        _composition.WorkspaceDuration = item.SelectedDuration;
    }

    private GraphicsOverlay CreateImageGraphic(string path, TimeSpan start)
    {
        var graphic = new GraphicsOverlay { Kind = GraphicsOverlayKind.Image, ImagePath = path, Start = start, Duration = TimeSpan.FromSeconds(5) };
        try { var bitmap = new BitmapImage(new Uri(path)); if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0) { graphic.Width = .3; graphic.Height = Math.Clamp(graphic.Width * bitmap.PixelHeight / bitmap.PixelWidth * 16 / 9, .04, .7); } } catch { }
        return graphic;
    }

    private TimelineLayer ActiveLayer
    {
        get
        {
            if (_composition.Layers.Count == 0)
            {
                if (_clips.Count == 0) throw new InvalidOperationException("Add project media before creating a timeline placement.");
                var layer = new TimelineLayer { Name = "Layer 1" }; _composition.Layers.Add(layer); _activeLayerId = layer.Id;
            }
            return _composition.Layers.FirstOrDefault(layer => layer.Id == _activeLayerId && layer.Kind == TimelineLayerKind.Video)
                ?? _composition.Layers.FirstOrDefault(layer => layer.Kind == TimelineLayerKind.Video)
                ?? throw new InvalidOperationException("Add a video layer before placing media.");
        }
    }

    private bool HasCustomComposition
    {
        get
        {
            var placements = _composition.Layers.SelectMany(layer => layer.Placements).ToArray();
            // Effects and keyframes live on the placement, and only the composition
            // renderer applies them, so their presence rules out the plain-clip path.
            return _composition.HasVisibleGraphics || _composition.Layers.Count > 1 || placements.Length != 1 || _current is null || !ReferenceEquals(placements[0].Clip, _current) || placements[0].Start > TimeSpan.Zero || placements[0].InPoint > TimeSpan.Zero || placements[0].Duration != placements[0].Clip.SelectedDuration || _composition.RenderWorkspaceTailAsBlack || placements[0].Effects.Count > 0 || placements[0].Keyframes.Count > 0;
        }
    }

    private void InvalidateCompositionPreview()
    {
        if (_livePreviewActive) { Pause(); StopLivePreview(); }
        if (!_compositionPreviewActive) return;
        _compositionPreviewActive = false; _playing = false; PlayButton.Content = "▶";
        if (_current?.PreviewPath is { } preview)
        {
            var segment = _current.Segments.FirstOrDefault(); _activeSegmentIndex = 0; _selectedSegmentId = segment?.Id; _position = segment?.SourceStart ?? TimeSpan.Zero; _sequencePosition = TimeSpan.Zero; _editPosition = TimeSpan.Zero;
            Player.Stop(); Player.Source = new Uri(preview); Player.Position = _position; Player.Pause(); DurationText.Text = $" / {_current.Media.Duration:mm\\:ss}";
        }
    }

    private void RemoveClip(ClipItem clip)
    {
        var index = _clips.IndexOf(clip); _clips.Remove(clip); _histories.Remove(clip); RefreshTray();
        _clipWaveforms.Remove(clip); _clipWaveformWidths.Remove(clip); _clipFilmstrips.Remove(clip); _clipFilmstripCounts.Remove(clip); _clipVisualTasks.Remove(clip);
        foreach (var layer in _composition.Layers) for (var i = layer.Placements.Count - 1; i >= 0; i--) if (ReferenceEquals(layer.Placements[i].Clip, clip)) layer.Placements.RemoveAt(i);
        InvalidateCompositionPreview(); RefreshLayerStack();
        if (_current == clip) { _current = null; Player.Stop(); if (_clips.Count > 0) _ = SwitchClipAsync(_clips[Math.Min(index, _clips.Count - 1)], CancellationToken.None); else DropPanel.Visibility = Visibility.Visible; }
    }

    private async Task SwitchClipAsync(ClipItem clip, CancellationToken token)
    {
        CancelScrubFrame();
        _projectMediaPreviewActive = false; _projectMediaAudioPreviewActive = false; _audioPreviewPlayer.Close(); _soloPreviewLayerId = null; _soloPreviewPlacementId = null;
        StopLivePreview(); _playing = false; _compositionPreviewActive = false; PlayButton.Content = "▶"; Player.Stop(); _current = clip; DropPanel.Visibility = Visibility.Collapsed;
        clip.PreviewPath ??= await _engine.CreatePreviewProxyAsync(clip.Media, _cache, token); Player.Source = new Uri(clip.PreviewPath); Player.Volume = PreviewVolume.Value;
        _activeSegmentIndex = 0; _selectedSegmentId = clip.Segments[0].Id; _sequencePosition = TimeSpan.Zero; _editPosition = TimeSpan.Zero; _viewStart = TimeSpan.Zero;
        _position = clip.Segments[0].SourceStart; Player.Position = _position; Player.Pause();
        DurationText.Text = $" / {clip.Media.Duration:mm\\:ss}";
        UpdateUi();
        await EnsureClipVisualsAsync(clip, token);
        _waveformImage = _clipWaveforms.GetValueOrDefault(clip);
        UpdateTimelineWidths(); DrawCuts(); RefreshLayerStack();
    }

    private async Task PreviewProjectMediaAsync(ClipItem clip)
    {
        StopPreviewPlayback();
        _projectMediaPreviewActive = true; _projectMediaAudioPreviewActive = !clip.Media.HasVideo && !clip.Media.IsStillImage; _soloPreviewLayerId = null; _soloPreviewPlacementId = null; _compositionPreviewActive = false; _current = clip;
        _activeSegmentIndex = 0; _selectedSegmentId = clip.Segments.FirstOrDefault()?.Id; _sequencePosition = TimeSpan.Zero; _editPosition = TimeSpan.Zero; _position = clip.Segments.FirstOrDefault()?.SourceStart ?? TimeSpan.Zero;
        DropPanel.Visibility = Visibility.Collapsed; LivePlayerHost.Visibility = Visibility.Collapsed; _liveGraphicsHost.Visibility = Visibility.Collapsed;

        if (clip.Media.IsStillImage)
        {
            Player.Source = null; Player.Visibility = Visibility.Collapsed;
            _scrubFrameView.Stretch = Stretch.Uniform; _scrubFrameView.RenderTransform = Transform.Identity; _scrubFrameView.RenderTransformOrigin = new Point(.5, .5);
            _scrubFrameView.Source = LoadBitmap(clip.SourcePath); _scrubFrameView.Visibility = Visibility.Visible;
            _playing = false; PlayButton.Content = "▶"; UpdateProjectUi(); return;
        }

        if (clip.PreviewPath is null)
            await RunBusyAsync($"Preparing {clip.DisplayName} preview…", async token => clip.PreviewPath = await _engine.CreatePreviewProxyAsync(clip.Media, _cache, token));
        if (clip.PreviewPath is null) { _projectMediaPreviewActive = false; return; }
        if (_projectMediaAudioPreviewActive)
        {
            CancelScrubFrame(); Player.Stop(); Player.Source = null; Player.Visibility = Visibility.Visible;
            _playWhenMediaOpened = true; _audioPreviewPlayer.Volume = PreviewVolume.Value; _audioPreviewPlayer.Balance = 0; _audioPreviewPlayer.Open(new Uri(clip.PreviewPath)); UpdateProjectUi(); return;
        }
        CancelScrubFrame(); Player.Visibility = Visibility.Visible; Player.Stop(); Player.Source = null; Player.Volume = PreviewVolume.Value;
        _playWhenMediaOpened = true; Player.Source = new Uri(clip.PreviewPath); Player.Position = _position; UpdateProjectUi();
    }

    private void StopPreviewPlayback()
    {
        _livePreviewClock.Stop(); _playing = false; PlayButton.Content = "▶"; _playWhenMediaOpened = false;
        foreach (var player in _livePlayers.Values) player.Pause();
        foreach (var player in _liveAudioPlayers.Values) player.Pause();
        _audioPreviewPlayer.Close(); _projectMediaAudioPreviewActive = false; Player.Stop(); StopLivePreview(); CancelScrubFrame();
    }

    private void ReturnToTimelinePreview()
    {
        if (!_projectMediaPreviewActive && _soloPreviewLayerId is null && _soloPreviewPlacementId is null) return;
        StopPreviewPlayback(); _projectMediaPreviewActive = false; _soloPreviewLayerId = null; _soloPreviewPlacementId = null;
        Player.Visibility = Visibility.Visible; Crop_ValueChanged(this, null!);
    }

    private async Task PreviewLayerAsync(TimelineLayer layer)
    {
        if (!layer.IsVisible || layer.Placements.Count == 0) return;
        StopPreviewPlayback(); _projectMediaPreviewActive = false; _soloPreviewLayerId = layer.Id; _soloPreviewPlacementId = null; _activeLayerId = layer.Id;
        _sequencePosition = layer.Placements.Min(item => item.Start); _livePreviewStart = _sequencePosition; UpdateProjectUi(); await StartLivePreviewAsync(true);
    }

    private async Task PreviewPlacementAsync(TimelineLayer layer, TimelinePlacement placement)
    {
        if (!layer.IsVisible) return;
        StopPreviewPlayback(); _projectMediaPreviewActive = false; _soloPreviewLayerId = layer.Id; _soloPreviewPlacementId = placement.Id; _activeLayerId = layer.Id; _selectedPlacementId = placement.Id;
        _sequencePosition = placement.Start; _livePreviewStart = _sequencePosition; UpdateProjectUi(); RefreshLayerStack(); await StartLivePreviewAsync(true);
    }

    private ClipSnapshot? RecordAnd(Action action)
    {
        if (_current is null) return null; EnsureProjectHistory(); InvalidateCompositionPreview(); action(); var snapshot = _current.Snapshot(); _histories[_current].Push(snapshot); CommitProjectEdit(); UpdateUi(); RefreshLayerStack(); return snapshot;
    }

    private ProjectEditSnapshot CaptureProjectSnapshot(ProjectSession? project = null)
    {
        project ??= _project;
        var layers = project.Composition.Layers.Select(layer => new LayerState(layer.Id, layer.Name, layer.IsVisible, layer.IsMuted, layer.MuteLeftChannel, layer.MuteRightChannel, layer.Kind,
            layer.Placements.Select(item => new PlacementState(item.Id, item.Clip, item.Start, item.InPoint, item.Length, item.Keyframes.Select(ToState).ToArray(), item.Effects.Select(effect => effect.Clone()).ToArray())).ToArray(),
            layer.Graphics.Select(item => new GraphicState(item.Id, item.Kind, item.Text, item.ImagePath, item.RenderedImagePath,
                item.FontFamily, item.FontSize, item.Foreground, item.Background, item.FillColor1, item.FillColor2, item.UseSecondFillColor, item.GradientKind, item.GradientAngle, item.Opacity, item.PreserveAspectRatio,
                item.X, item.Y, item.Width, item.Height, item.Start, item.Duration, item.Keyframes.Select(ToState).ToArray())).ToArray(), layer.Volume)).ToArray();
        return new(project.Clips.Select(clip => new ClipState(clip, clip.Snapshot())).ToArray(), layers,
            project.Composition.WorkspaceDuration, project.Composition.RenderWorkspaceTailAsBlack,
            _activeLayerId, _selectedPlacementId, _selectedGraphicId, project.AutomaticPlacementId, _sequencePosition, _editPosition,
            project.Composition.OutputEffects.Select(effect => effect.Clone()).ToArray(), project.Composition.Equalizer.Clone());
    }

    private void EnsureProjectHistory()
    {
        if (_project.ProjectHistory.Count == 0) _project.ProjectHistory.Push(CaptureProjectSnapshot());
    }

    private void CommitProjectEdit()
    {
        _project.ProjectHistory.Push(CaptureProjectSnapshot());
    }

    private void RestoreProjectSnapshot(ProjectEditSnapshot state)
    {
        Pause(); StopLivePreview(); InvalidateCompositionPreview();
        foreach (var clip in state.Clips) clip.Clip.Restore(clip.Snapshot);
        _composition.Layers.Clear();
        foreach (var saved in state.Layers)
        {
            var layer = new TimelineLayer { Id = saved.Id, Name = saved.Name, IsVisible = saved.IsVisible, IsMuted = saved.IsMuted, MuteLeftChannel = saved.MuteLeftChannel, MuteRightChannel = saved.MuteRightChannel, Kind = saved.Kind, Volume = saved.Volume };
            foreach (var item in saved.Placements) { var placement = new TimelinePlacement { Id = item.Id, Clip = item.Clip, Start = item.Start, InPoint = item.InPoint, Length = item.Length }; foreach (var keyframe in item.Keyframes) placement.Keyframes.Add(FromState(keyframe)); foreach (var effect in item.Effects) placement.Effects.Add(effect.Clone()); layer.Placements.Add(placement); }
            foreach (var item in saved.Graphics) { var graphic = new GraphicsOverlay { Id = item.Id, Kind = item.Kind, Text = item.Text, ImagePath = item.ImagePath, RenderedImagePath = item.RenderedImagePath, FontFamily = item.FontFamily, FontSize = item.FontSize, Foreground = item.Foreground, Background = item.Background, FillColor1 = item.FillColor1, FillColor2 = item.FillColor2, UseSecondFillColor = item.UseSecondFillColor, GradientKind = item.GradientKind, GradientAngle = item.GradientAngle, Opacity = item.Opacity, PreserveAspectRatio = item.PreserveAspectRatio, X = item.X, Y = item.Y, Width = item.Width, Height = item.Height, Start = item.Start, Duration = item.Duration }; foreach (var keyframe in item.Keyframes) graphic.Keyframes.Add(FromState(keyframe)); layer.Graphics.Add(graphic); }
            _composition.Layers.Add(layer);
        }
        _composition.OutputEffects.Clear(); foreach (var effect in state.OutputEffects) _composition.OutputEffects.Add(effect.Clone());
        _composition.Equalizer.CopyFrom(state.Equalizer);
        _composition.WorkspaceDuration = state.WorkspaceDuration; _composition.RenderWorkspaceTailAsBlack = state.RenderWorkspaceTailAsBlack;
        _activeLayerId = state.ActiveLayerId; _selectedPlacementId = state.SelectedPlacementId; _selectedGraphicId = state.SelectedGraphicId;
        _project.AutomaticPlacementId = state.AutomaticPlacementId; _sequencePosition = ClampTime(state.SequencePosition, TimeSpan.Zero, _composition.DisplayDuration); _editPosition = ClampTime(state.EditPosition, TimeSpan.Zero, _composition.DisplayDuration);
        RefreshLayerStack(); DrawCuts(); UpdateProjectUi(); _ = ShowCurrentProjectFrameAsync();
    }

    private static KeyframeState ToState(AnimationKeyframe item) => new(item.Id, item.Property, item.Offset, item.Value, item.Interpolation);
    private static AnimationKeyframe FromState(KeyframeState item) => new() { Id = item.Id, Property = item.Property, Offset = item.Offset, Value = item.Value, Interpolation = item.Interpolation };

    private void UpdateUi()
    {
        UpdateProjectUi();
    }

    private void UpdateProjectUi()
    {
        if (_projectMediaPreviewActive && _current is not null)
        {
            var mediaDuration = _current.SelectedDuration; CurrentText.Text = $"PLAY {TimeText.Format(_sequencePosition)}"; DurationText.Text = $"  EDIT {TimeText.Format(_editPosition)} / {mediaDuration:mm\\:ss}";
            _timelineUpdating = true; Timeline.Minimum = 0; Timeline.Maximum = Math.Max(.001, mediaDuration.TotalSeconds); Timeline.Value = Math.Min(mediaDuration.TotalSeconds, _sequencePosition.TotalSeconds); _timelineUpdating = false; return;
        }
        var duration = _composition.OutputDuration > TimeSpan.Zero ? _composition.OutputDuration : _composition.DisplayDuration;
        if (duration <= TimeSpan.Zero) duration = _current?.SelectedDuration ?? TimeSpan.Zero;
        MarkerText.Text = $"PROJECT: {TimeText.Format(duration)}"; CurrentText.Text = $"PLAY {TimeText.Format(_sequencePosition)}"; DurationText.Text = $"  EDIT {TimeText.Format(_editPosition)} / {duration.ToString(@"mm\:ss")}";
        CompositionStatus.Text = $"  •  LAYERS: {_composition.Layers.Count}  •  WORK: {FormatWholeSeconds(_composition.DisplayDuration)}";
        _viewStart = TimeSpan.Zero; _timelineUpdating = true; Timeline.Minimum = 0; Timeline.Maximum = Math.Max(.001, duration.TotalSeconds); Timeline.Value = Math.Min(duration.TotalSeconds, _sequencePosition.TotalSeconds); _timelineUpdating = false; UpdateLayerPlayheads(); DrawCuts();
    }
    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateProjectMediaPreviewAudio();
        UpdateCompositionPreviewAudio();
        UpdatePreviewShaders();
        if (_livePreviewActive)
        {
            if (_playing)
            {
                var (start, end) = CurrentLivePreviewRange;
                _sequencePosition = _livePreviewStart + _livePreviewClock.Elapsed;
                if (_sequencePosition >= end)
                {
                    if (_loop) { _sequencePosition = start; _livePreviewStart = start; _livePreviewClock.Restart(); }
                    else { _sequencePosition = end; Pause(); }
                }
            }
            UpdateLivePlayers(_sequencePosition, _playing); UpdateProjectUi(); return;
        }
        if (_current is null || _current.Segments.Count == 0) return;
        if (_playing && _projectMediaPreviewActive && _projectMediaAudioPreviewActive)
        {
            _position = _audioPreviewPlayer.Position;
            _sequencePosition = TimelineOperations.SourceToSequence(_current.Segments, Math.Clamp(_activeSegmentIndex, 0, _current.Segments.Count - 1), _position);
            var segment = _current.Segments[Math.Clamp(_activeSegmentIndex, 0, _current.Segments.Count - 1)];
            if (_position >= segment.SourceEnd - TimeSpan.FromMilliseconds(8)) AdvanceSegmentOrStop();
            UpdateUi(); return;
        }
        if (_playing)
        {
            if (DateTime.UtcNow < _ignorePlayerPositionUntil) { UpdateUi(); return; }
            if (_compositionPreviewActive)
            {
                _position = Player.Position; _sequencePosition = _position;
                if (_sequencePosition >= _composition.OutputDuration - TimeSpan.FromMilliseconds(8)) { if (_loop) { Player.Position = TimeSpan.Zero; Player.Play(); } else Pause(); }
                UpdateUi(); return;
            }
            _position = Player.Position;
            var segment = _current.Segments[Math.Clamp(_activeSegmentIndex, 0, _current.Segments.Count - 1)];
            if (_position >= segment.SourceEnd - TimeSpan.FromMilliseconds(8)) AdvanceSegmentOrStop();
            else _sequencePosition = TimelineOperations.SourceToSequence(_current.Segments, _activeSegmentIndex, _position);
        }
        UpdateUi();
    }

    private async Task PlayProjectAsync()
    {
        if (_playing) { Pause(); return; }
        _sequencePosition = ClampTime(_editPosition, TimeSpan.Zero, VisibleDuration);
        if (_projectMediaPreviewActive)
        {
            if (_current is null || _current.Media.IsStillImage) return;
            if (_sequencePosition >= _current.SelectedDuration) SeekSequence(TimeSpan.Zero);
            CancelScrubFrame(); if (_projectMediaAudioPreviewActive) _audioPreviewPlayer.Play(); else Player.Play(); _playing = true; PlayButton.Content = "❚❚"; return;
        }
        CancelScrubFrame();
        if (!_livePreviewActive) await StartLivePreviewAsync(true);
        else { var (start, end) = CurrentLivePreviewRange; if (_sequencePosition >= end || _sequencePosition < start) _sequencePosition = start; _livePreviewStart = _sequencePosition; _livePreviewClock.Restart(); _playing = true; PlayButton.Content = "❚❚"; UpdateLivePlayers(_sequencePosition, true); }
    }
    private void Pause()
    {
        if (_livePreviewActive) { _livePreviewClock.Stop(); foreach (var player in _livePlayers.Values) player.Pause(); foreach (var player in _liveAudioPlayers.Values) player.Pause(); _livePlayingIds.Clear(); }
        else if (_projectMediaAudioPreviewActive) _audioPreviewPlayer.Pause();
        else Player.Pause();
        _playing = false; PlayButton.Content = "▶";
        if (!_projectMediaPreviewActive) _ = RenderCurrentScrubFrameAsync();
    }
    private void SeekSequence(TimeSpan sequencePosition)
    {
        if (_projectMediaPreviewActive && _current is not null && _current.Segments.Count > 0)
        {
            var mediaMapped = TimelineOperations.SequenceToSource(_current.Segments, sequencePosition); _activeSegmentIndex = mediaMapped.SegmentIndex; _sequencePosition = ClampTime(sequencePosition, TimeSpan.Zero, _current.SelectedDuration); _position = mediaMapped.SourceTime; _selectedSegmentId = _current.Segments[_activeSegmentIndex].Id; if (_projectMediaAudioPreviewActive) _audioPreviewPlayer.Position = _position; else Player.Position = _position; UpdateProjectUi(); return;
        }
        if (_compositionPreviewActive)
        {
            _sequencePosition = ClampTime(sequencePosition, TimeSpan.Zero, _composition.OutputDuration); _position = _sequencePosition; Player.Position = _position; UpdateProjectUi(); if (!_playing) _ = RenderCurrentScrubFrameAsync(); return;
        }
        if (_livePreviewActive)
        {
            _sequencePosition = ClampTime(sequencePosition, TimeSpan.Zero, VisibleDuration); _livePreviewStart = _sequencePosition; if (_playing) _livePreviewClock.Restart(); UpdateLivePlayers(_sequencePosition, _playing); UpdateProjectUi(); if (!_playing) _ = RenderCurrentScrubFrameAsync(); return;
        }
        if (HasCustomComposition || _current is null || _current.Segments.Count == 0)
        {
            _sequencePosition = ClampTime(sequencePosition, TimeSpan.Zero, VisibleDuration); UpdateProjectUi(); if (!_playing) _ = ShowCurrentProjectFrameAsync(); return;
        }
        var mapped = TimelineOperations.SequenceToSource(_current.Segments, sequencePosition); _activeSegmentIndex = mapped.SegmentIndex; _sequencePosition = ClampTime(sequencePosition, TimeSpan.Zero, _current.SelectedDuration); _position = mapped.SourceTime; _selectedSegmentId = _current.Segments[_activeSegmentIndex].Id; _ignorePlayerPositionUntil = DateTime.UtcNow.AddMilliseconds(100); Player.Position = _position; UpdateProjectUi(); if (!_playing) _ = RenderCurrentScrubFrameAsync();
    }

    private void SetEditPosition(TimeSpan position)
    {
        if (_playing) Pause();
        _editPosition = ClampTime(position, TimeSpan.Zero, VisibleDuration);
        if (_livePreviewActive) { UpdateLivePlayers(_editPosition, false); UpdateLiveGraphics(_editPosition); }
        UpdateProjectUi();
        _ = ShowCurrentProjectFrameAsync();
    }

    private async Task ShowCurrentProjectFrameAsync()
    {
        if (_playing) return;
        if (!_livePreviewActive) await StartLivePreviewAsync(false);
        UpdateLivePlayers(_editPosition, false);
        await RenderCurrentScrubFrameAsync();
    }

    private ActiveTimelinePlacement? ResolveVideoFrameAt(TimeSpan projectTime)
    {
        var active = TimelineOperations.ActivePlacementsAt(_composition, projectTime)
            .Where(item => LayerIncludedInPreview(item.Layer) && PlacementIncludedInPreview(item.Placement) && item.Layer.Kind == TimelineLayerKind.Video && item.Placement.Clip.Media.HasVideo)
            .ToArray();
        if (active.Length == 0) return null;
        var preferred = Array.FindIndex(active, item => item.Layer.Id == _activeLayerId);
        return active[preferred >= 0 ? preferred : 0];
    }

    private void CancelScrubFrame()
    {
        Interlocked.Increment(ref _scrubFrameRequest);
        _scrubFrameWork?.Cancel();
        _scrubFrameWork = null;
        if (_scrubFrameView is not null) _scrubFrameView.Visibility = Visibility.Collapsed;
    }

    private async Task RenderCurrentScrubFrameAsync()
    {
        if (_playing || _projectMediaPreviewActive) { if (_playing) CancelScrubFrame(); return; }
        var active = ResolveVideoFrameAt(_editPosition);
        if (active is null) { CancelScrubFrame(); return; }

        var request = Interlocked.Increment(ref _scrubFrameRequest);
        _scrubFrameWork?.Cancel();
        var work = new CancellationTokenSource();
        _scrubFrameWork = work;
        var output = System.IO.Path.Combine(_cache, $"scrub-{Guid.NewGuid():N}.png");
        try
        {
            await Task.Delay(60, work.Token);
            Directory.CreateDirectory(_cache);
            // The still preview is the only place effects show up before a render, so
            // apply the clip's stack and the timeline's output stack to it.
            await _engine.CaptureFrameAsync(active.Value.Placement.Clip.SourcePath, active.Value.SourcePosition.SourceTime, output, work.Token, PreviewEffectsFor(active.Value.Placement));
            if (work.IsCancellationRequested || request != Interlocked.Read(ref _scrubFrameRequest) || _playing) return;
            _scrubFrameView.Source = LoadBitmap(output);
            _scrubFrameView.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Paused frame render failed: {ex.Message}");
            if (request == Interlocked.Read(ref _scrubFrameRequest)) _scrubFrameView.Visibility = Visibility.Collapsed;
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            if (ReferenceEquals(_scrubFrameWork, work)) _scrubFrameWork = null;
            work.Dispose();
        }
    }

    private async Task StartLivePreviewAsync(bool playImmediately)
    {
        if (_startingLivePreview) return; _startingLivePreview = true;
        try { await StartLivePreviewCoreAsync(playImmediately); }
        finally { _startingLivePreview = false; }
    }

    private bool LayerIncludedInPreview(TimelineLayer layer) => layer.IsVisible && (_soloPreviewLayerId is null || layer.Id == _soloPreviewLayerId);
    private bool PlacementIncludedInPreview(TimelinePlacement placement) => _soloPreviewPlacementId is null || placement.Id == _soloPreviewPlacementId;

    private (TimeSpan Start, TimeSpan End) CurrentLivePreviewRange
        => TimelineOperations.PlaybackRange(_composition, _soloPreviewLayerId, _soloPreviewPlacementId);

    private async Task StartLivePreviewCoreAsync(bool playImmediately)
    {
        if (!_composition.HasVisibleMedia && !_composition.HasVisibleGraphics) { MessageBox.Show(this, "Add media to a visible layer first.", "Post"); return; }
        var clips = _composition.Layers.Where(LayerIncludedInPreview).SelectMany(layer => layer.Placements.Where(PlacementIncludedInPreview)).Select(item => item.Clip).Distinct().ToArray();
        var missing = clips.Where(clip => clip.PreviewPath is null).ToArray();
        if (missing.Length > 0)
        {
            await RunBusyAsync("Preparing source playback…", async token =>
            {
                for (var i = 0; i < missing.Length; i++)
                {
                    BusyProgress.Value = i / (double)missing.Length; BusyText.Text = $"Preparing playback {i + 1}/{missing.Length}: {missing[i].DisplayName}";
                    missing[i].PreviewPath = await _engine.CreatePreviewProxyAsync(missing[i].Media, _cache, token);
                }
                BusyProgress.Value = 1;
            });
            if (missing.Any(clip => clip.PreviewPath is null)) return;
        }

        _compositionPreviewActive = false; Player.Stop(); Player.Visibility = Visibility.Collapsed; DropPanel.Visibility = Visibility.Collapsed; LivePlayerHost.Visibility = Visibility.Visible; _liveGraphicsHost.Visibility = Visibility.Visible;
        var preload = new List<Task>();
        foreach (var layer in _composition.Layers.Where(layer => LayerIncludedInPreview(layer) && layer.Kind != TimelineLayerKind.Graphics))
            foreach (var placement in layer.Placements.Where(PlacementIncludedInPreview))
                if (LivePreviewPathFor(placement.Clip) is { } path) { if (UsesAudioOnlyPlayer(layer, placement)) EnsureLiveAudioPlayer(layer, placement, path); else EnsureLivePlayer(layer, placement, path); preload.Add(_liveReady[placement.Id].Task); }
        if (preload.Count > 0) await Task.WhenAny(Task.WhenAll(preload), Task.Delay(TimeSpan.FromSeconds(5)));
        _livePreviewActive = true; EnsurePreviewAudio(); QueueEqualizedPreview(); var (rangeStart, rangeEnd) = CurrentLivePreviewRange; if (_sequencePosition >= rangeEnd || _sequencePosition < rangeStart) _sequencePosition = rangeStart;
        _livePreviewStart = _sequencePosition;
        if (playImmediately) { CancelScrubFrame(); _livePreviewClock.Restart(); _playing = true; PlayButton.Content = "❚❚"; UpdateLivePlayers(_sequencePosition, true); }
        else { _livePreviewClock.Reset(); _playing = false; PlayButton.Content = "▶"; UpdateLivePlayers(_sequencePosition, false); }
    }

    private void StopLivePreview()
    {
        _livePreviewClock.Reset(); _livePreviewActive = false;
        ClearLivePreviewAudio();
        foreach (var player in _livePlayers.Values) { player.Stop(); player.Source = null; }
        foreach (var player in _liveAudioPlayers.Values) player.Close();
        _livePlayers.Clear(); _liveDesiredPositions.Clear(); _livePlayingIds.Clear(); _liveLastCorrections.Clear();
        _liveAudioPlayers.Clear();
        _liveOpenedPlayers.Clear();
        _liveReady.Clear();
        if (LivePlayerHost is not null) { LivePlayerHost.Children.Clear(); LivePlayerHost.Visibility = Visibility.Collapsed; }
        _liveGraphicsHost.Children.Clear(); _liveGraphicsHost.Visibility = Visibility.Collapsed;
        if (Player is not null) Player.Visibility = Visibility.Visible;
    }

    private void UpdateLivePlayers(TimeSpan projectTime, bool play)
    {
        if (!_livePreviewActive) return;
            var activeIds = new HashSet<Guid>(); var z = _composition.Layers.Count;
        foreach (var active in TimelineOperations.ActivePlacementsAt(_composition, projectTime).Where(item => LayerIncludedInPreview(item.Layer) && PlacementIncludedInPreview(item.Placement)))
        {
                var layer = active.Layer; var placement = active.Placement;
                if (LivePreviewPathFor(placement.Clip) is not { } path) continue;
                activeIds.Add(placement.Id);
                var mapped = active.SourcePosition;
                _liveDesiredPositions[placement.Id] = mapped.SourceTime;
                if (UsesAudioOnlyPlayer(layer, placement))
                {
                    var audioPlayer = EnsureLiveAudioPlayer(layer, placement, path);
                    var offset = projectTime - placement.Start; var audioOnlyVolume = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.Volume, offset, 1);
                    audioPlayer.IsMuted = _muted || LayerAudioFullyMuted(layer) || PreviewAudioEngaged; audioPlayer.Balance = LayerAudioBalance(layer); audioPlayer.Volume = Math.Clamp(PreviewVolume.Value * audioOnlyVolume * layer.Volume, 0, 1);
                    UpdatePreviewAudioSource(layer, placement, mapped.SourceTime, audioOnlyVolume, play);
                    var drift = (audioPlayer.Position - mapped.SourceTime).Duration();
                    if (!play && drift > TimeSpan.FromMilliseconds(30)) audioPlayer.Position = mapped.SourceTime;
                    else if (play && drift > TimeSpan.FromSeconds(2) && (!_liveLastCorrections.TryGetValue(placement.Id, out var audioCorrected) || DateTime.UtcNow - audioCorrected > TimeSpan.FromSeconds(5))) { audioPlayer.Position = mapped.SourceTime; _liveLastCorrections[placement.Id] = DateTime.UtcNow; }
                    if (play) { if (_livePlayingIds.Add(placement.Id)) { audioPlayer.Position = mapped.SourceTime; _liveLastCorrections[placement.Id] = DateTime.UtcNow; audioPlayer.Play(); } }
                    else { _livePlayingIds.Remove(placement.Id); audioPlayer.Pause(); }
                    continue;
                }
                var player = EnsureLivePlayer(layer, placement, path);
                var localOffset = projectTime - placement.Start; var opacity = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.Opacity, localOffset, 1); var scale = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.Scale, localOffset, 1);
                var x = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.PositionX, localOffset, .5); var y = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.PositionY, localOffset, .5); var animatedVolume = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.Volume, localOffset, 1);
                player.Visibility = Visibility.Visible; player.Opacity = layer.Kind == TimelineLayerKind.Audio ? 0 : (_liveOpenedPlayers.Contains(placement.Id) ? Math.Clamp(opacity, 0, 1) : 0); player.IsMuted = _muted || LayerAudioFullyMuted(layer) || PreviewAudioEngaged; player.Balance = LayerAudioBalance(layer); player.Volume = Math.Clamp(PreviewVolume.Value * animatedVolume * layer.Volume, 0, 1); Panel.SetZIndex(player, layer.Kind == TimelineLayerKind.Audio ? -1 : z--);
                UpdatePreviewAudioSource(layer, placement, mapped.SourceTime, animatedVolume, play);
                ApplyPreviewShader(player, placement.Id, [.. placement.Effects, .. _composition.OutputEffects]);
                player.Stretch = Player.Stretch; player.RenderTransformOrigin = Player.RenderTransformOrigin;
                var transforms = new TransformGroup(); transforms.Children.Add(new ScaleTransform(CropZoom.Value * scale, CropZoom.Value * scale)); transforms.Children.Add(new TranslateTransform((x - .5) * Math.Max(1, PreviewSurface.ActualWidth), (y - .5) * Math.Max(1, PreviewSurface.ActualHeight))); player.RenderTransform = transforms;
                var uri = new Uri(path);
                if (player.Source is null || !player.Source.Equals(uri)) { player.Stop(); _livePlayingIds.Remove(placement.Id); player.Source = uri; player.Position = mapped.SourceTime; }
                else
                {
                    var drift = (player.Position - mapped.SourceTime).Duration();
                    if (!play && drift > TimeSpan.FromMilliseconds(30)) player.Position = mapped.SourceTime;
                    else if (play && drift > TimeSpan.FromSeconds(2) && (!_liveLastCorrections.TryGetValue(placement.Id, out var corrected) || DateTime.UtcNow - corrected > TimeSpan.FromSeconds(5))) { player.Position = mapped.SourceTime; _liveLastCorrections[placement.Id] = DateTime.UtcNow; }
                }
                if (play)
                {
                    if (_livePlayingIds.Add(placement.Id))
                    {
                        player.Position = mapped.SourceTime;
                        _liveLastCorrections[placement.Id] = DateTime.UtcNow;
                        player.Play();
                    }
                }
                else { _livePlayingIds.Remove(placement.Id); player.Pause(); }
        }
        foreach (var item in _livePlayers.Where(item => !activeIds.Contains(item.Key))) { _livePlayingIds.Remove(item.Key); item.Value.Pause(); item.Value.Visibility = Visibility.Collapsed; }
        foreach (var item in _liveAudioPlayers.Where(item => !activeIds.Contains(item.Key))) { _livePlayingIds.Remove(item.Key); item.Value.Pause(); }
        FinishPreviewAudioFrame(activeIds, play);
        UpdateLiveGraphics(projectTime);
    }

    /// <summary>
    /// The layer's own level in the mix, sitting on top of whatever volume keyframes its
    /// clips carry. Dragging is live: the preview follows without a rebuild, and only
    /// letting go writes an undo step, so a drag is one edit rather than dozens.
    /// </summary>
    private FrameworkElement BuildLayerVolume(TimelineLayer layer)
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = 2, Value = Math.Clamp(layer.Volume, 0, 2), Width = 58, Height = 20,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
            SmallChange = .05, LargeChange = .1, IsSnapToTickEnabled = false,
            ToolTip = $"Layer volume: {layer.Volume * 100:0}%",
        };
        slider.ValueChanged += (_, e) =>
        {
            layer.Volume = e.NewValue;
            slider.ToolTip = $"Layer volume: {e.NewValue * 100:0}%";
            ApplyLiveLayerVolume(layer);
        };
        slider.PreviewMouseLeftButtonDown += (_, _) => EnsureProjectHistory();
        slider.LostMouseCapture += (_, _) => CommitProjectEdit();
        slider.KeyUp += (_, _) => { EnsureProjectHistory(); CommitProjectEdit(); };
        return slider;
    }

    /// <summary>Pushes a layer's level onto whichever players are already playing it.</summary>
    private void ApplyLiveLayerVolume(TimelineLayer layer)
    {
        foreach (var placement in layer.Placements)
        {
            if (_livePlayers.TryGetValue(placement.Id, out var player))
                player.Volume = Math.Clamp(PreviewVolume.Value * layer.Volume, 0, 1);
            if (_liveAudioPlayers.TryGetValue(placement.Id, out var audioPlayer))
                audioPlayer.Volume = Math.Clamp(PreviewVolume.Value * layer.Volume, 0, 1);
        }
    }

    private void SetLayerMuted(TimelineLayer layer, bool muted)
    {
        if (layer.IsMuted == muted) return; EnsureProjectHistory(); layer.IsMuted = muted;
        foreach (var placement in layer.Placements)
        {
            if (_livePlayers.TryGetValue(placement.Id, out var player)) player.IsMuted = _muted || muted;
            if (_liveAudioPlayers.TryGetValue(placement.Id, out var audioPlayer)) audioPlayer.IsMuted = _muted || muted;
        }
        CommitProjectEdit(); RefreshLayerStack();
    }

    private static bool LayerAudioFullyMuted(TimelineLayer layer) => layer.IsMuted || (layer.Kind == TimelineLayerKind.Audio && layer.MuteLeftChannel && layer.MuteRightChannel);
    private static double LayerAudioBalance(TimelineLayer layer) => layer.Kind != TimelineLayerKind.Audio ? 0 : layer.MuteLeftChannel && !layer.MuteRightChannel ? 1 : layer.MuteRightChannel && !layer.MuteLeftChannel ? -1 : 0;

    private void SetAudioChannelMute(TimelineLayer layer, bool? left = null, bool? right = null, bool? both = null)
    {
        var newLeft = both ?? left ?? layer.MuteLeftChannel;
        var newRight = both ?? right ?? layer.MuteRightChannel;
        if (newLeft == layer.MuteLeftChannel && newRight == layer.MuteRightChannel) return;
        EnsureProjectHistory(); layer.IsMuted = false; layer.MuteLeftChannel = newLeft; layer.MuteRightChannel = newRight;
        foreach (var placement in layer.Placements)
        {
            if (_livePlayers.TryGetValue(placement.Id, out var player)) { player.IsMuted = _muted || LayerAudioFullyMuted(layer); player.Balance = LayerAudioBalance(layer); }
            if (_liveAudioPlayers.TryGetValue(placement.Id, out var audioPlayer)) { audioPlayer.IsMuted = _muted || LayerAudioFullyMuted(layer); audioPlayer.Balance = LayerAudioBalance(layer); }
        }
        CommitProjectEdit(); RefreshLayerStack();
    }

    private async Task SetLayerVisibilityAsync(TimelineLayer layer, bool visible)
    {
        if (layer.IsVisible == visible) return;
        EnsureProjectHistory(); layer.IsVisible = visible;
        if (_livePreviewActive)
        {
            if (visible)
            {
                foreach (var placement in layer.Placements)
                {
                    placement.Clip.PreviewPath ??= await _engine.CreatePreviewProxyAsync(placement.Clip.Media, _cache);
                    if (UsesAudioOnlyPlayer(layer, placement)) EnsureLiveAudioPlayer(layer, placement, placement.Clip.PreviewPath); else EnsureLivePlayer(layer, placement, placement.Clip.PreviewPath);
                }
            }
            UpdateLivePlayers(_sequencePosition, _playing);
        }
        else if (!_playing) await ShowCurrentProjectFrameAsync();
        CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private MediaElement EnsureLivePlayer(TimelineLayer layer, TimelinePlacement placement, string path)
    {
        if (_livePlayers.TryGetValue(placement.Id, out var existing)) return existing;
        var capturedId = placement.Id; var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _liveReady[capturedId] = ready;
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Player.Stretch, RenderTransformOrigin = Player.RenderTransformOrigin, Opacity = 0, IsHitTestVisible = false, ScrubbingEnabled = true };
        player.MediaOpened += async (_, _) =>
        {
            _liveOpenedPlayers.Add(capturedId); player.Opacity = 0; player.Pause();
            if (_liveDesiredPositions.TryGetValue(capturedId, out var desired)) player.Position = desired;
            await Dispatcher.Yield(DispatcherPriority.Render);
            if (_liveDesiredPositions.TryGetValue(capturedId, out desired)) player.Position = desired;
            _liveLastCorrections[capturedId] = DateTime.UtcNow;
            if (_livePlayingIds.Contains(capturedId)) player.Play(); else player.Pause();
            ready.TrySetResult(true);
        };
        player.MediaFailed += (_, _) => { _liveOpenedPlayers.Remove(capturedId); player.Opacity = 0; player.Visibility = Visibility.Collapsed; ready.TrySetResult(false); };
        _livePlayers[capturedId] = player; LivePlayerHost.Children.Add(player); player.Source = new Uri(path); player.Pause(); return player;
    }

    private static bool UsesAudioOnlyPlayer(TimelineLayer layer, TimelinePlacement placement) => layer.Kind == TimelineLayerKind.Audio || !placement.Clip.Media.HasVideo;

    private MediaPlayer EnsureLiveAudioPlayer(TimelineLayer layer, TimelinePlacement placement, string path)
    {
        if (_liveAudioPlayers.TryGetValue(placement.Id, out var existing)) return existing;
        var capturedId = placement.Id; var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _liveReady[capturedId] = ready;
        var player = new MediaPlayer { Volume = PreviewVolume.Value, Balance = LayerAudioBalance(layer), IsMuted = _muted || LayerAudioFullyMuted(layer) };
        player.MediaOpened += (_, _) => { _liveOpenedPlayers.Add(capturedId); if (_liveDesiredPositions.TryGetValue(capturedId, out var desired)) player.Position = desired; _liveLastCorrections[capturedId] = DateTime.UtcNow; if (_livePlayingIds.Contains(capturedId)) player.Play(); else player.Pause(); ready.TrySetResult(true); };
        player.MediaFailed += (_, _) => { _liveOpenedPlayers.Remove(capturedId); ready.TrySetResult(false); };
        _liveAudioPlayers[capturedId] = player; player.Open(new Uri(path)); return player;
    }

    private void UpdateLiveGraphics(TimeSpan projectTime)
    {
        if (!_livePreviewActive || _liveGraphicsHost is null) return;
        _liveGraphicsHost.Children.Clear();
        var width = Math.Max(1, _liveGraphicsHost.ActualWidth > 0 ? _liveGraphicsHost.ActualWidth : PreviewSurface.ActualWidth);
        var height = Math.Max(1, _liveGraphicsHost.ActualHeight > 0 ? _liveGraphicsHost.ActualHeight : PreviewSurface.ActualHeight);
        var z = 100;
        foreach (var layer in _composition.Layers.Where(LayerIncludedInPreview).Reverse())
        {
            if (_soloPreviewPlacementId is not null) continue;
            foreach (var graphic in layer.Graphics.Where(item => projectTime >= item.Start && projectTime < item.End))
            {
                var offset = projectTime - graphic.Start;
                FrameworkElement element;
                if (graphic.Kind == GraphicsOverlayKind.Lottie)
                {
                    if (LottieFrameFor(graphic, offset, width, height) is not { } lottieFrame) continue;
                    element = new Image { Source = lottieFrame, Stretch = graphic.PreserveAspectRatio ? Stretch.Uniform : Stretch.Fill };
                }
                else if (graphic.Kind == GraphicsOverlayKind.Image && graphic.ImagePath is { } path && File.Exists(path))
                {
                    try { element = new Image { Source = new BitmapImage(new Uri(path)), Stretch = graphic.PreserveAspectRatio ? Stretch.Uniform : Stretch.Fill }; } catch { continue; }
                }
                else if (graphic.Kind is GraphicsOverlayKind.SolidColor or GraphicsOverlayKind.Gradient)
                    element = new Border { Background = GraphicFillBrush(graphic) };
                else
                {
                    element = new Border { Background = GraphicBrush(graphic.Background, Brushes.Transparent), Child = new TextBlock { Text = graphic.Text, FontFamily = new FontFamily(graphic.FontFamily), FontSize = graphic.FontSize * height / 1080, Foreground = GraphicBrush(graphic.Foreground, Brushes.White), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                }
                var scale = KeyframeEvaluator.Evaluate(graphic.Keyframes, KeyframeProperty.Scale, offset, 1);
                var x = KeyframeEvaluator.Evaluate(graphic.Keyframes, KeyframeProperty.PositionX, offset, graphic.X); var y = KeyframeEvaluator.Evaluate(graphic.Keyframes, KeyframeProperty.PositionY, offset, graphic.Y);
                element.Width = Math.Max(2, graphic.Width * width * scale); element.Height = Math.Max(2, graphic.Height * height * scale); element.Opacity = Math.Clamp(KeyframeEvaluator.Evaluate(graphic.Keyframes, KeyframeProperty.Opacity, offset, graphic.Opacity), 0, 1);
                Canvas.SetLeft(element, x * width); Canvas.SetTop(element, y * height); Panel.SetZIndex(element, z++); _liveGraphicsHost.Children.Add(element);
            }
        }
    }

    private static Brush GraphicBrush(string value, Brush fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!); } catch { return fallback; }
    }
    private static Brush GraphicFillBrush(GraphicsOverlay graphic)
    {
        var first = GraphicColor(graphic.FillColor1, Colors.White);
        if (graphic.Kind == GraphicsOverlayKind.SolidColor || !graphic.UseSecondFillColor) return new SolidColorBrush(first);
        var second = GraphicColor(graphic.FillColor2, Colors.Black); var radians = graphic.GradientAngle * Math.PI / 180; var dx = Math.Cos(radians); var dy = Math.Sin(radians);
        if (graphic.GradientKind == GraphicGradientKind.Radial)
            return new RadialGradientBrush(first, second) { Center = new Point(.5, .5), GradientOrigin = new Point(Math.Clamp(.5 - dx * .15, 0, 1), Math.Clamp(.5 - dy * .15, 0, 1)), RadiusX = .7, RadiusY = .7 };
        return new LinearGradientBrush(first, second, new Point(.5 - dx / 2, .5 - dy / 2), new Point(.5 + dx / 2, .5 + dy / 2));
    }
    private static Color GraphicColor(string value, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(value)!; } catch { return fallback; } }
    private TimeSpan FrameDuration => TimeSpan.FromSeconds(1 / Math.Max(1, _current?.Media.FrameRate ?? 30));

    private void SetIn() { if (_current is null || _current.Segments.Count == 0 || _sequencePosition >= _current.SelectedDuration) return; var old = _sequencePosition; RecordAnd(() => TimelineOperations.TrimSequenceStart(_current, old)); SeekSequence(TimeSpan.Zero); }
    private void SetOut() { if (_current is null || _current.Segments.Count == 0 || _sequencePosition <= TimeSpan.Zero) return; var end = _sequencePosition; RecordAnd(() => TimelineOperations.TrimSequenceEnd(_current, end)); SeekSequence(_current.SelectedDuration); }
    private void CutFrom() { if (_current is not null) RecordAnd(() => { _current.PendingCutStart = _sequencePosition; _current.PendingCutEnd = null; }); }
    private void CutTo() { if (_current is not null && _current.PendingCutStart is { } start && _sequencePosition > start) RecordAnd(() => _current.PendingCutEnd = _sequencePosition); }
    private void CutSelection()
    {
        if (_current is null || _current.PendingCutStart is not { } start || _current.PendingCutEnd is not { } end || end <= start) return;
        Pause(); RecordAnd(() => { TimelineOperations.SplitSelection(_current, start, end); _current.PendingCutStart = null; _current.PendingCutEnd = null; });
        SeekSequence(start);
    }

    private void SplitSelectedPlacement()
    {
        if (_selectedPlacementId is not { } id) { MessageBox.Show(this, "Select a clip on a layer first.", "Split clip", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        EnsureProjectHistory();
        var right = TimelineOperations.SplitPlacement(_composition, id, _editPosition);
        if (right is null) { MessageBox.Show(this, "Move the playhead inside the selected clip, then split again.", "Split clip", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _selectedPlacementId = right.Id; _activeLayerId = _composition.Layers.First(layer => layer.Placements.Contains(right)).Id; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private void RemoveSelectedPlacement()
    {
        if (_selectedPlacementId is not { } id) return;
        EnsureProjectHistory(); TimelineOperations.RemovePlacement(_composition, id); _selectedPlacementId = null; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private void DrawCuts()
    {
        CutOverlay.Children.Clear(); WaveformCanvas.Children.Clear();
    }

    private void UpdateTimelineWidths()
    {
        if (TimelineArea is null || SourceTimelineScroll is null) return;
        var viewport = Math.Max(600, SourceTimelineScroll.ViewportWidth > 0 ? SourceTimelineScroll.ViewportWidth : SourceTimelineScroll.ActualWidth);
        TimelineArea.Width = viewport * Math.Max(1, _timelineZoom);
        RefreshLayerStack(); DrawCuts();
    }

    private void RefreshLayerStack()
    {
        if (LayerStack is null || CompositionScroll is null) return;
        if (_composition.Layers.Count == 0)
        {
            LayerStack.Children.Clear(); _layerPlayheads.Clear(); _layerEditCarets.Clear();
            _layerTimeRulerLane = null; _layerTimeRulerPlayhead = null; _layerTimeRulerEditCaret = null; LayerStack.Width = double.NaN;
            var empty = new Border { MinHeight = 90, AllowDrop = true, Background = new SolidColorBrush(Color.FromArgb(24, 70, 150, 210)), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(6), Child = new TextBlock { Text = "No layers — drag project media here to create one", Foreground = (Brush)FindResource("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            empty.DragOver += LayersArea_DragOver;
            empty.Drop += LayersArea_Drop;
            LayerStack.Children.Add(empty);
            if (CompositionStatus is not null) CompositionStatus.Text = "  •  LAYERS: 0  •  WORK: 00:00"; return;
        }
        var display = _composition.DisplayDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _composition.DisplayDuration;
        const double rowHeight = 74; const double splitterWidth = 5;
        var viewport = Math.Max(600, (CompositionScroll.ViewportWidth > 0 ? CompositionScroll.ViewportWidth : CompositionScroll.ActualWidth) - _layerHeaderWidth - splitterWidth);
        var laneWidth = viewport * Math.Max(1, _timelineZoom);
        LayerStack.Children.Clear(); _layerPlayheads.Clear(); _layerEditCarets.Clear(); LayerStack.Width = _layerHeaderWidth + splitterWidth + laneWidth; LayerStack.HorizontalAlignment = HorizontalAlignment.Left;
        LayerStack.Children.Add(CreateLayerTimeRuler(display, laneWidth, splitterWidth));
        for (var layerIndex = 0; layerIndex < _composition.Layers.Count; layerIndex++)
        {
            var layer = _composition.Layers[layerIndex];
            var currentLayerIndex = layerIndex;
            var row = new Grid { Height = rowHeight, Width = _layerHeaderWidth + splitterWidth + laneWidth, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(layerIndex % 2 == 0 ? Color.FromRgb(9, 25, 46) : Color.FromRgb(12, 30, 52)) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_layerHeaderWidth), MinWidth = 130, MaxWidth = 420 }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(splitterWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(laneWidth) });
            var header = new Border { BorderBrush = layer.Id == _activeLayerId ? (Brush)FindResource("CyanBrush") : (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2, 4, 2) };
            header.ContextMenu = CreateLayerMenu(layer);
            var controls = new Grid(); controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) }); controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) }); controls.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            var name = new TextBox { Text = layer.Name, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(0), FontSize = 11, FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromArgb(45, 70, 120, 160)), BorderThickness = new Thickness(0), Foreground = Brushes.White, ToolTip = "Click to select and rename this layer" };
            name.GotKeyboardFocus += (_, _) => { _activeLayerId = layer.Id; name.SelectAll(); };
            name.LostKeyboardFocus += (_, _) => { var value = name.Text.Trim(); if (!string.IsNullOrWhiteSpace(value) && value != layer.Name) { EnsureProjectHistory(); layer.Name = value; CommitProjectEdit(); } else name.Text = layer.Name; };
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { var value = name.Text.Trim(); if (!string.IsNullOrWhiteSpace(value)) layer.Name = value; Keyboard.ClearFocus(); e.Handled = true; } };
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 1, 0, 0) };
            var visible = new CheckBox { Content = "Visible", IsChecked = layer.IsVisible, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 10, 0), ToolTip = "Show/hide this layer" }; visible.Checked += async (_, _) => await SetLayerVisibilityAsync(layer, true); visible.Unchecked += async (_, _) => await SetLayerVisibilityAsync(layer, false);
            toggles.Children.Add(visible);
            if (layer.Kind == TimelineLayerKind.Audio)
            {
                toggles.Children.Add(new TextBlock { Text = "Mute:", Foreground = (Brush)FindResource("MutedBrush"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
                var muteLeft = new CheckBox { Content = "L", IsChecked = layer.MuteLeftChannel, Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Mute the left channel" };
                var muteRight = new CheckBox { Content = "R", IsChecked = layer.MuteRightChannel, Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Mute the right channel" };
                var muteBoth = new CheckBox { Content = "L&R", IsChecked = layer.MuteLeftChannel && layer.MuteRightChannel, Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Mute both channels" };
                muteLeft.Checked += (_, _) => SetAudioChannelMute(layer, left: true); muteLeft.Unchecked += (_, _) => SetAudioChannelMute(layer, left: false);
                muteRight.Checked += (_, _) => SetAudioChannelMute(layer, right: true); muteRight.Unchecked += (_, _) => SetAudioChannelMute(layer, right: false);
                muteBoth.Checked += (_, _) => SetAudioChannelMute(layer, both: true); muteBoth.Unchecked += (_, _) => { if (layer.MuteLeftChannel && layer.MuteRightChannel) SetAudioChannelMute(layer, both: false); };
                toggles.Children.Add(muteLeft); toggles.Children.Add(muteRight); toggles.Children.Add(muteBoth);
            }
            else if (layer.Kind != TimelineLayerKind.Graphics)
            {
                var mute = new CheckBox { Content = "Mute", IsChecked = layer.IsMuted, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 8, 0), ToolTip = "Mute this layer" }; mute.Checked += (_, _) => SetLayerMuted(layer, true); mute.Unchecked += (_, _) => SetLayerMuted(layer, false); toggles.Children.Add(mute);
            }
            Grid.SetRow(toggles, 1);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
            var playLayer = new Button { Content = "▶", Padding = new Thickness(6, 0, 6, 0), Height = 20, Margin = new Thickness(1, 0, 1, 0), ToolTip = layer.Kind == TimelineLayerKind.Audio ? "Preview only this audio layer" : "Preview only this video layer", IsEnabled = layer.Kind != TimelineLayerKind.Graphics && layer.IsVisible && layer.Placements.Count > 0 }; playLayer.Click += async (_, _) => await PreviewLayerAsync(layer);
            var up = new Button { Content = "↑", Padding = new Thickness(6, 0, 6, 0), Height = 20, Margin = new Thickness(1, 0, 1, 0), ToolTip = "Move layer up", IsEnabled = currentLayerIndex > 0 }; up.Click += (_, _) => { EnsureProjectHistory(); if (TimelineOperations.MoveLayer(_composition, layer.Id, currentLayerIndex - 1)) CommitProjectEdit(); InvalidateCompositionPreview(); RefreshLayerStack(); DrawCuts(); };
            var down = new Button { Content = "↓", Padding = new Thickness(6, 0, 6, 0), Height = 20, Margin = new Thickness(1, 0, 1, 0), ToolTip = "Move layer down", IsEnabled = currentLayerIndex < _composition.Layers.Count - 1 }; down.Click += (_, _) => { EnsureProjectHistory(); if (TimelineOperations.MoveLayer(_composition, layer.Id, currentLayerIndex + 1)) CommitProjectEdit(); InvalidateCompositionPreview(); RefreshLayerStack(); DrawCuts(); };
            var removeLayer = new Button { Content = "×", Padding = new Thickness(7, 0, 7, 0), Height = 20, Margin = new Thickness(1, 0, 1, 0), ToolTip = "Delete layer" }; removeLayer.Click += (_, _) => RemoveLayer(layer);
            if (layer.Kind != TimelineLayerKind.Graphics) buttons.Children.Add(playLayer); buttons.Children.Add(up); buttons.Children.Add(down);
            if (layer.Kind != TimelineLayerKind.Graphics) buttons.Children.Add(BuildLayerVolume(layer));
            buttons.Children.Add(removeLayer); Grid.SetRow(buttons, 2);
            controls.Children.Add(name); controls.Children.Add(toggles); controls.Children.Add(buttons); header.Child = controls; row.Children.Add(header);

            var headerResize = new Thumb { Width = splitterWidth, Cursor = Cursors.SizeWE, Background = new SolidColorBrush(Color.FromRgb(47, 89, 114)), ToolTip = "Drag to resize layer headers" };
            headerResize.DragDelta += (_, e) => ResizeLayerHeaders(e.HorizontalChange, laneWidth, splitterWidth); Grid.SetColumn(headerResize, 1); row.Children.Add(headerResize);
            headerResize.DragCompleted += (_, _) => RefreshLayerStack();

            var lane = new Canvas { Width = laneWidth, Height = rowHeight, AllowDrop = true, Background = new SolidColorBrush(Color.FromArgb(30, 70, 150, 210)), Tag = layer };
            lane.DragOver += Layer_DragOver; lane.Drop += Layer_Drop;
            lane.MouseLeftButtonDown += (_, e) =>
            {
                if (!ReferenceEquals(e.Source, lane)) return;
                ReturnToTimelinePreview();
                _activeLayerId = layer.Id; _selectedPlacementId = null; _selectedGraphicId = null;
                SetEditPosition(TimeSpan.FromSeconds(Math.Clamp(e.GetPosition(lane).X / Math.Max(1, lane.ActualWidth), 0, 1) * display.TotalSeconds));
                RefreshLayerStack(); e.Handled = true;
            };
            foreach (var graphic in layer.Graphics)
            {
                var left = graphic.Start.TotalSeconds / display.TotalSeconds * laneWidth;
                var width = Math.Max(36, graphic.Duration.TotalSeconds / display.TotalSeconds * laneWidth);
                var selected = graphic.Id == _selectedGraphicId;
                var kindName = GraphicKindName(graphic.Kind);
                var block = new Border { Width = width, Height = 52, CornerRadius = new CornerRadius(5), BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(203, 116, 255)), BorderThickness = selected ? new Thickness(3) : new Thickness(1.5), Background = new SolidColorBrush(Color.FromRgb(68, 33, 91)), ClipToBounds = true, ToolTip = $"{kindName} overlay\nStart {TimeText.Format(graphic.Start)} • Duration {TimeText.Format(graphic.Duration)}\nDrag to move • drag either edge to change duration" };
                var content = new Grid();
                var graphicLabel = graphic.Kind switch { GraphicsOverlayKind.Text => $"T  {graphic.Text}", GraphicsOverlayKind.Image => $"▧  {System.IO.Path.GetFileName(graphic.ImagePath)}", GraphicsOverlayKind.SolidColor => $"■  {graphic.FillColor1}", GraphicsOverlayKind.Gradient => $"◩  {graphic.FillColor1} → {graphic.FillColor2}", _ => kindName };
                content.Children.Add(new TextBlock { Text = graphicLabel, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13, 0, 13, 0) });
                var leftHandle = new Thumb { Width = 10, Background = new SolidColorBrush(Color.FromRgb(102, 218, 255)), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.SizeWE, ToolTip = "Drag to change start time" };
                var rightHandle = new Thumb { Width = 10, Background = new SolidColorBrush(Color.FromRgb(249, 200, 71)), HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.SizeWE, ToolTip = "Drag to change end time" };
                AttachGraphicTrimHandles(leftHandle, rightHandle, lane, graphic); content.Children.Add(leftHandle); content.Children.Add(rightHandle); block.Child = content;
                AttachGraphicInteractions(block, lane, layer, graphic); block.ContextMenu = CreateGraphicMenu(layer, graphic);
                Canvas.SetLeft(block, left); Canvas.SetTop(block, 10); lane.Children.Add(block);
                AddKeyframeMarkers(lane, graphic.Start, graphic.Duration, graphic.Keyframes, display, laneWidth, 58);
            }
            foreach (var placement in layer.Placements)
            {
                if (!_clipFilmstrips.ContainsKey(placement.Clip) || !_clipWaveforms.ContainsKey(placement.Clip)) _ = EnsureClipVisualsAsync(placement.Clip);
                var left = placement.Start.TotalSeconds / display.TotalSeconds * laneWidth;
                var width = Math.Max(32, placement.Duration.TotalSeconds / display.TotalSeconds * laneWidth);
                var selected = placement.Id == _selectedPlacementId;
                if (layer.Kind == TimelineLayerKind.Audio)
                {
                    var audioContent = new Grid(); audioContent.RowDefinitions.Add(new RowDefinition()); audioContent.RowDefinitions.Add(new RowDefinition());
                    var leftChannel = new TextBlock { Text = $"L  {TimeText.Format(placement.Duration)}", Foreground = layer.MuteLeftChannel ? (Brush)FindResource("MutedBrush") : Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var rightChannel = new TextBlock { Text = "R", Foreground = layer.MuteRightChannel ? (Brush)FindResource("MutedBrush") : Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(rightChannel, 1); audioContent.Children.Add(leftChannel); audioContent.Children.Add(rightChannel);
                    var audioOnly = new Border { Tag = placement, Width = width, Height = 52, CornerRadius = new CornerRadius(5), BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(72, 230, 155)), BorderThickness = selected ? new Thickness(3) : new Thickness(1.5), Background = PlacementBrush(placement, true), Opacity = LayerAudioFullyMuted(layer) ? .35 : 1, ClipToBounds = true, ToolTip = $"Audio: {placement.Clip.DisplayName}\nStart {TimeText.Format(placement.Start)} • Duration {TimeText.Format(placement.Duration)}\nDrag to rearrange • right-click for actions", Child = audioContent };
                    AttachPlacementInteractions(audioOnly, lane, layer, placement); audioOnly.ContextMenu = CreatePlacementMenu(layer, placement); Canvas.SetLeft(audioOnly, left); Canvas.SetTop(audioOnly, 10); lane.Children.Add(audioOnly); AddKeyframeMarkers(lane, placement.Start, placement.Duration, placement.Keyframes, display, laneWidth, 58); continue;
                }
                var video = new Border { Tag = placement, Width = width, Height = 46, CornerRadius = new CornerRadius(5, 5, 2, 2), BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(116, 226, 255)), BorderThickness = selected ? new Thickness(3) : new Thickness(1.5), Background = PlacementBrush(placement, false), ClipToBounds = true, ToolTip = $"{placement.Clip.DisplayName}\nStart {TimeText.Format(placement.Start)} • Duration {TimeText.Format(placement.Duration)}\nDrag to rearrange • right-click for actions" };
                var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)) }; overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); overlay.ColumnDefinitions.Add(new ColumnDefinition()); overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                var leftMark = new TextBlock { Text = "▶", Foreground = (Brush)FindResource("CyanBrush"), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var label = new TextBlock { Text = TimeText.Format(placement.Duration), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 4), Background = new SolidColorBrush(Color.FromArgb(125, 0, 0, 0)) };
                var rightMark = new TextBlock { Text = "◀", Foreground = new SolidColorBrush(Color.FromRgb(249, 200, 71)), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(label, 1); Grid.SetColumn(rightMark, 2); overlay.Children.Add(leftMark); overlay.Children.Add(label); overlay.Children.Add(rightMark);
                if (placement.Effects.Any(effect => effect.IsEnabled))
                {
                    var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(210, 92, 67, 157)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 3, 0), ToolTip = string.Join("\n", placement.Effects.Where(effect => effect.IsEnabled).Select(effect => $"{effect.DisplayName} — {effect.Summary}")), Child = new TextBlock { Text = "fx", Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold } };
                    Grid.SetColumn(badge, 1); overlay.Children.Add(badge);
                }
                video.Child = overlay;
                AttachPlacementInteractions(video, lane, layer, placement);
                var audio = new Border { Tag = placement, Width = width, Height = 22, CornerRadius = new CornerRadius(2, 2, 5, 5), BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(72, 230, 155)), BorderThickness = selected ? new Thickness(2) : new Thickness(1), Background = PlacementBrush(placement, true), Opacity = layer.IsMuted ? .35 : 1, ClipToBounds = true, Child = new TextBlock { Text = layer.IsMuted ? "🔇 MUTED" : "▰ AUDIO", Foreground = Brushes.White, FontSize = 9, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } };
                AttachPlacementInteractions(audio, lane, layer, placement);
                var menu = CreatePlacementMenu(layer, placement); video.ContextMenu = menu; audio.ContextMenu = CreatePlacementMenu(layer, placement);
                Canvas.SetLeft(video, left); Canvas.SetTop(video, 2); lane.Children.Add(video); Canvas.SetLeft(audio, left); Canvas.SetTop(audio, 49); lane.Children.Add(audio);
                AddKeyframeMarkers(lane, placement.Start, placement.Duration, placement.Keyframes, display, laneWidth, 62);
            }
            var isActiveLayer = layer.Id == _activeLayerId;
            var markerColor = new SolidColorBrush(isActiveLayer ? Color.FromRgb(255, 49, 92) : Color.FromRgb(205, 70, 96));
            var playhead = new Grid { Width = 12, Height = rowHeight, IsHitTestVisible = false, Opacity = isActiveLayer ? 1 : .62 };
            playhead.Children.Add(new Border { Width = isActiveLayer ? 4 : 2, Background = markerColor, HorizontalAlignment = HorizontalAlignment.Center });
            playhead.Children.Add(new Ellipse { Width = isActiveLayer ? 11 : 8, Height = isActiveLayer ? 11 : 8, Fill = markerColor, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center });
            playhead.Children.Add(new Ellipse { Width = isActiveLayer ? 11 : 8, Height = isActiveLayer ? 11 : 8, Fill = markerColor, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center });
            Panel.SetZIndex(playhead, 10000); lane.Children.Add(playhead); _layerPlayheads[layer.Id] = (lane, playhead);
            var editCaret = new Grid { Width = 10, Height = rowHeight, IsHitTestVisible = false, Opacity = isActiveLayer ? 1 : .42 };
            editCaret.Children.Add(new Border { Width = isActiveLayer ? 3 : 1, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            editCaret.Children.Add(new Polygon { Points = new PointCollection([new Point(0, 0), new Point(10, 0), new Point(5, 7)]), Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Top });
            editCaret.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(40, 50, 70)), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45) });
            Panel.SetZIndex(editCaret, 9999); lane.Children.Add(editCaret); _layerEditCarets[layer.Id] = (lane, editCaret);
            Grid.SetColumn(lane, 2); row.Children.Add(lane); LayerStack.Children.Add(row);
        }
        UpdateLayerPlayheads();
        _effectsWindow?.RefreshApplied();
        if (CompositionStatus is not null) CompositionStatus.Text = $"  •  LAYERS: {_composition.Layers.Count}  •  WORK: {FormatWholeSeconds(_composition.DisplayDuration)}";
    }

    private void ResizeLayerHeaders(double delta, double laneWidth, double splitterWidth)
    {
        _layerHeaderWidth = Math.Clamp(_layerHeaderWidth + delta, 130, 420);
        foreach (var row in LayerStack.Children.OfType<Grid>())
        {
            if (row.ColumnDefinitions.Count < 3) continue; row.ColumnDefinitions[0].Width = new GridLength(_layerHeaderWidth); row.Width = _layerHeaderWidth + splitterWidth + laneWidth;
        }
        LayerStack.Width = _layerHeaderWidth + splitterWidth + laneWidth;
        LayerStack.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private Grid CreateLayerTimeRuler(TimeSpan display, double laneWidth, double splitterWidth)
    {
        const double rulerHeight = 22;
        var row = new Grid { Height = rulerHeight, Width = _layerHeaderWidth + splitterWidth + laneWidth, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(7, 18, 34)) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_layerHeaderWidth), MinWidth = 130, MaxWidth = 420 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(splitterWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(laneWidth) });
        var header = new Border { BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(7, 0, 0, 0), Child = new TextBlock { Text = "TIME", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("MutedBrush") } };
        row.Children.Add(header);
        var split = new Border { Background = new SolidColorBrush(Color.FromRgb(47, 89, 114)) }; Grid.SetColumn(split, 1); row.Children.Add(split);

        var lane = new Canvas { Width = laneWidth, Height = rulerHeight, Background = new SolidColorBrush(Color.FromRgb(10, 28, 48)), Cursor = Cursors.Hand, ToolTip = "Click or drag to move the white edit caret" };
        var durationSeconds = Math.Max(.001, display.TotalSeconds);
        var major = NiceRulerInterval(durationSeconds / Math.Max(1, laneWidth / 90));
        var minor = major / 5;
        var tickCount = Math.Min(5000, (int)Math.Ceiling(durationSeconds / minor) + 1);
        for (var tick = 0; tick < tickCount; tick++)
        {
            var seconds = tick * minor; if (seconds > durationSeconds + .000001) break;
            var x = seconds / durationSeconds * laneWidth;
            var isMajor = Math.Abs(seconds / major - Math.Round(seconds / major)) < .001;
            lane.Children.Add(new Line { X1 = x, X2 = x, Y1 = isMajor ? 9 : 15, Y2 = rulerHeight, Stroke = new SolidColorBrush(isMajor ? Color.FromRgb(151, 180, 207) : Color.FromRgb(65, 91, 118)), StrokeThickness = isMajor ? 1 : .7, IsHitTestVisible = false });
            if (!isMajor) continue;
            var label = new TextBlock { Text = FormatRulerTime(TimeSpan.FromSeconds(seconds), major), FontFamily = new FontFamily("Consolas"), FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(186, 207, 226)), IsHitTestVisible = false };
            Canvas.SetLeft(label, Math.Min(Math.Max(2, x + 3), Math.Max(2, laneWidth - 62))); Canvas.SetTop(label, -1); lane.Children.Add(label);
        }

        var playhead = new Rectangle { Width = 2, Height = rulerHeight, Fill = new SolidColorBrush(Color.FromRgb(244, 63, 94)), IsHitTestVisible = false };
        var editCaret = new Rectangle { Width = 2, Height = rulerHeight, Fill = Brushes.White, IsHitTestVisible = false };
        Panel.SetZIndex(playhead, 100); Panel.SetZIndex(editCaret, 101); lane.Children.Add(playhead); lane.Children.Add(editCaret);
        _layerTimeRulerLane = lane; _layerTimeRulerPlayhead = playhead; _layerTimeRulerEditCaret = editCaret;

        void SetCaret(MouseEventArgs args)
        {
            var ratio = Math.Clamp(args.GetPosition(lane).X / Math.Max(1, lane.ActualWidth), 0, 1);
            SetEditPosition(TimeSpan.FromSeconds(ratio * display.TotalSeconds));
        }
        lane.MouseLeftButtonDown += (_, e) => { lane.CaptureMouse(); SetCaret(e); e.Handled = true; };
        lane.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && lane.IsMouseCaptured) { SetCaret(e); e.Handled = true; } };
        lane.MouseLeftButtonUp += (_, e) => { if (lane.IsMouseCaptured) lane.ReleaseMouseCapture(); e.Handled = true; };
        lane.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            SetEditPosition(_editPosition + (e.Delta > 0 ? FrameDuration : -FrameDuration)); e.Handled = true;
        };
        Grid.SetColumn(lane, 2); row.Children.Add(lane); return row;
    }

    private static double NiceRulerInterval(double minimum)
    {
        double[] intervals = [.001, .002, .005, .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 18000, 36000];
        return intervals.FirstOrDefault(value => value >= minimum, intervals[^1]);
    }

    private static string FormatRulerTime(TimeSpan value, double intervalSeconds) => intervalSeconds < 1
        ? TimeText.Format(value)
        : value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private void UpdateLayerPlayheads()
    {
        var display = _composition.DisplayDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _composition.DisplayDuration;
        var ratio = Math.Clamp(_sequencePosition.TotalSeconds / display.TotalSeconds, 0, 1);
        foreach (var (_, item) in _layerPlayheads)
        {
            var width = item.Lane.Width > 0 ? item.Lane.Width : item.Lane.ActualWidth;
            Canvas.SetLeft(item.Marker, ratio * Math.Max(1, width) - item.Marker.Width / 2);
        }
        var editRatio = Math.Clamp(_editPosition.TotalSeconds / display.TotalSeconds, 0, 1);
        foreach (var (_, item) in _layerEditCarets)
        {
            var width = item.Lane.Width > 0 ? item.Lane.Width : item.Lane.ActualWidth;
            Canvas.SetLeft(item.Marker, editRatio * Math.Max(1, width) - item.Marker.Width / 2);
        }
        if (_layerTimeRulerLane is not null && _layerTimeRulerPlayhead is not null)
            Canvas.SetLeft(_layerTimeRulerPlayhead, ratio * Math.Max(1, _layerTimeRulerLane.Width) - _layerTimeRulerPlayhead.Width / 2);
        if (_layerTimeRulerLane is not null && _layerTimeRulerEditCaret is not null)
            Canvas.SetLeft(_layerTimeRulerEditCaret, editRatio * Math.Max(1, _layerTimeRulerLane.Width) - _layerTimeRulerEditCaret.Width / 2);
    }

    private static void AddKeyframeMarkers(Canvas lane, TimeSpan start, TimeSpan duration, IEnumerable<AnimationKeyframe> keyframes, TimeSpan display, double laneWidth, double top)
    {
        if (duration <= TimeSpan.Zero || display <= TimeSpan.Zero) return;
        foreach (var group in keyframes.GroupBy(item => item.Offset).OrderBy(item => item.Key))
        {
            var marker = new Rectangle { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(255, 211, 77)), Stroke = Brushes.Black, StrokeThickness = .5, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45), ToolTip = string.Join("\n", group.Select(item => $"{item.Property}: {item.Value:0.###} ({item.Interpolation})")) };
            Canvas.SetLeft(marker, Math.Max(0, (start.TotalSeconds + group.Key.TotalSeconds) / display.TotalSeconds * laneWidth - marker.Width / 2)); Canvas.SetTop(marker, top); Panel.SetZIndex(marker, 9000); lane.Children.Add(marker);
        }
    }

    private Brush PlacementBrush(TimelinePlacement placement, bool audio)
    {
        var total = Math.Max(.001, placement.Clip.SelectedDuration.TotalSeconds);
        var viewbox = new Rect(Math.Clamp(placement.InPoint.TotalSeconds / total, 0, 1), 0, Math.Clamp(placement.Duration.TotalSeconds / total, .0001, 1), 1);
        ImageSource? image = audio ? _clipWaveforms.GetValueOrDefault(placement.Clip) : _clipFilmstrips.GetValueOrDefault(placement.Clip);
        if (image is null) return new SolidColorBrush(audio ? Color.FromRgb(17, 86, 75) : Color.FromRgb(25, 82, 116));
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = viewbox };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); return brush;
    }

    private void AttachPlacementInteractions(Border item, Canvas lane, TimelineLayer layer, TimelinePlacement placement)
    {
        var hoverTime = new ToolTip { PlacementTarget = item, Placement = PlacementMode.MousePoint, StaysOpen = true };
        ToolTipService.SetInitialShowDelay(item, 0);
        item.MouseMove += (_, e) =>
        {
            if (_selectedPlacementId != placement.Id || e.LeftButton == MouseButtonState.Pressed) { hoverTime.IsOpen = false; return; }
            var ratio = Math.Clamp(e.GetPosition(item).X / Math.Max(1, item.ActualWidth), 0, 1); var offset = TimeSpan.FromSeconds(ratio * placement.Duration.TotalSeconds);
            var projectTime = placement.Start + offset; var sequenceTime = placement.InPoint + offset; var source = TimelineOperations.SequenceToSource(placement.Clip.Segments, sequenceTime);
            hoverTime.Content = $"Project {TimeText.Format(projectTime)}  •  Source {TimeText.Format(source.SourceTime)}"; hoverTime.IsOpen = true;
        };
        item.MouseLeave += (_, _) => hoverTime.IsOpen = false;
        item.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ReturnToTimelinePreview();
            _activeLayerId = layer.Id; _selectedPlacementId = placement.Id; _placementDragStart = e.GetPosition(item); _dragPlacement = placement;
            _placementDragOriginalStart = placement.Start; _placementDragOriginalLayerId = layer.Id;
            _placementDragOffset = TimeSpan.FromSeconds(Math.Clamp(e.GetPosition(item).X / Math.Max(1, item.ActualWidth), 0, 1) * placement.Duration.TotalSeconds);
            SetEditPosition(placement.Start + _placementDragOffset); item.BorderBrush = Brushes.White; item.BorderThickness = new Thickness(3); hoverTime.IsOpen = false; e.Handled = true;
        };
        item.PreviewMouseMove += (_, e) =>
        {
            if (_placementDragStart is not { } start || _dragPlacement != placement || e.LeftButton != MouseButtonState.Pressed || Math.Abs(e.GetPosition(item).X - start.X) < 5) return;
            DragDrop.DoDragDrop(item, placement, DragDropEffects.Move); _placementDragStart = null; _dragPlacement = null; _placementDragOriginalLayerId = null; RefreshLayerStack();
        };
        item.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_dragPlacement == placement) { _placementDragStart = null; _dragPlacement = null; RefreshLayerStack(); }
        };
    }

    private ContextMenu CreatePlacementMenu(TimelineLayer layer, TimelinePlacement placement)
    {
        var menu = new ContextMenu();
        var play = new MenuItem { Header = placement.Clip.Media.HasVideo ? "Play Clip" : "Play Audio Clip", IsEnabled = layer.IsVisible }; play.Click += async (_, _) => await PreviewPlacementAsync(layer, placement);
        var split = new MenuItem { Header = "Cut / split at playhead" }; split.Click += (_, _) => { _selectedPlacementId = placement.Id; SplitSelectedPlacement(); };
        var keyframes = new MenuItem { Header = "Keyframes…" }; keyframes.Click += (_, _) => { _activeLayerId = layer.Id; _selectedPlacementId = placement.Id; _selectedGraphicId = null; Keyframe_Click(this, new RoutedEventArgs()); };
        var effects = new MenuItem { Header = "Effects…" }; effects.Click += (_, _) => { _activeLayerId = layer.Id; _selectedPlacementId = placement.Id; _selectedGraphicId = null; EnsureEffectsWindow(); };
        var export = new MenuItem { Header = "Export this clip…" }; export.Click += (_, _) => _ = ExportPlacementAsync(placement);
        var gifDuration = placement.Duration < QuickGifMaximumDuration ? placement.Duration : QuickGifMaximumDuration;
        var exportGif = new MenuItem { Header = placement.Duration > QuickGifMaximumDuration ? $"Export as GIF (first {QuickGifMaximumDuration.TotalSeconds:0}s)…" : "Export as GIF…", IsEnabled = placement.Clip.Media.HasVideo, ToolTip = placement.Clip.Media.HasVideo ? $"Exports up to {QuickGifMaximumDuration.TotalSeconds:0} seconds from this clip's current start." : "GIF export requires a video clip." };
        exportGif.Click += (_, _) => _ = ExportPlacementAsGifAsync(placement, gifDuration);
        var duplicate = new MenuItem { Header = "Duplicate clip to new layer" }; duplicate.Click += (_, _) => DuplicatePlacementToNewLayer(placement);
        var remove = new MenuItem { Header = "Remove clip from layer" }; remove.Click += (_, _) => { EnsureProjectHistory(); TimelineOperations.RemovePlacement(_composition, placement.Id); if (_selectedPlacementId == placement.Id) _selectedPlacementId = null; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts(); };
        menu.Items.Add(play); menu.Items.Add(new Separator()); menu.Items.Add(keyframes); menu.Items.Add(effects); menu.Items.Add(split); menu.Items.Add(duplicate); menu.Items.Add(export); menu.Items.Add(exportGif); menu.Items.Add(new Separator()); menu.Items.Add(remove); return menu;
    }

    private ContextMenu CreateLayerMenu(TimelineLayer layer)
    {
        var menu = new ContextMenu();
        var duplicate = new MenuItem { Header = "Duplicate layer" }; duplicate.Click += (_, _) => DuplicateLayer(layer);
        var remove = new MenuItem { Header = "Delete layer" }; remove.Click += (_, _) => RemoveLayer(layer);
        menu.Items.Add(duplicate); menu.Items.Add(new Separator()); menu.Items.Add(remove); return menu;
    }

    private void DuplicateLayer(TimelineLayer layer)
    {
        EnsureProjectHistory(); var copy = TimelineOperations.DuplicateLayer(_composition, layer.Id); if (copy is null) return;
        _activeLayerId = copy.Id; _selectedPlacementId = null; _selectedGraphicId = null;
        ExtendWorkspace(copy.Placements.Select(item => item.End).Concat(copy.Graphics.Select(item => item.End)).DefaultIfEmpty(TimeSpan.Zero).Max());
        InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts(); UpdateLiveGraphics(_editPosition);
    }

    private void DuplicatePlacementToNewLayer(TimelinePlacement placement)
    {
        EnsureProjectHistory(); var copyLayer = TimelineOperations.DuplicatePlacementToNewLayer(_composition, placement.Id); if (copyLayer is null) return;
        var copy = copyLayer.Placements.Single(); _activeLayerId = copyLayer.Id; _selectedPlacementId = copy.Id; _selectedGraphicId = null;
        ExtendWorkspace(copy.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private void AttachGraphicInteractions(Border item, Canvas lane, TimelineLayer layer, GraphicsOverlay graphic)
    {
        Point? start = null;
        item.PreviewMouseLeftButtonDown += (_, e) => { if (IsInsideThumb(e.OriginalSource as DependencyObject)) return; ReturnToTimelinePreview(); _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null; start = e.GetPosition(item); _dragGraphic = graphic; _graphicDragOffset = TimeSpan.FromSeconds(Math.Clamp(e.GetPosition(item).X / Math.Max(1, item.ActualWidth), 0, 1) * graphic.Duration.TotalSeconds); SetEditPosition(graphic.Start + _graphicDragOffset); e.Handled = true; };
        item.PreviewMouseMove += (_, e) => { if (start is not { } origin || _dragGraphic != graphic || e.LeftButton != MouseButtonState.Pressed || Math.Abs(e.GetPosition(item).X - origin.X) < 5) return; DragDrop.DoDragDrop(item, graphic, DragDropEffects.Move); start = null; _dragGraphic = null; };
        item.PreviewMouseLeftButtonUp += (_, _) => { start = null; _dragGraphic = null; };
    }

    private void AttachGraphicTrimHandles(Thumb left, Thumb right, Canvas lane, GraphicsOverlay graphic)
    {
        var originalStart = graphic.Start; var originalDuration = graphic.Duration;
        left.DragStarted += (_, _) => { originalStart = graphic.Start; originalDuration = graphic.Duration; };
        left.DragDelta += (_, e) => { var delta = TimeSpan.FromSeconds(e.HorizontalChange / Math.Max(1, lane.Width) * Math.Max(.001, _composition.DisplayDuration.TotalSeconds)); var end = graphic.End; graphic.Start = ClampTime(graphic.Start + delta, TimeSpan.Zero, end - TimeSpan.FromMilliseconds(100)); graphic.Duration = end - graphic.Start; UpdateGraphicBlock(left, lane, graphic); };
        right.DragStarted += (_, _) => { originalStart = graphic.Start; originalDuration = graphic.Duration; };
        right.DragDelta += (_, e) => { var delta = TimeSpan.FromSeconds(e.HorizontalChange / Math.Max(1, lane.Width) * Math.Max(.001, _composition.DisplayDuration.TotalSeconds)); graphic.Duration = graphic.Duration + delta < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : graphic.Duration + delta; ExtendWorkspace(graphic.End); UpdateGraphicBlock(right, lane, graphic); };
        left.DragCompleted += (_, _) => { if (graphic.Start != originalStart || graphic.Duration != originalDuration) { InvalidateCompositionPreview(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); } };
        right.DragCompleted += (_, _) => { if (graphic.Start != originalStart || graphic.Duration != originalDuration) { InvalidateCompositionPreview(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); } };
    }

    private static bool IsInsideThumb(DependencyObject? item)
    {
        while (item is not null)
        {
            if (item is Thumb) return true;
            item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }

    private void UpdateGraphicBlock(Thumb handle, Canvas lane, GraphicsOverlay graphic)
    {
        if (handle.Parent is not Grid { Parent: Border block }) return;
        var display = Math.Max(.001, _composition.DisplayDuration.TotalSeconds); Canvas.SetLeft(block, graphic.Start.TotalSeconds / display * lane.Width); block.Width = Math.Max(36, graphic.Duration.TotalSeconds / display * lane.Width);
    }

    private ContextMenu CreateGraphicMenu(TimelineLayer layer, GraphicsOverlay graphic)
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "Edit overlay…" }; edit.Click += (_, _) => { EnsureProjectHistory(); var editor = new GraphicsOverlayEditor(graphic) { Owner = this }; if (editor.ShowDialog() == true) { graphic.RenderedImagePath = null; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); } };
        var keyframes = new MenuItem { Header = "Keyframes…" }; keyframes.Click += (_, _) => { _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null; Keyframe_Click(this, new RoutedEventArgs()); };
        var effects = new MenuItem { Header = "Effects…" }; effects.Click += (_, _) => { _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null; EnsureEffectsWindow(); };
        var duplicate = new MenuItem { Header = "Duplicate overlay to new layer" }; duplicate.Click += (_, _) => { EnsureProjectHistory(); var copy = CloneGraphic(graphic); copy.Start = graphic.End; var copyLayer = CreateGraphicsLayer(copy.Kind); copyLayer.Graphics.Add(copy); ExtendWorkspace(copy.End); _activeLayerId = copyLayer.Id; _selectedGraphicId = copy.Id; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); };
        var remove = new MenuItem { Header = "Remove overlay and layer" }; remove.Click += (_, _) => { EnsureProjectHistory(); layer.Graphics.Remove(graphic); TimelineOperations.RemoveLayer(_composition, layer.Id); if (_selectedGraphicId == graphic.Id) _selectedGraphicId = null; _activeLayerId = _composition.Layers.FirstOrDefault()?.Id; ResetEmptyComposition(); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); };
        menu.Items.Add(edit); menu.Items.Add(keyframes); menu.Items.Add(effects); menu.Items.Add(duplicate); menu.Items.Add(new Separator()); menu.Items.Add(remove); return menu;
    }

    private (ICollection<AnimationKeyframe> Keyframes, TimeSpan Start, TimeSpan Duration, KeyframeProperty[] Properties, Func<KeyframeProperty, double> Fallback)? SelectedKeyframeTarget()
    {
        if (_selectedGraphicId is { } graphicId && _composition.Layers.SelectMany(layer => layer.Graphics).FirstOrDefault(item => item.Id == graphicId) is { } graphic)
            return (graphic.Keyframes, graphic.Start, graphic.Duration,
                [KeyframeProperty.PositionX, KeyframeProperty.PositionY, KeyframeProperty.Scale, KeyframeProperty.Opacity],
                property => property switch { KeyframeProperty.PositionX => graphic.X, KeyframeProperty.PositionY => graphic.Y, KeyframeProperty.Scale => 1, KeyframeProperty.Opacity => graphic.Opacity, _ => 0 });
        if (_selectedPlacementId is { } placementId && _composition.Layers.SelectMany(layer => layer.Placements).FirstOrDefault(item => item.Id == placementId) is { } placement)
        {
            var properties = placement.Clip.Media.HasVideo
                ? (placement.Clip.Media.HasAudio ? new[] { KeyframeProperty.PositionX, KeyframeProperty.PositionY, KeyframeProperty.Scale, KeyframeProperty.Opacity, KeyframeProperty.Volume } : [KeyframeProperty.PositionX, KeyframeProperty.PositionY, KeyframeProperty.Scale, KeyframeProperty.Opacity])
                : [KeyframeProperty.Volume];
            return (placement.Keyframes, placement.Start, placement.Duration, properties,
                property => property is KeyframeProperty.PositionX or KeyframeProperty.PositionY ? .5 : 1);
        }
        return null;
    }

    private void Keyframe_Click(object sender, RoutedEventArgs e)
    {
        var target = SelectedKeyframeTarget();
        if (target is null) { MessageBox.Show(this, "Select a clip, audio item, text overlay, or image overlay first.", "Keyframes", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var value = target.Value;
        if (_editPosition < value.Start || _editPosition > value.Start + value.Duration)
        { MessageBox.Show(this, "Move the white edit caret onto the selected item before adding a keyframe.", "Keyframes", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ShowKeyframeWindow(value);
    }

    private void ShowKeyframeWindow((ICollection<AnimationKeyframe> Keyframes, TimeSpan Start, TimeSpan Duration, KeyframeProperty[] Properties, Func<KeyframeProperty, double> Fallback) value)
    {
        if (_keyframeWindow is not null) { _keyframeWindow.Close(); _keyframeWindow = null; }
        EnsureProjectHistory();
        var targetName = _selectedGraphicId is not null ? "Selected overlay" : _composition.Layers.SelectMany(layer => layer.Placements).FirstOrDefault(item => item.Id == _selectedPlacementId)?.Clip.DisplayName ?? "Selected item";
        _keyframeWindow = new KeyframeEditor(value.Keyframes, _editPosition - value.Start, value.Duration, value.Properties, value.Fallback, targetName,
            () => { InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLivePlayers(_editPosition, false); UpdateLiveGraphics(_editPosition); _ = ShowCurrentProjectFrameAsync(); },
            local => SetEditPosition(value.Start + local),
            () => ClampTime(_sequencePosition - value.Start, TimeSpan.Zero, value.Duration),
            () => _playing, FrameDuration) { Owner = this };
        _keyframeWindow.Closed += (_, _) => { _keyframeWindow = null; if (KeyframesWindowMenuItem is not null) KeyframesWindowMenuItem.IsChecked = false; };
        KeyframesWindowMenuItem.IsChecked = true; _keyframeWindow.Show();
    }

    private void ToggleKeyframesWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_keyframeWindow is not null) { _keyframeWindow.Close(); return; }
        var target = SelectedKeyframeTarget();
        if (target is null) { KeyframesWindowMenuItem.IsChecked = false; MessageBox.Show(this, "Select a timeline item first, then open the Keyframe Timeline.", "Keyframes", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var value = target.Value;
        if (_editPosition < value.Start || _editPosition > value.Start + value.Duration) _editPosition = value.Start;
        ShowKeyframeWindow(value);
    }

    private void ToggleEffectsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_effectsWindow is not null) { _effectsWindow.Close(); return; }
        EnsureEffectsWindow();
    }

    private void EnsureEffectsWindow()
    {
        if (_effectsWindow is not null) { _effectsWindow.Activate(); return; }
        _effectsWindow = new EffectsWindow(new EffectHost(ApplyEffectPreset, AddVideoEffect, RemoveVideoEffect, SetVideoEffectEnabled, ListVideoEffects, DescribeEffectTarget, UpdateVideoEffect, SetPreviewEffect, AddLookStyle)) { Owner = this };
        _effectsWindow.Closed += (_, _) => { _effectsWindow = null; if (EffectsWindowMenuItem is not null) EffectsWindowMenuItem.IsChecked = false; };
        EffectsWindowMenuItem.IsChecked = true; _effectsWindow.Show();
    }

    // ---- effect stack -------------------------------------------------------
    // Effects live on a placement, or on the composition when they should apply to
    // the finished frame. Both are only visible in the rendered preview and the
    // export, so every change has to drop the cached preview.

    private TimelinePlacement? SelectedPlacement() => _composition.Layers.SelectMany(layer => layer.Placements).FirstOrDefault(item => item.Id == _selectedPlacementId);

    private IReadOnlyList<VideoEffect> ListVideoEffects(bool wholeTimeline)
        => wholeTimeline ? _composition.OutputEffects.ToArray() : SelectedPlacement()?.Effects.ToArray() ?? [];

    private string DescribeEffectTarget(bool wholeTimeline)
        => wholeTimeline
            ? "Applies to the finished video, after every layer is composited."
            : SelectedPlacement() is { } placement ? $"Applies to “{placement.Clip.DisplayName}” on the timeline." : "Select a clip on the timeline first, or tick the box below to affect the whole timeline.";

    private void AddVideoEffect(VideoEffect effect, bool wholeTimeline)
    {
        var target = wholeTimeline ? _composition.OutputEffects : SelectedPlacement()?.Effects;
        if (target is null) { MessageBox.Show(this, "Select a clip on the timeline first, or apply the effect to the whole timeline.", "Effects", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        EnsureProjectHistory(); target.Add(effect); AfterEffectChange();
    }

    private void RemoveVideoEffect(Guid id, bool wholeTimeline)
    {
        var target = wholeTimeline ? _composition.OutputEffects : SelectedPlacement()?.Effects;
        var existing = target?.FirstOrDefault(item => item.Id == id);
        if (target is null || existing is null) return;
        EnsureProjectHistory(); target.Remove(existing); AfterEffectChange();
    }

    private void SetVideoEffectEnabled(Guid id, bool wholeTimeline, bool enabled)
    {
        var target = wholeTimeline ? _composition.OutputEffects : SelectedPlacement()?.Effects;
        if (target?.FirstOrDefault(item => item.Id == id) is not { } existing || existing.IsEnabled == enabled) return;
        EnsureProjectHistory(); existing.IsEnabled = enabled; AfterEffectChange();
    }

    private void AfterEffectChange()
    {
        InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); _ = ShowCurrentProjectFrameAsync();
    }

    private void ApplyEffectPreset(EffectsWindow.EffectOptions options)
    {
        var target = SelectedKeyframeTarget();
        if (target is null) { MessageBox.Show(this, "Select a clip, audio item, text overlay, or image overlay first.", "Effects", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var value = target.Value; var duration = value.Duration; var requested = TimeSpan.FromSeconds(Math.Max(.001, options.DurationSeconds)); var transition = requested < duration ? requested : duration;
        KeyframeProperty property; double first; double last; TimeSpan firstAt; TimeSpan lastAt;
        switch (options.Name)
        {
            case "Fade In": property = KeyframeProperty.Opacity; first = options.From; last = options.To; firstAt = TimeSpan.Zero; lastAt = transition; break;
            case "Fade Out": property = KeyframeProperty.Opacity; first = options.From; last = options.To; firstAt = duration - transition; lastAt = duration; break;
            case "Slide In From Left": case "Slide In From Right": property = KeyframeProperty.PositionX; first = options.From; last = options.To; firstAt = TimeSpan.Zero; lastAt = transition; break;
            case "Zoom In": case "Zoom Out": property = KeyframeProperty.Scale; first = options.From; last = options.To; firstAt = TimeSpan.Zero; lastAt = transition; break;
            case "Audio Fade In": property = KeyframeProperty.Volume; first = options.From; last = options.To; firstAt = TimeSpan.Zero; lastAt = transition; break;
            case "Audio Fade Out": property = KeyframeProperty.Volume; first = options.From; last = options.To; firstAt = duration - transition; lastAt = duration; break;
            default: return;
        }
        if (!value.Properties.Contains(property)) { MessageBox.Show(this, $"{options.Name} is not available for the selected item.", "Effects", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        EnsureProjectHistory(); KeyframeEvaluator.Upsert(value.Keyframes, property, firstAt, first, KeyframeInterpolation.Smooth, duration); KeyframeEvaluator.Upsert(value.Keyframes, property, lastAt, last, KeyframeInterpolation.Smooth, duration);
        InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLivePlayers(_editPosition, false); UpdateLiveGraphics(_editPosition); _ = ShowCurrentProjectFrameAsync();
    }

    private void PreviousKeyframe_Click(object sender, RoutedEventArgs e) => MoveEditCaretToKeyframe(false);
    private void NextKeyframe_Click(object sender, RoutedEventArgs e) => MoveEditCaretToKeyframe(true);
    private void MoveEditCaretToKeyframe(bool forward)
    {
        var target = SelectedKeyframeTarget(); if (target is null || target.Value.Keyframes.Count == 0) return;
        Pause(); var positions = target.Value.Keyframes.Select(item => target.Value.Start + item.Offset).Distinct().OrderBy(item => item).ToArray();
        var position = forward ? positions.FirstOrDefault(item => item > _editPosition + TimeSpan.FromMilliseconds(1)) : positions.LastOrDefault(item => item < _editPosition - TimeSpan.FromMilliseconds(1));
        if (position == default && (forward ? positions[0] != TimeSpan.Zero : positions[^1] != TimeSpan.Zero)) position = forward ? positions[0] : positions[^1];
        SetEditPosition(position); RefreshLayerStack();
    }

    private static GraphicsOverlay CloneGraphic(GraphicsOverlay value)
    {
        var copy = new GraphicsOverlay { Kind = value.Kind, Text = value.Text, ImagePath = value.ImagePath, FontFamily = value.FontFamily, FontSize = value.FontSize, Foreground = value.Foreground, Background = value.Background, FillColor1 = value.FillColor1, FillColor2 = value.FillColor2, UseSecondFillColor = value.UseSecondFillColor, GradientKind = value.GradientKind, GradientAngle = value.GradientAngle, Opacity = value.Opacity, PreserveAspectRatio = value.PreserveAspectRatio, X = value.X, Y = value.Y, Width = value.Width, Height = value.Height, Start = value.Start, Duration = value.Duration };
        foreach (var keyframe in value.Keyframes) copy.Keyframes.Add(new AnimationKeyframe { Property = keyframe.Property, Offset = keyframe.Offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation }); return copy;
    }

    private Task EnsureClipVisualsAsync(ClipItem clip, CancellationToken token = default)
    {
        var targetFrames = TargetFilmstripFrames; var targetWaveformWidth = TargetWaveformWidth;
        if (_clipWaveformWidths.GetValueOrDefault(clip) == targetWaveformWidth && _clipFilmstrips.ContainsKey(clip) && _clipFilmstripCounts.GetValueOrDefault(clip) == targetFrames) return Task.CompletedTask;
        if (_clipVisualTasks.TryGetValue(clip, out var existing)) return existing;
        var task = LoadClipVisualsAsync(clip, targetFrames, targetWaveformWidth, token); _clipVisualTasks[clip] = task; return task;
    }

    private int TargetFilmstripFrames => Math.Clamp((int)Math.Ceiling(12 * _timelineZoom), 12, 120);
    private int TargetWaveformWidth => Math.Clamp((int)Math.Ceiling(1800 * _timelineZoom), 1800, 24000);

    private async Task LoadClipVisualsAsync(ClipItem clip, int targetFrames, int targetWaveformWidth, CancellationToken token)
    {
        await Task.Yield(); // Ensure the in-flight task is registered before cached operations can complete.
        try
        {
            if (_clipWaveformWidths.GetValueOrDefault(clip) != targetWaveformWidth)
            {
                try { var wave = await _engine.CreateWaveformAsync(clip.Media, _cache, targetWaveformWidth, token: token); _clipWaveforms[clip] = wave is null ? null : LoadBitmap(wave); _clipWaveformWidths[clip] = targetWaveformWidth; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { _clipWaveforms[clip] = null; _clipWaveformWidths[clip] = targetWaveformWidth; }
            }
            if (!clip.Media.HasVideo)
            {
                _clipFilmstrips[clip] = null; _clipFilmstripCounts[clip] = targetFrames;
            }
            else if (!_clipFilmstrips.ContainsKey(clip) || _clipFilmstripCounts.GetValueOrDefault(clip) != targetFrames)
            {
                try { var strip = await _engine.CreateThumbnailStripAsync(clip.Media, _cache, targetFrames, token: token); _clipFilmstrips[clip] = LoadBitmap(strip); _clipFilmstripCounts[clip] = targetFrames; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { _clipFilmstrips[clip] = null; _clipFilmstripCounts[clip] = targetFrames; }
            }
            RefreshLayerStack();
        }
        finally { _clipVisualTasks.Remove(clip); }
    }

    private async Task RefreshZoomFilmstripsAsync(int generation)
    {
        var target = TargetFilmstripFrames; var waveformWidth = TargetWaveformWidth;
        var clips = _composition.Layers.SelectMany(layer => layer.Placements).Select(item => item.Clip).Distinct().ToArray();
        foreach (var clip in clips)
        {
            if (_clipVisualTasks.TryGetValue(clip, out var pending)) { try { await pending; } catch { } }
            if (generation != _visualZoomGeneration) return;
            if (_clipWaveformWidths.GetValueOrDefault(clip) != waveformWidth)
            {
                try { var wave = await _engine.CreateWaveformAsync(clip.Media, _cache, waveformWidth); if (generation != _visualZoomGeneration) return; _clipWaveforms[clip] = wave is null ? null : LoadBitmap(wave); _clipWaveformWidths[clip] = waveformWidth; }
                catch { _clipWaveforms[clip] = null; _clipWaveformWidths[clip] = waveformWidth; }
            }
            if (clip.Media.HasVideo && _clipFilmstripCounts.GetValueOrDefault(clip) != target)
            {
                try { var strip = await _engine.CreateThumbnailStripAsync(clip.Media, _cache, target); if (generation != _visualZoomGeneration) return; _clipFilmstrips[clip] = LoadBitmap(strip); _clipFilmstripCounts[clip] = target; }
                catch { _clipFilmstrips[clip] = null; _clipFilmstripCounts[clip] = target; }
            }
        }
        if (_current is not null) _waveformImage = _clipWaveforms.GetValueOrDefault(_current);
        RefreshLayerStack(); DrawCuts();
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
    }

    private void LayersArea_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var hasMedia = e.Data.GetDataPresent(typeof(ClipItem[])) || e.Data.GetDataPresent(typeof(ClipItem));
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasMedia || hasFiles ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
    }

    private async void LayersArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return; e.Handled = true;
        if (!TryClaimLayerDrop(e)) { e.Effects = DragDropEffects.None; return; }
        var start = LayerDropTime(e);
        ClipItem[] items;
        if (e.Data.GetData(typeof(ClipItem[])) is ClipItem[] selected) items = selected.Where(_clips.Contains).Distinct().ToArray();
        else if (e.Data.GetData(typeof(ClipItem)) is ClipItem single) items = [single];
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var mediaPaths = paths.Where(path => !IsProjectFile(path) && MediaProbeService.IsSupported(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (mediaPaths.Length == 0) { e.Effects = DragDropEffects.None; return; }
            await LoadFilesAsync(mediaPaths, initializeComposition: false);
            items = mediaPaths.Select(path => _clips.FirstOrDefault(clip => clip.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase))).Where(clip => clip is not null).Cast<ClipItem>().ToArray();
        }
        else { e.Effects = DragDropEffects.None; return; }
        if (items.Length == 0) { e.Effects = DragDropEffects.None; return; }
        foreach (var item in items) AddMediaAsNewLayer(item, start);
        _selectedMedia.Clear(); foreach (var item in items) _selectedMedia.Add(item); RefreshProjectMedia(); e.Effects = DragDropEffects.Copy;
    }

    private TimeSpan LayerDropTime(DragEventArgs e)
    {
        var x = e.GetPosition(LayerStack).X - _layerHeaderWidth - 5;
        var laneWidth = Math.Max(1, LayerStack.ActualWidth - _layerHeaderWidth - 5);
        return TimeSpan.FromSeconds(Math.Clamp(x / laneWidth, 0, 1) * VisibleDuration.TotalSeconds);
    }

    private void Layer_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Canvas { Tag: TimelineLayer target }) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (e.Data.GetData(typeof(ClipItem)) is ClipItem media)
            e.Effects = media.Media.IsStillImage || media.Media.HasVideo || media.Media.HasAudio ? DragDropEffects.Copy : DragDropEffects.None;
        else if (target.Kind != TimelineLayerKind.Graphics && e.Data.GetData(typeof(TimelinePlacement)) is TimelinePlacement placement)
        {
            e.Effects = DragDropEffects.Move; PreviewPlacementDrag(target, placement, e);
        }
        else e.Effects = target.Kind == TimelineLayerKind.Graphics && e.Data.GetDataPresent(typeof(GraphicsOverlay)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PreviewPlacementDrag(TimelineLayer target, TimelinePlacement placement, DragEventArgs e)
    {
        var source = _composition.Layers.FirstOrDefault(layer => layer.Placements.Contains(placement));
        if (source is null || !_layerPlayheads.TryGetValue(source.Id, out var sourceView) || !_layerPlayheads.TryGetValue(target.Id, out var targetView)) return;
        var display = _composition.DisplayDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _composition.DisplayDuration;
        var cursor = TimeSpan.FromSeconds(Math.Clamp(e.GetPosition(targetView.Lane).X / Math.Max(1, targetView.Lane.ActualWidth), 0, 1) * display.TotalSeconds);
        var proposed = cursor - _placementDragOffset; if (proposed < TimeSpan.Zero) proposed = TimeSpan.Zero;
        var left = proposed.TotalSeconds / display.TotalSeconds * sourceView.Lane.Width;
        foreach (var element in sourceView.Lane.Children.OfType<FrameworkElement>().Where(element => ReferenceEquals(element.Tag, placement))) Canvas.SetLeft(element, left);
    }

    private void Layer_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Canvas lane || lane.Tag is not TimelineLayer target) return;
        var display = _composition.DisplayDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : _composition.DisplayDuration;
        var cursor = TimeSpan.FromSeconds(Math.Clamp(e.GetPosition(lane).X / Math.Max(1, lane.ActualWidth), 0, 1) * display.TotalSeconds);
        if (e.Data.GetData(typeof(ClipItem)) is ClipItem mediaItem)
        {
            if (!TryClaimLayerDrop(e)) { e.Handled = true; return; }
            AddMediaAsNewLayer(mediaItem, cursor, _composition.Layers.IndexOf(target) + 1, target);
            e.Effects = DragDropEffects.Copy; e.Handled = true; return;
        }
        if (e.Data.GetData(typeof(GraphicsOverlay)) is GraphicsOverlay graphic && target.Kind == TimelineLayerKind.Graphics)
        {
            if (!TryClaimLayerDrop(e)) { e.Handled = true; return; }
            var source = _composition.Layers.FirstOrDefault(layer => layer.Graphics.Contains(graphic)); if (source is null) return;
            if (!ReferenceEquals(source, target)) { source.Graphics.Remove(graphic); target.Graphics.Add(graphic); if (source.Graphics.Count == 0 && source.Placements.Count == 0) _composition.Layers.Remove(source); }
            graphic.Start = SnapGraphicStart(cursor - _graphicDragOffset, graphic.Duration, graphic.Id, display, lane.ActualWidth);
            _activeLayerId = target.Id; _selectedGraphicId = graphic.Id; ExtendWorkspace(graphic.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); e.Handled = true; return;
        }
        if (target.Kind == TimelineLayerKind.Graphics) return;
        TimelinePlacement? moved = null;
        if (e.Data.GetData(typeof(TimelinePlacement)) is TimelinePlacement placement)
        {
            if (!TryClaimLayerDrop(e)) { e.Handled = true; return; }
            if (target.Kind == TimelineLayerKind.Audio && !placement.Clip.Media.HasAudio) { MessageBox.Show(this, $"{placement.Clip.DisplayName} does not contain an audio stream.", "Audio layer", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var proposed = cursor - _placementDragOffset;
            var threshold = TimeSpan.FromSeconds(Math.Min(.5, display.TotalSeconds * 10 / Math.Max(1, lane.ActualWidth)));
            var source = _composition.Layers.FirstOrDefault(layer => layer.Placements.Contains(placement));
            if (source is not null && ReferenceEquals(source, target))
            {
                var original = _placementDragOriginalLayerId == source.Id ? _placementDragOriginalStart : placement.Start;
                var action = TimelineOperations.PlaceWithinLayer(target, placement, proposed, original);
                if (action == PlacementDropAction.Moved)
                {
                    var snapped = TimelineOperations.SnapPlacementStart(_composition, placement.Id, placement.Start, placement.Duration, threshold);
                    TimelineOperations.MovePlacement(_composition, placement.Id, target.Id, snapped);
                }
            }
            else
            {
                var start = TimelineOperations.SnapPlacementStart(_composition, placement.Id, proposed, placement.Duration, threshold);
                TimelineOperations.MovePlacement(_composition, placement.Id, target.Id, start);
            }
            moved = placement;
        }
        else return;
        _activeLayerId = target.Id; _selectedPlacementId = moved?.Id; if (_composition.ContentDuration > _composition.WorkspaceDuration) _composition.WorkspaceDuration = _composition.ContentDuration; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts(); e.Effects = DragDropEffects.Move; e.Handled = true;
    }

    private static bool TryClaimLayerDrop(DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(LayerDropHandledFormat)) return false;
            e.Data.SetData(LayerDropHandledFormat, true);
        }
        catch
        {
            // Shell drag data can be read-only. Routed-event handling still prevents parent handlers in that case.
        }
        return true;
    }

    private void AddMediaAsNewLayer(ClipItem clip, TimeSpan proposedStart, int? insertIndex = null, TimelineLayer? preferredLayer = null)
    {
        EnsureProjectHistory(); _compositionInitialized = true;
        if (clip.Media.IsStillImage)
        {
            var graphic = CreateImageGraphic(clip.SourcePath, proposedStart);
            var imageLayer = preferredLayer is { Kind: TimelineLayerKind.Graphics }
                ? preferredLayer
                : new TimelineLayer { Name = $"Image {_composition.Layers.Count(layer => layer.Kind == TimelineLayerKind.Graphics) + 1}", Kind = TimelineLayerKind.Graphics };
            imageLayer.Graphics.Add(graphic); if (!_composition.Layers.Contains(imageLayer)) InsertLayer(imageLayer, insertIndex);
            _activeLayerId = imageLayer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null;
            ExtendWorkspace(graphic.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition); return;
        }

        var kind = clip.Media.HasVideo ? TimelineLayerKind.Video : TimelineLayerKind.Audio;
        var automatic = _project.AutomaticPlacementId is { } automaticId
            ? _composition.Layers.SelectMany(item => item.Placements.Select(placement => (Layer: item, Placement: placement))).FirstOrDefault(item => item.Placement.Id == automaticId)
            : default;
        if (automatic.Placement is not null && ReferenceEquals(automatic.Placement.Clip, clip))
        {
            automatic.Placement.Start = proposedStart < TimeSpan.Zero ? TimeSpan.Zero : proposedStart; _project.AutomaticPlacementId = null;
            _activeLayerId = automatic.Layer.Id; _selectedPlacementId = automatic.Placement.Id; _selectedGraphicId = null;
            ExtendWorkspace(automatic.Placement.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts(); return;
        }
        var prefix = kind == TimelineLayerKind.Video ? "Layer" : "Audio";
        var layer = preferredLayer is not null && preferredLayer.Kind == kind && preferredLayer.Placements.Count == 0 && preferredLayer.Graphics.Count == 0
            ? preferredLayer
            : new TimelineLayer { Name = $"{prefix} {_composition.Layers.Count(item => item.Kind == kind) + 1}", Kind = kind };
        if (!_composition.Layers.Contains(layer)) InsertLayer(layer, insertIndex);
        var threshold = TimeSpan.FromSeconds(Math.Min(.5, VisibleDuration.TotalSeconds * 10 / Math.Max(1, CompositionScroll.ActualWidth)));
        var start = TimelineOperations.SnapPlacementStart(_composition, null, proposedStart, clip.SelectedDuration, threshold);
        var placement = TimelineOperations.AddPlacement(layer, clip, start);
        _activeLayerId = layer.Id; _selectedPlacementId = placement.Id; _selectedGraphicId = null;
        ExtendWorkspace(placement.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private void InsertLayer(TimelineLayer layer, int? insertIndex)
    {
        if (insertIndex is { } index && index >= 0 && index <= _composition.Layers.Count) _composition.Layers.Insert(index, layer);
        else _composition.Layers.Add(layer);
    }

    private TimeSpan SnapGraphicStart(TimeSpan proposed, TimeSpan duration, Guid movingId, TimeSpan display, double laneWidth)
    {
        proposed = proposed < TimeSpan.Zero ? TimeSpan.Zero : proposed; var threshold = TimeSpan.FromSeconds(Math.Min(.5, display.TotalSeconds * 10 / Math.Max(1, laneWidth)));
        var candidates = _composition.Layers.SelectMany(layer => layer.Placements.SelectMany(item => new[] { item.Start, item.End }).Concat(layer.Graphics.Where(item => item.Id != movingId).SelectMany(item => new[] { item.Start, item.End }))).Append(TimeSpan.Zero);
        var best = proposed; var distance = threshold + TimeSpan.FromTicks(1);
        foreach (var candidate in candidates) foreach (var start in new[] { candidate, candidate - duration }) { if (start < TimeSpan.Zero) continue; var current = (start - proposed).Duration(); if (current <= threshold && current < distance) { best = start; distance = current; } }
        return best;
    }

    private void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0) { MessageBox.Show(this, "Add a media file first. Layer 1 will be created automatically at that file's length.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        EnsureProjectHistory(); var layer = new TimelineLayer { Name = $"Layer {_composition.Layers.Count + 1}" }; _composition.Layers.Add(layer); _activeLayerId = layer.Id; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack();
    }

    private void AddAudioLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0) { MessageBox.Show(this, "Add project media before creating an audio layer.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var count = _composition.Layers.Count(layer => layer.Kind == TimelineLayerKind.Audio) + 1;
        EnsureProjectHistory(); var layer = new TimelineLayer { Name = $"Audio {count}", Kind = TimelineLayerKind.Audio }; _composition.Layers.Add(layer); _activeLayerId = layer.Id; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack();
    }

    private TimelineLayer CreateGraphicsLayer(GraphicsOverlayKind kind)
    {
        var label = GraphicKindName(kind);
        var count = _composition.Layers.Count(layer => layer.Kind == TimelineLayerKind.Graphics && layer.Name.StartsWith(label, StringComparison.OrdinalIgnoreCase)) + 1;
        var layer = new TimelineLayer { Name = $"{label} {count}", Kind = TimelineLayerKind.Graphics };
        _composition.Layers.Insert(0, layer); return layer;
    }

    private static string GraphicKindName(GraphicsOverlayKind kind) => kind switch
    {
        GraphicsOverlayKind.Text => "Text",
        GraphicsOverlayKind.Image => "Image",
        GraphicsOverlayKind.SolidColor => "Solid Color",
        GraphicsOverlayKind.Gradient => "Gradient",
        GraphicsOverlayKind.Lottie => "Animation",
        _ => "Graphic"
    };

    private void AddGraphic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var text = new MenuItem { Header = "Text" }; text.Click += AddTextOverlay_Click;
        var image = new MenuItem { Header = "Image…" }; image.Click += AddPngOverlay_Click;
        var solid = new MenuItem { Header = "Solid Color…" }; solid.Click += (_, args) => AddFillGraphic(GraphicsOverlayKind.SolidColor, args);
        var gradient = new MenuItem { Header = "Gradient…" }; gradient.Click += (_, args) => AddFillGraphic(GraphicsOverlayKind.Gradient, args);
        menu.Items.Add(text); menu.Items.Add(image); menu.Items.Add(new Separator()); menu.Items.Add(solid); menu.Items.Add(gradient);
        menu.IsOpen = true;
    }

    private void AddFillGraphic(GraphicsOverlayKind kind, RoutedEventArgs e)
    {
        if (_clips.Count == 0) { MessageBox.Show(this, "Add project media before creating graphics.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var graphic = new GraphicsOverlay
        {
            Kind = kind, Start = _editPosition, Duration = DefaultGraphicDuration(), X = .2, Y = .2, Width = .6, Height = .6,
            FillColor1 = kind == GraphicsOverlayKind.SolidColor ? "#FFFFFFFF" : "#FF2563EB", FillColor2 = "#FF7C3AED", UseSecondFillColor = kind == GraphicsOverlayKind.Gradient
        };
        var editor = new GraphicsOverlayEditor(graphic) { Owner = this }; if (editor.ShowDialog() != true) return;
        var layer = CreateGraphicsLayer(kind); layer.Graphics.Add(graphic); _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null;
        ExtendWorkspace(graphic.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_editPosition);
    }

    private void AddTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0) { MessageBox.Show(this, "Add project media before creating graphics.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var graphic = new GraphicsOverlay { Kind = GraphicsOverlayKind.Text, Start = _editPosition, Duration = DefaultGraphicDuration() };
        var editor = new GraphicsOverlayEditor(graphic) { Owner = this }; if (editor.ShowDialog() != true) return;
        var layer = CreateGraphicsLayer(GraphicsOverlayKind.Text); layer.Graphics.Add(graphic); _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null;
        ExtendWorkspace(graphic.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition);
    }

    private void AddPngOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_clips.Count == 0) { MessageBox.Show(this, "Add project media before creating graphics.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff", Multiselect = false }; if (dialog.ShowDialog(this) != true) return;
        var graphic = CreateImageGraphic(dialog.FileName, _editPosition); graphic.Duration = DefaultGraphicDuration();
        var editor = new GraphicsOverlayEditor(graphic) { Owner = this }; if (editor.ShowDialog() != true) return;
        var layer = CreateGraphicsLayer(GraphicsOverlayKind.Image); layer.Graphics.Add(graphic); _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id; _selectedPlacementId = null;
        ExtendWorkspace(graphic.End); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_sequencePosition);
    }

    private TimeSpan DefaultGraphicDuration()
    {
        var remaining = _composition.OutputDuration - _sequencePosition;
        return remaining > TimeSpan.Zero && remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
    }
    private void ExtendWorkspace(TimeSpan end) { if (end > _composition.WorkspaceDuration) _composition.WorkspaceDuration = end; }

    private void RemoveLayer(TimelineLayer layer)
    {
        var itemCount = layer.Placements.Count + layer.Graphics.Count;
        if (itemCount > 0 && MessageBox.Show(this, $"Remove {layer.Name} and its {itemCount} item(s) from the composition?\nSource media files will not be deleted.", "Remove layer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!TimelineOperations.RemoveLayer(_composition, layer.Id)) return;
        if (_project.AutomaticPlacementId is { } automatic && layer.Placements.Any(item => item.Id == automatic)) _project.AutomaticPlacementId = null;
        _activeLayerId = _composition.Layers.FirstOrDefault()?.Id; _selectedPlacementId = null; _selectedGraphicId = null;
        ResetEmptyComposition(); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateProjectUi();
    }

    private void ResetEmptyComposition()
    {
        if (_composition.Layers.Count != 0) return;
        _composition.WorkspaceDuration = TimeSpan.Zero; _composition.RenderWorkspaceTailAsBlack = false; _sequencePosition = TimeSpan.Zero; _editPosition = TimeSpan.Zero; _position = TimeSpan.Zero;
        CancelScrubFrame(); StopLivePreview(); Player.Stop(); Player.Source = null; DropPanel.Visibility = Visibility.Visible;
    }

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        var length = new TextBox { Text = FormatWholeSeconds(_composition.WorkspaceDuration), Margin = new Thickness(0, 6, 0, 12) };
        var black = new CheckBox { Content = "Render unused time at the end as black", IsChecked = _composition.RenderWorkspaceTailAsBlack, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 14) };
        var note = new TextBlock { Text = "When unchecked, extra working space is ignored during playback/export. Empty gaps between positioned clips remain black so timing is preserved.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var apply = new Button { Content = "Apply Working Area", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(22) }; panel.Children.Add(new TextBlock { Text = "Working area length (MM:SS or whole seconds)", FontWeight = FontWeights.SemiBold }); panel.Children.Add(length); panel.Children.Add(black); panel.Children.Add(note); panel.Children.Add(apply);
        var window = new Window { Title = "Project Working Area", Width = 470, Height = 280, Content = panel, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        apply.Click += (_, _) =>
        {
            TimeSpan value;
            if (double.TryParse(length.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) value = TimeSpan.FromSeconds(Math.Ceiling(seconds)); else if (TimeSpan.TryParse(length.Text, CultureInfo.InvariantCulture, out value)) value = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds)); else { MessageBox.Show(window, "Enter whole seconds or a time such as 02:30.", "Invalid duration"); return; }
            if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(24)) { MessageBox.Show(window, "Choose a working area between 1 second and 24 hours.", "Invalid duration"); return; }
            EnsureProjectHistory(); _composition.WorkspaceDuration = value; _composition.RenderWorkspaceTailAsBlack = black.IsChecked == true; InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); window.DialogResult = true;
        };
        window.ShowDialog();
    }

    private async void PreviewLayers_Click(object sender, RoutedEventArgs e) => await PlayProjectAsync();

    private ContextMenu CreateSegmentMenu(MediaSegment segment)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove piece from timeline" }; remove.Click += (_, _) => RemoveTimelineSegment(segment.Id);
        var duplicate = new MenuItem { Header = "Duplicate piece" }; duplicate.Click += (_, _) => DuplicateTimelineSegment(segment.Id);
        var left = new MenuItem { Header = "Move piece left" }; left.Click += (_, _) => MoveTimelineSegment(segment.Id, -1);
        var right = new MenuItem { Header = "Move piece right" }; right.Click += (_, _) => MoveTimelineSegment(segment.Id, 1);
        menu.Items.Add(remove); menu.Items.Add(new Separator()); menu.Items.Add(duplicate); menu.Items.Add(left); menu.Items.Add(right); return menu;
    }

    private void RemoveTimelineSegment(Guid id)
    {
        if (_current is null) return;
        if (_current.Segments.Count <= 1) { MessageBox.Show(this, "The final piece cannot be removed. Use Reset to restore the source or load another video.", "Post", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Pause(); _selectedSegmentId = id; RecordAnd(() => TimelineOperations.Remove(_current, id)); ReanchorAfterEdit();
    }

    private void DuplicateTimelineSegment(Guid id) { if (_current is null) return; Pause(); _selectedSegmentId = id; RecordAnd(() => TimelineOperations.Duplicate(_current, id)); ReanchorAfterEdit(); }
    private void MoveTimelineSegment(Guid id, int delta) { if (_current is null) return; var index = _current.Segments.ToList().FindIndex(s => s.Id == id); if (index < 0) return; Pause(); _selectedSegmentId = id; RecordAnd(() => TimelineOperations.Move(_current, id, index + delta + (delta > 0 ? 1 : 0))); ReanchorAfterEdit(); }

    private void ResetMarkers() { if (_current is not null) { Pause(); RecordAnd(() => { _current.Segments.Clear(); _current.Segments.Add(new MediaSegment { SourceStart = TimeSpan.Zero, SourceEnd = _current.Media.Duration }); _current.PendingCutStart = null; _current.PendingCutEnd = null; }); _selectedSegmentId = _current.Segments[0].Id; SeekSequence(TimeSpan.Zero); } }
    private void Undo() { EnsureProjectHistory(); if (_project.ProjectHistory.Undo() is { } state) RestoreProjectSnapshot(state); }
    private void Redo() { EnsureProjectHistory(); if (_project.ProjectHistory.Redo() is { } state) RestoreProjectSnapshot(state); }

    private ExportOptions GetOptions() => new() { Mode = _mode, Aspect = _aspect, Speed = _exportSpeed, Volume = _exportVolume, CustomSizeMb = _customSizeMb, CropZoom = CropZoom.Value, PanX = PanX.Value, PanY = PanY.Value, CopyToClipboard = _settings.AutoCopyExports, VideoQualityCrf = _settings.VideoQualityCrf, AudioBitrateKbps = _settings.AudioBitrateKbps };
    /// <summary>
    /// Options for exports with no timeline of their own. Timeline exports read effects
    /// from the composition, so these must not be added there as well.
    /// </summary>
    private ExportOptions GetClipOptions() => GetOptions() with { Effects = _composition.OutputEffects.Select(effect => effect.Clone()).ToArray(), Equalizer = _composition.Equalizer.Clone() };
    private string VideoExtension => new[] { "mp4", "mkv", "mov", "webm", "avi" }.Contains(_settings.DefaultVideoFormat, StringComparer.OrdinalIgnoreCase) ? _settings.DefaultVideoFormat.ToLowerInvariant() : "mp4";
    private static string VideoFilter(string extension) => extension switch { "mkv" => "Matroska Video|*.mkv", "mov" => "QuickTime Video|*.mov", "webm" => "WebM Video|*.webm", "avi" => "AVI Video|*.avi", _ => "MP4 Video|*.mp4" };
    private string DefaultName(ClipItem c, bool gif = false, string? extension = null) => $"{System.IO.Path.GetFileNameWithoutExtension(c.SourcePath)}_Post_{DateTime.Now:yyyyMMdd_HHmmss}.{(gif ? "gif" : extension ?? VideoExtension)}";

    private Task ExportCurrentAsync()
    {
        if (_current is null) return Task.CompletedTask;
        var gif = _mode == ExportMode.Gif; var extension = gif ? "gif" : VideoExtension; var dialog = new SaveFileDialog { FileName = DefaultName(_current, gif), Filter = gif ? "Animated GIF|*.gif" : VideoFilter(extension), DefaultExt = extension, AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var options = HasCustomComposition ? GetOptions() : GetClipOptions(); var output = dialog.FileName;
        if (HasCustomComposition)
        {
            PrepareGraphicsForExport();
            var composition = CloneComposition(_composition, out var scratch);
            StartExportJob("Timeline export", output, (progress, token) => _engine.ExportCompositionAsync(composition, output, options, progress, token), job => FinishExportedFile(job, options.CopyToClipboard), scratch);
        }
        else
        {
            var clip = CloneClip(_current);
            StartExportJob($"Export {clip.DisplayName}", output, (progress, token) => _engine.ExportAsync(clip, output, options, progress, token), job => FinishExportedFile(job, options.CopyToClipboard));
        }
        return Task.CompletedTask;
    }

    /// <summary>Post-export housekeeping for a background job that produced one file.</summary>
    private void FinishExportedFile(ExportJob job, bool copyToClipboard)
    {
        if (job.OutputPath is null || !File.Exists(job.OutputPath)) return;
        if (copyToClipboard) CopyFile(job.OutputPath);
    }

    private Task ExportAudioAsync()
    {
        if (!_composition.Layers.Any(layer => layer.IsVisible && !LayerAudioFullyMuted(layer) && layer.Placements.Any(placement => placement.Clip.Media.HasAudio))) { MessageBox.Show(this, "No visible, unmuted layers contain audio.", "Export Audio", MessageBoxButton.OK, MessageBoxImage.Information); return Task.CompletedTask; }
        var extension = new[] { "mp3", "m4a", "wav", "flac", "ogg" }.Contains(_settings.DefaultAudioFormat, StringComparer.OrdinalIgnoreCase) ? _settings.DefaultAudioFormat.ToLowerInvariant() : "mp3";
        var filter = extension switch { "m4a" => "MPEG-4 Audio|*.m4a", "wav" => "Wave Audio|*.wav", "flac" => "FLAC Audio|*.flac", "ogg" => "Ogg Vorbis Audio|*.ogg", _ => "MP3 Audio|*.mp3" };
        var baseName = _current is null ? $"Post_Audio_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}" : DefaultName(_current, false, extension);
        var dialog = new SaveFileDialog { FileName = baseName, Filter = filter, DefaultExt = extension, AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder }; if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var options = GetOptions(); var output = dialog.FileName;
        var composition = CloneComposition(_composition, out var scratch);
        StartExportJob("Audio export", output, (progress, token) => _engine.ExportCompositionAudioAsync(composition, output, options, progress, token), job => FinishExportedFile(job, options.CopyToClipboard), scratch);
        return Task.CompletedTask;
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var mode = new ComboBox { Margin = new Thickness(0, 5, 0, 12) }; foreach (var value in Enum.GetValues<ExportMode>()) mode.Items.Add(value); mode.SelectedItem = _mode;
        var customSize = new TextBox { Text = _customSizeMb.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 5, 0, 12) };
        var exportVolume = new Slider { Minimum = 0, Maximum = 2, Value = _exportVolume, TickFrequency = .1, IsSnapToTickEnabled = false, Margin = new Thickness(0, 5, 0, 0) };
        var exportVolumeLabel = new TextBlock { Text = $"{exportVolume.Value * 100:0}%", Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 12) }; exportVolume.ValueChanged += (_, args) => exportVolumeLabel.Text = $"{args.NewValue * 100:0}%";
        var speeds = new[] { .5, 1d, 1.5, 2d, 4d }; var speed = new ComboBox { Margin = new Thickness(0, 5, 0, 12) }; foreach (var value in speeds) speed.Items.Add($"{value:0.#}x"); speed.SelectedIndex = Math.Max(0, Array.IndexOf(speeds, _exportSpeed));
        var video = new ComboBox { Margin = new Thickness(0, 5, 0, 12), SelectedValue = _settings.DefaultVideoFormat }; foreach (var value in new[] { "mp4", "mkv", "mov", "webm", "avi" }) video.Items.Add(value);
        video.SelectedItem = video.Items.Cast<string>().FirstOrDefault(item => item.Equals(_settings.DefaultVideoFormat, StringComparison.OrdinalIgnoreCase)) ?? "mp4";
        var audio = new ComboBox { Margin = new Thickness(0, 5, 0, 12) }; foreach (var value in new[] { "mp3", "m4a", "wav", "flac", "ogg" }) audio.Items.Add(value); audio.SelectedItem = audio.Items.Cast<string>().FirstOrDefault(item => item.Equals(_settings.DefaultAudioFormat, StringComparison.OrdinalIgnoreCase)) ?? "mp3";
        var quality = new TextBox { Text = _settings.VideoQualityCrf.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 5, 0, 12) }; var bitrate = new TextBox { Text = _settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 5, 0, 12) };
        var encoder = new ComboBox { Margin = new Thickness(0, 5, 0, 4), DisplayMemberPath = "Label", SelectedValuePath = "Name" };
        var encoderHint = new TextBlock { Text = "Looking for a hardware encoder\u2026", Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        _ = LoadEncodersAsync(encoder, encoderHint);
        var save = new Button { Content = "Save Export Settings", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Render mode", FontWeight = FontWeights.SemiBold }); panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "Custom target size (MB)", FontWeight = FontWeights.SemiBold }); panel.Children.Add(customSize);
        panel.Children.Add(new TextBlock { Text = "Export volume (0–200%)", FontWeight = FontWeights.SemiBold }); panel.Children.Add(exportVolume); panel.Children.Add(exportVolumeLabel);
        panel.Children.Add(new TextBlock { Text = "Playback / export speed", FontWeight = FontWeights.SemiBold }); panel.Children.Add(speed);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Default video format", FontWeight = FontWeights.SemiBold }); panel.Children.Add(video); panel.Children.Add(new TextBlock { Text = "Video quality (CRF 10–40; lower is sharper)", FontWeight = FontWeights.SemiBold }); panel.Children.Add(quality); panel.Children.Add(new TextBlock { Text = "Default audio format", FontWeight = FontWeights.SemiBold }); panel.Children.Add(audio); panel.Children.Add(new TextBlock { Text = "Audio bitrate (64–512 kbps; ignored by WAV/FLAC)", FontWeight = FontWeights.SemiBold }); panel.Children.Add(bitrate);
        panel.Children.Add(new TextBlock { Text = "Encoder", FontWeight = FontWeights.SemiBold }); panel.Children.Add(encoder); panel.Children.Add(encoderHint);
        panel.Children.Add(save);
        var window = new Window { Title = "Export Settings", Width = 480, Height = 720, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        save.Click += (_, _) => { if (!int.TryParse(customSize.Text, out var sizeMb) || sizeMb is < 1 or > 10000) { MessageBox.Show(window, "Custom target size must be between 1 and 10,000 MB.", "Export Settings"); return; } if (!int.TryParse(quality.Text, out var crf) || crf is < 10 or > 40) { MessageBox.Show(window, "Video quality must be between 10 and 40.", "Export Settings"); return; } if (!int.TryParse(bitrate.Text, out var kbps) || kbps is < 64 or > 512) { MessageBox.Show(window, "Audio bitrate must be between 64 and 512 kbps.", "Export Settings"); return; } _mode = (ExportMode)mode.SelectedItem; _customSizeMb = sizeMb; _exportVolume = exportVolume.Value; _exportSpeed = speeds[Math.Max(0, speed.SelectedIndex)]; _settings = _settings with { DefaultVideoFormat = (string)video.SelectedItem, DefaultAudioFormat = (string)audio.SelectedItem, VideoQualityCrf = crf, AudioBitrateKbps = kbps, VideoEncoder = (string?)encoder.SelectedValue ?? _settings.VideoEncoder }; _settings.Save(); _engine.EncoderPreference = _settings.VideoEncoder; window.DialogResult = true; };
        window.ShowDialog();
    }

    /// <summary>
    /// Fills the encoder list with what this machine can actually run. Each candidate has
    /// to encode a frame to prove itself, so this happens off the dialog's opening.
    /// </summary>
    private async Task LoadEncodersAsync(ComboBox box, TextBlock hint)
    {
        IReadOnlyList<VideoEncoder> found;
        try { found = await _engine.AvailableEncodersAsync(); }
        catch { hint.Text = "Could not check for a hardware encoder; exports will use the CPU."; return; }

        box.Items.Add(new { Name = "auto", Label = found[0].IsHardware ? $"Automatic \u2014 {found[0].Label}" : "Automatic \u2014 CPU" });
        foreach (var item in found) box.Items.Add(new { item.Name, item.Label });
        box.SelectedValue = _settings.VideoEncoder;
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
        hint.Text = found[0].IsHardware
            ? "Your graphics card can encode video, which is several times quicker than the processor at the same settings. Switch to the CPU if a file ever comes out wrong."
            : "No hardware encoder was found on this machine, so exports use the processor.";
    }

    private void PrepareGraphicsForExport()
    {
        Directory.CreateDirectory(_cache);
        foreach (var graphic in _composition.Layers.SelectMany(layer => layer.Graphics))
        {
            if (graphic.Kind == GraphicsOverlayKind.Image) { graphic.RenderedImagePath = graphic.ImagePath; continue; }
            if (graphic.Kind == GraphicsOverlayKind.Lottie) { graphic.RenderedImagePath = RenderLottieSequence(graphic); continue; }
            var pixelWidth = Math.Clamp((int)Math.Round(graphic.Width * 1920), 32, 1920); var pixelHeight = Math.Clamp((int)Math.Round(graphic.Height * 1080), 20, 1080);
            var path = System.IO.Path.Combine(_cache, $"overlay-{graphic.Id:N}-{pixelWidth}x{pixelHeight}.png");
            var visual = graphic.Kind is GraphicsOverlayKind.SolidColor or GraphicsOverlayKind.Gradient
                ? new Border { Width = pixelWidth, Height = pixelHeight, Background = GraphicFillBrush(graphic) }
                : new Border { Width = pixelWidth, Height = pixelHeight, Background = GraphicBrush(graphic.Background, Brushes.Transparent), Child = new TextBlock { Text = graphic.Text, FontFamily = new FontFamily(graphic.FontFamily), FontSize = graphic.FontSize, Foreground = GraphicBrush(graphic.Foreground, Brushes.White), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            visual.Measure(new Size(pixelWidth, pixelHeight)); visual.Arrange(new Rect(0, 0, pixelWidth, pixelHeight)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); graphic.RenderedImagePath = path;
        }
    }

    private Task ExportPlacementAsync(TimelinePlacement placement)
    {
        var gif = _mode == ExportMode.Gif;
        var extension = gif ? "gif" : VideoExtension; var dialog = new SaveFileDialog { FileName = DefaultName(placement.Clip, gif), Filter = gif ? "Animated GIF|*.gif" : VideoFilter(extension), DefaultExt = extension, AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var isolated = new TimelineComposition { WorkspaceDuration = placement.Duration };
        foreach (var effect in _composition.OutputEffects) isolated.OutputEffects.Add(effect.Clone());
        var layer = new TimelineLayer { Name = "Exported Clip" }; isolated.Layers.Add(layer);
        var copy = TimelineOperations.AddPlacement(layer, CloneClip(placement.Clip), TimeSpan.Zero); copy.InPoint = placement.InPoint; copy.Length = placement.Duration; foreach (var keyframe in placement.Keyframes) copy.Keyframes.Add(new AnimationKeyframe { Property = keyframe.Property, Offset = keyframe.Offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation });
        var options = GetOptions(); var output = dialog.FileName;
        StartExportJob($"Export {placement.Clip.DisplayName}", output, (progress, token) => _engine.ExportCompositionAsync(isolated, output, options, progress, token), job => FinishExportedFile(job, options.CopyToClipboard));
        return Task.CompletedTask;
    }

    private Task ExportPlacementAsGifAsync(TimelinePlacement placement, TimeSpan duration)
    {
        if (!placement.Clip.Media.HasVideo || duration <= TimeSpan.Zero) return Task.CompletedTask;
        var dialog = new SaveFileDialog { FileName = DefaultName(placement.Clip, true), Filter = "Animated GIF|*.gif", DefaultExt = "gif", AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var isolated = new TimelineComposition { WorkspaceDuration = duration };
        foreach (var effect in _composition.OutputEffects) isolated.OutputEffects.Add(effect.Clone());
        var layer = new TimelineLayer { Name = "Quick GIF", Kind = TimelineLayerKind.Video }; isolated.Layers.Add(layer);
        var copy = TimelineOperations.AddPlacement(layer, CloneClip(placement.Clip), TimeSpan.Zero); copy.InPoint = placement.InPoint; copy.Length = duration; foreach (var keyframe in placement.Keyframes.Where(item => item.Offset <= duration)) copy.Keyframes.Add(new AnimationKeyframe { Property = keyframe.Property, Offset = keyframe.Offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation });
        var options = GetOptions() with { Mode = ExportMode.Gif, Speed = 1 }; var output = dialog.FileName;
        StartExportJob($"GIF ({duration.TotalSeconds:0.##}s)", output, (progress, token) => _engine.ExportCompositionAsync(isolated, output, options, progress, token), job => FinishExportedFile(job, options.CopyToClipboard));
        return Task.CompletedTask;
    }

    private Task BatchExportAsync()
    {
        if (_clips.Count == 0) return Task.CompletedTask;
        var dialog = new OpenFolderDialog { Title = "Choose folder for exported clips", InitialDirectory = _settings.DefaultOutputFolder }; if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var gif = _mode == ExportMode.Gif; var options = GetClipOptions(); var folder = dialog.FolderName;
        var items = _clips.Select(clip => (Clip: CloneClip(clip), Output: System.IO.Path.Combine(folder, DefaultName(clip, gif)))).ToArray();
        var copyToClipboard = _settings.AutoCopyExports;
        StartExportJob($"Batch export ({items.Length} clips)", folder, async (progress, token) =>
        {
            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var relay = new Progress<ExportProgress>(value => progress.Report(new((index + value.Fraction) / items.Length, $"Clip {index + 1} of {items.Length}: {value.Stage}")));
                await _engine.ExportAsync(items[index].Clip, items[index].Output, options, relay, token);
            }
        }, _ =>
        {
            if (!copyToClipboard) return;
            var written = new StringCollection(); foreach (var item in items) if (File.Exists(item.Output)) written.Add(item.Output);
            if (written.Count == 0) return; var data = new DataObject(); data.SetFileDropList(written); Clipboard.SetDataObject(data, true);
        });
        return Task.CompletedTask;
    }

    private Task StitchAsync()
    {
        if (_clips.Count == 0) return Task.CompletedTask;
        var extension = VideoExtension; var dialog = new SaveFileDialog { FileName = $"Post_Video_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}", Filter = VideoFilter(extension), DefaultExt = extension, AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder }; if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        var options = GetClipOptions(); var output = dialog.FileName; var clips = _clips.Select(CloneClip).ToArray(); var copyToClipboard = _settings.AutoCopyExports;
        StartExportJob($"Montage ({clips.Length} clips)", output, (progress, token) => _engine.StitchAsync(clips, output, options, progress, token), job => FinishExportedFile(job, copyToClipboard));
        return Task.CompletedTask;
    }

    private static void CopyFile(string path) { var files = new StringCollection { path }; var data = new DataObject(); data.SetFileDropList(files); Clipboard.SetDataObject(data, true); }
    private async Task SnapshotAsync()
    {
        var active = ResolveVideoFrameAt(_sequencePosition);
        if (active is null) { MessageBox.Show(this, "There is no visible video under the caret to capture.", "Screenshot", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var clip = active.Value.Placement.Clip; var sourceTime = active.Value.SourcePosition.SourceTime;
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(clip.SourcePath)!, "Post Screenshots");
        var output = System.IO.Path.Combine(folder, $"{System.IO.Path.GetFileNameWithoutExtension(clip.SourcePath)}_{_sequencePosition:hh\\-mm\\-ss\\-fff}.png");
        await RunBusyAsync("Capturing frame…", token => _engine.CaptureFrameAsync(clip.SourcePath, sourceTime, output, token));
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(output); image.EndInit(); Clipboard.SetImage(image); MessageBox.Show(this, $"Frame saved and copied. Paste it with Ctrl+V.\n{output}", "PRT", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RunBusyAsync(string text, Func<CancellationToken, Task> operation)
    {
        _work = new(); BusyText.Text = text; BusyProgress.Value = 0; BusyOverlay.Visibility = Visibility.Visible;
        try { await operation(_work.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Post", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BusyOverlay.Visibility = Visibility.Collapsed; _work.Dispose(); _work = null; }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && !(Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.Z) { if (shift) Redo(); else Undo(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { SaveProjectAs_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { SaveProject_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Enter) { _ = ExportCurrentAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.N) { NewProject_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { OpenProject_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { CutSelectionToClipboard(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { PasteSelection(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteSelection(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.OemComma) { Settings_Click(sender, e); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Space: if (shift) ToggleLoop(); else _ = PlayProjectAsync(); break;
            case Key.I: case Key.OemOpenBrackets: SetIn(); break;
            case Key.O: case Key.OemCloseBrackets: SetOut(); break;
            case Key.C: case Key.V: SplitSelectedPlacement(); break; case Key.X: RemoveSelectedPlacement(); break;
            case Key.P: _ = SnapshotAsync(); break;
            case Key.Left: SetEditPosition(_editPosition - (shift ? TimeSpan.FromSeconds(5) : FrameDuration)); break;
            case Key.Right: SetEditPosition(_editPosition + (shift ? TimeSpan.FromSeconds(5) : FrameDuration)); break;
            case Key.Home: SetEditPosition(TimeSpan.Zero); break; case Key.End: SetEditPosition(_composition.OutputDuration); break;
            case Key.L: ToggleLoop(); break; case Key.R: case Key.Escape: ResetMarkers(); break; case Key.M: ToggleMute(); break; default: return;
        }
        e.Handled = true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Cut_Click(object sender, RoutedEventArgs e) => CutSelectionToClipboard();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void Paste_Click(object sender, RoutedEventArgs e) => PasteSelection();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private async void ExportAudio_Click(object sender, RoutedEventArgs e) => await ExportAudioAsync();
    private void MarkerText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Workspace_Click(sender, e);

    private void CopySelection()
    {
        var placement = _selectedPlacementId is { } placementId ? _composition.Layers.SelectMany(layer => layer.Placements.Select(item => (Layer: layer, Item: item))).FirstOrDefault(item => item.Item.Id == placementId) : default;
        if (placement.Item is not null)
        {
            _placementClipboard = new(placement.Item.Clip, placement.Layer.Kind, placement.Item.InPoint, placement.Item.Duration, placement.Item.Keyframes.Select(ToState).ToArray()); _graphicClipboard = null; return;
        }
        var graphic = _selectedGraphicId is { } graphicId ? _composition.Layers.SelectMany(layer => layer.Graphics).FirstOrDefault(item => item.Id == graphicId) : null;
        if (graphic is not null) { _graphicClipboard = CloneGraphic(graphic); _placementClipboard = null; }
    }

    private void CutSelectionToClipboard() { CopySelection(); DeleteSelection(); }

    private void PasteSelection()
    {
        EnsureProjectHistory();
        if (_placementClipboard is { } placement)
        {
            var layer = _composition.Layers.FirstOrDefault(item => item.Id == _activeLayerId && item.Kind == placement.Kind)
                ?? new TimelineLayer { Name = placement.Kind == TimelineLayerKind.Audio ? $"Audio {_composition.Layers.Count + 1}" : $"Layer {_composition.Layers.Count + 1}", Kind = placement.Kind };
            if (!_composition.Layers.Contains(layer)) _composition.Layers.Add(layer);
            var added = TimelineOperations.AddPlacement(layer, placement.Clip, _editPosition); added.InPoint = placement.InPoint; added.Length = placement.Length; foreach (var keyframe in placement.Keyframes) added.Keyframes.Add(FromState(keyframe));
            _activeLayerId = layer.Id; _selectedPlacementId = added.Id; _selectedGraphicId = null; ExtendWorkspace(added.End);
        }
        else if (_graphicClipboard is not null)
        {
            var copy = CloneGraphic(_graphicClipboard); copy.Start = _editPosition; var layer = CreateGraphicsLayer(copy.Kind); layer.Graphics.Add(copy);
            _activeLayerId = layer.Id; _selectedGraphicId = copy.Id; _selectedPlacementId = null; ExtendWorkspace(copy.End);
        }
        else return;
        InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); DrawCuts();
    }

    private void DeleteSelection()
    {
        if (_selectedPlacementId is not null) { RemoveSelectedPlacement(); return; }
        if (_selectedGraphicId is not { } id) return;
        var layer = _composition.Layers.FirstOrDefault(item => item.Graphics.Any(graphic => graphic.Id == id)); var graphic = layer?.Graphics.FirstOrDefault(item => item.Id == id);
        if (layer is null || graphic is null) return; EnsureProjectHistory(); layer.Graphics.Remove(graphic); if (layer.Graphics.Count == 0 && layer.Placements.Count == 0) _composition.Layers.Remove(layer);
        _selectedGraphicId = null; _activeLayerId = _composition.Layers.FirstOrDefault()?.Id; ResetEmptyComposition(); InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack();
    }

    private void TimelineZoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)) return;
        _timelineZoom = Math.Clamp(zoom, 1, 40); _visualZoomGeneration++; ZoomText.Text = $" {_timelineZoom * 100:0}% • Ctrl+Wheel to zoom"; UpdateTimelineWidths(); _filmstripZoomTimer.Stop(); _filmstripZoomTimer.Start();
    }

    private void MenuLoop_Click(object sender, RoutedEventArgs e) { _loop = (sender as MenuItem)?.IsChecked == true; LoopButton.Background = _loop ? (Brush)FindResource("CyanBrush") : (Brush)FindResource("Panel2Brush"); }
    private void MenuMute_Click(object sender, RoutedEventArgs e) { var desired = (sender as MenuItem)?.IsChecked == true; if (desired != _muted) ToggleMute(); }

    private void ToggleMediaPanel_Click(object sender, RoutedEventArgs e)
    {
        if (FindVisualAncestor<Border>(ProjectMediaList, border => border.Parent is Grid grid && Grid.GetColumn(border) == 0) is not { Parent: Grid host } panel) return;
        var show = (sender as MenuItem)?.IsChecked == true; panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (host.ColumnDefinitions.Count >= 2) { if (!show && host.ColumnDefinitions[0].ActualWidth > 0) _mediaPanelWidth = host.ColumnDefinitions[0].ActualWidth; host.ColumnDefinitions[0].Width = show ? new GridLength(Math.Max(175, _mediaPanelWidth)) : new GridLength(0); host.ColumnDefinitions[1].Width = show ? new GridLength(5) : new GridLength(0); }
    }

    private void ToggleLayersPanel_Click(object sender, RoutedEventArgs e)
    {
        var panel = FindVisualAncestor<Grid>(CompositionScroll, grid => grid.Parent is Grid && Grid.GetRow(grid) == 3); if (panel?.Parent is not Grid host || host.RowDefinitions.Count < 4) return;
        var show = (sender as MenuItem)?.IsChecked == true; if (!show && host.RowDefinitions[3].ActualHeight > 0) _layersPanelHeight = new GridLength(host.RowDefinitions[3].ActualHeight);
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed; host.RowDefinitions[3].Height = show ? _layersPanelHeight : new GridLength(0); host.RowDefinitions[2].Height = show ? new GridLength(5) : new GridLength(0);
    }

    private static T? FindVisualAncestor<T>(DependencyObject start, Func<T, bool> predicate) where T : DependencyObject
    {
        DependencyObject? current = start; while (current is not null) { if (current is T value && predicate(value)) return value; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current); } return null;
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void ToggleLoop() { _loop = !_loop; LoopButton.Background = _loop ? (Brush)FindResource("CyanBrush") : (Brush)FindResource("Panel2Brush"); }
    private void ToggleMute()
    {
        _muted = !_muted; Player.IsMuted = _muted; _audioPreviewPlayer.IsMuted = _muted;
        foreach (var entry in _livePlayers)
        {
            var layer = _composition.Layers.FirstOrDefault(candidate => candidate.Placements.Any(placement => placement.Id == entry.Key));
            entry.Value.IsMuted = _muted || (layer is not null && LayerAudioFullyMuted(layer));
            entry.Value.Balance = layer is null ? 0 : LayerAudioBalance(layer);
        }
        foreach (var entry in _liveAudioPlayers)
        {
            var layer = _composition.Layers.FirstOrDefault(item => item.Placements.Any(placement => placement.Id == entry.Key));
            entry.Value.IsMuted = _muted || (layer is not null && LayerAudioFullyMuted(layer));
            entry.Value.Balance = layer is null ? 0 : LayerAudioBalance(layer);
        }
        MuteButton.Content = _muted ? "🔇" : "🔊";
    }
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _composition.Layers.Count > 0) ApplyTimelineZoom(e); }
    private void TimelineWorkspace_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ApplyTimelineZoom(e); }
    private void ApplyTimelineZoom(MouseWheelEventArgs e)
    {
        var sourceRatio = SourceTimelineScroll.ExtentWidth <= 0 ? 0 : (SourceTimelineScroll.HorizontalOffset + e.GetPosition(SourceTimelineScroll).X) / SourceTimelineScroll.ExtentWidth;
        var compositionRatio = CompositionScroll.ExtentWidth <= 0 ? sourceRatio : (CompositionScroll.HorizontalOffset + e.GetPosition(CompositionScroll).X) / CompositionScroll.ExtentWidth;
        _timelineZoom = Math.Clamp(_timelineZoom * (e.Delta > 0 ? 1.25 : .8), 1, 40); _visualZoomGeneration++; UpdateTimelineWidths(); UpdateLayout();
        SourceTimelineScroll.ScrollToHorizontalOffset(Math.Max(0, sourceRatio * SourceTimelineScroll.ExtentWidth - e.GetPosition(SourceTimelineScroll).X));
        CompositionScroll.ScrollToHorizontalOffset(Math.Max(0, compositionRatio * CompositionScroll.ExtentWidth - e.GetPosition(CompositionScroll).X));
        ZoomText.Text = $" {_timelineZoom * 100:0}% • Ctrl+Wheel to zoom"; _filmstripZoomTimer.Stop(); _filmstripZoomTimer.Start(); e.Handled = true;
    }
    private void Timeline_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var ratio = Math.Clamp(e.GetPosition(TimelineArea).X / Math.Max(1, TimelineArea.ActualWidth), 0, 1);
        SetEditPosition(TimeSpan.FromSeconds(ratio * VisibleDuration.TotalSeconds)); e.Handled = true;
    }
    
    private void Timeline_PreviewMouseMove(object sender, MouseEventArgs e) {
        if (_dragStart.HasValue && _dragSegment != null && e.LeftButton == MouseButtonState.Pressed) {
            if (Math.Abs(e.GetPosition(TimelineArea).X - _dragStart.Value.X) > 5) {
                DragDrop.DoDragDrop(TimelineArea, _dragSegment, DragDropEffects.Move);
                _dragStart = null; _dragSegment = null;
            }
        }
    }
    
    private void Timeline_Drop(object sender, DragEventArgs e) {
        if (e.Data.GetData(typeof(ClipItem)) is ClipItem trayClip)
        {
            var x = Math.Clamp(e.GetPosition(TimelineArea).X / Math.Max(1, TimelineArea.ActualWidth), 0, 1);
            var proposed = TimeSpan.FromSeconds(x * VisibleDuration.TotalSeconds);
            AddMediaAsNewLayer(trayClip, proposed); e.Handled = true; return;
        }
    }
    private void Timeline_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(typeof(ClipItem)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    
    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_timelineUpdating && Timeline.IsMouseCaptureWithin) SetEditPosition(TimeSpan.FromSeconds(e.NewValue)); }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Handled || e.Data.GetDataPresent(LayerDropHandledFormat)) return; e.Handled = true; if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { var projects = files.Where(IsProjectFile).ToArray(); if (projects.Length > 0) await OpenProjectFilesAsync(projects); var media = files.Except(projects, StringComparer.OrdinalIgnoreCase).ToArray(); if (media.Length > 0) await LoadFilesAsync(media); } }
    private async void Open_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Multiselect = true, Filter = "All supported media|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.wmv;*.flv;*.m4v;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.wmv;*.flv;*.m4v|Audio files|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma|Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" }; if (d.ShowDialog(this) == true) await LoadFilesAsync(d.FileNames); }
    private void Player_MediaOpened(object sender, RoutedEventArgs e) { if (_current is not null && _current.Segments.Count > 0) { SeekSequence(_sequencePosition); if (_playWhenMediaOpened) { _playWhenMediaOpened = false; Player.Play(); _playing = true; PlayButton.Content = "❚❚"; } else Player.Pause(); } }
    private void AudioPreviewPlayer_MediaOpened(object? sender, EventArgs e)
    {
        if (!_projectMediaAudioPreviewActive || _current is null || _current.Segments.Count == 0) return;
        SeekSequence(_sequencePosition);
        if (_playWhenMediaOpened) { _playWhenMediaOpened = false; _audioPreviewPlayer.Play(); _playing = true; PlayButton.Content = "❚❚"; }
        else _audioPreviewPlayer.Pause();
    }
    private void Player_MediaEnded(object sender, RoutedEventArgs e) { if (_compositionPreviewActive) { if (_loop) { Player.Position = TimeSpan.Zero; Player.Play(); } else Pause(); } else if (_playing) AdvanceSegmentOrStop(); else Pause(); }
    private async void Play_Click(object sender, RoutedEventArgs e) => await PlayProjectAsync(); private void SetIn_Click(object sender, RoutedEventArgs e) => SetIn(); private void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();
    private void SplitPlacement_Click(object sender, RoutedEventArgs e) => SplitSelectedPlacement();
    private void CutFrom_Click(object sender, RoutedEventArgs e) => CutFrom(); private void CutTo_Click(object sender, RoutedEventArgs e) => CutTo(); private void CutRemove_Click(object sender, RoutedEventArgs e) => CutSelection();
    private void InJump_Click(object sender, RoutedEventArgs e) => SetEditPosition(TimeSpan.Zero); private void OutJump_Click(object sender, RoutedEventArgs e) => SetEditPosition(_composition.OutputDuration);
    private void Back5_Click(object sender, RoutedEventArgs e) => SetEditPosition(_editPosition - TimeSpan.FromSeconds(5)); private void Forward5_Click(object sender, RoutedEventArgs e) => SetEditPosition(_editPosition + TimeSpan.FromSeconds(5)); private void FrameBack_Click(object sender, RoutedEventArgs e) => SetEditPosition(_editPosition - FrameDuration); private void FrameNext_Click(object sender, RoutedEventArgs e) => SetEditPosition(_editPosition + FrameDuration);
    private void Loop_Click(object sender, RoutedEventArgs e) => ToggleLoop(); private void Reset_Click(object sender, RoutedEventArgs e) => ResetMarkers(); private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void PreviewVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Player is not null) Player.Volume = e.NewValue; _audioPreviewPlayer.Volume = e.NewValue; foreach (var player in _livePlayers.Values) player.Volume = e.NewValue; foreach (var player in _liveAudioPlayers.Values) player.Volume = e.NewValue; }
    private void Aspect_Click(object sender, RoutedEventArgs e) { _aspect = Enum.Parse<AspectPreset>((string)((Button)sender).Tag); Crop_ValueChanged(sender, null!); }
    private void AspectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AspectCombo?.SelectedItem is not ComboBoxItem { Tag: string value } || !Enum.TryParse(value, out AspectPreset preset)) return;
        _aspect = preset; Crop_ValueChanged(sender, null!);
    }
    private void Crop_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Player is null || CropZoom is null) return; Player.Stretch = _aspect == AspectPreset.Original && CropZoom.Value == 1 ? Stretch.Uniform : Stretch.UniformToFill; Player.RenderTransformOrigin = new(.5 + PanX.Value * .15, .5 + PanY.Value * .15); Player.RenderTransform = new ScaleTransform(CropZoom.Value, CropZoom.Value); if (_scrubFrameView is not null) { _scrubFrameView.Stretch = Player.Stretch; _scrubFrameView.RenderTransformOrigin = Player.RenderTransformOrigin; _scrubFrameView.RenderTransform = new ScaleTransform(CropZoom.Value, CropZoom.Value); } foreach (var live in _livePlayers.Values) { live.Stretch = Player.Stretch; live.RenderTransformOrigin = Player.RenderTransformOrigin; live.RenderTransform = new ScaleTransform(CropZoom.Value, CropZoom.Value); } }
    private async void Snapshot_Click(object sender, RoutedEventArgs e) => await SnapshotAsync(); private async void Export_Click(object sender, RoutedEventArgs e) => await ExportCurrentAsync(); private async void Batch_Click(object sender, RoutedEventArgs e) => await BatchExportAsync(); private async void Stitch_Click(object sender, RoutedEventArgs e) => await StitchAsync(); private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();

    private TimeSpan VisibleDuration => _composition.DisplayDuration > TimeSpan.Zero ? _composition.DisplayDuration : TimeSpan.FromSeconds(1);

    private static bool IsProjectFile(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".post", StringComparison.OrdinalIgnoreCase) || extension.Equals(".clipedit", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatWholeSeconds(TimeSpan value)
    {
        value = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, value.TotalSeconds)));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void AdvanceSegmentOrStop()
    {
        if (_current is null || _current.Segments.Count == 0) { Pause(); return; }
        if (_activeSegmentIndex + 1 < _current.Segments.Count)
        {
            _activeSegmentIndex++; var next = _current.Segments[_activeSegmentIndex]; _selectedSegmentId = next.Id;
            _position = next.SourceStart; _sequencePosition = TimelineOperations.SourceToSequence(_current.Segments, _activeSegmentIndex, _position);
            _ignorePlayerPositionUntil = DateTime.UtcNow.AddMilliseconds(100); if (_projectMediaAudioPreviewActive) { _audioPreviewPlayer.Position = _position; if (_playing) _audioPreviewPlayer.Play(); } else { Player.Position = _position; if (_playing) Player.Play(); }
        }
        else if (_loop) SeekSequence(TimeSpan.Zero);
        else { _sequencePosition = _current.SelectedDuration; _position = _current.Segments[^1].SourceEnd; Pause(); }
    }

    private void ReanchorAfterEdit()
    {
        if (_current is null || _current.Segments.Count == 0) return;
        var index = _selectedSegmentId is { } id ? _current.Segments.ToList().FindIndex(s => s.Id == id) : -1;
        _activeSegmentIndex = index >= 0 ? index : Math.Clamp(_activeSegmentIndex, 0, _current.Segments.Count - 1);
        var segment = _current.Segments[_activeSegmentIndex]; _selectedSegmentId = segment.Id;
        _position = ClampTime(_position, segment.SourceStart, segment.SourceEnd);
        _sequencePosition = TimelineOperations.SourceToSequence(_current.Segments, _activeSegmentIndex, _position);
        _ignorePlayerPositionUntil = DateTime.UtcNow.AddMilliseconds(100); if (_projectMediaAudioPreviewActive) _audioPreviewPlayer.Position = _position; else Player.Position = _position; UpdateUi();
    }

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min) max = min; return value < min ? min : value > max ? max : value;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var folder = new TextBox { Text = _settings.DefaultOutputFolder, Margin = new Thickness(0, 6, 0, 12) }; var copy = new CheckBox { Content = "Automatically copy exports to clipboard", IsChecked = _settings.AutoCopyExports, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 6) }; var updates = new CheckBox { Content = "Check GitHub Releases for updates", IsChecked = _settings.CheckForUpdates, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 8) }; var save = new Button { Content = "Save Settings", IsDefault = true, Width = 130, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(22) }; panel.Children.Add(new TextBlock { Text = "Default export folder", Foreground = Brushes.LightGray }); panel.Children.Add(folder); panel.Children.Add(copy); panel.Children.Add(updates); panel.Children.Add(new TextBlock { Text = "Updates come from the official Post GitHub Releases page.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10) }); panel.Children.Add(new TextBlock { Text = "Explorer menu: Quick Edit with Post is registered for all supported formats.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap }); panel.Children.Add(save);
        var window = new Window { Title = "Post Settings", Width = 500, Height = 330, Content = panel, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize }; save.Click += (_, _) => { _settings = _settings with { DefaultOutputFolder = folder.Text, AutoCopyExports = copy.IsChecked == true, CheckForUpdates = updates.IsChecked == true, PreviewVolume = PreviewVolume.Value }; _settings.Save(); window.DialogResult = true; }; window.ShowDialog();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Checking for updates…", async token => { var service = new UpdateService(); var update = await service.CheckAsync(UpdateService.UpdateEndpoint, token); if (update is null) { MessageBox.Show(this, "You already have the latest release.", "Post Updater"); return; } if (MessageBox.Show(this, $"{update.Name} is available. Download and install it now?", "Post Updater", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var path = await service.DownloadAsync(update, new Progress<double>(p => BusyProgress.Value = p), token); UpdateService.LaunchInstaller(path); Application.Current.Shutdown(); });
    }
}

