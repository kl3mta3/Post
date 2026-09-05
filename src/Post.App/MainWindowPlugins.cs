using Post.App.Plugins;
using Post.Core.Plugins;
using Post.Core;
using Post.Plugins;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Post.App;

/// <summary>
/// What plugins can actually do to the editor. Everything a plugin asks for arrives here,
/// on the UI thread, against the same objects a person's own edits touch — which is why
/// each operation goes through the usual history and refresh rather than around it.
/// </summary>
public partial class MainWindow
{
    private readonly PluginLoader _plugins = new();
    private readonly List<(string PluginName, ClipCommand Command)> _pluginClipCommands = [];
    private readonly List<(string PluginName, TextCommand Command)> _pluginTextCommands = [];

    /// <summary>
    /// Starts the installed plugins. Failures are collected and shown once rather than
    /// stopping startup: a broken plugin should not cost someone their editor.
    /// </summary>
    private void StartPlugins()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
        // A host each, so one plugin's settings and folder are not another's.
        _plugins.LoadAll(manifest => _pluginHosts[manifest.Id] = new PluginHost(this, manifest.Id), version);

        foreach (var plugin in _plugins.Loaded) RegisterCommands(plugin);

        if (_plugins.Failures.Count > 0)
            Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                "Some plugins did not start:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, _plugins.Failures),
                "Plugins", MessageBoxButton.OK, MessageBoxImage.Warning), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private readonly Dictionary<string, PluginHost> _pluginHosts = [];

    /// <summary>Puts what a plugin registered onto the menus it asked for.</summary>
    private void RegisterCommands(LoadedPlugin plugin)
    {
        if (!_pluginHosts.TryGetValue(plugin.Manifest.Id, out var host)) return;
        foreach (var command in host.ClipCommands) _pluginClipCommands.Add((plugin.Manifest.Name, command));
        foreach (var command in host.TextCommands) _pluginTextCommands.Add((plugin.Manifest.Name, command));
        foreach (var command in host.ToolsCommands) AddPluginToolsCommand(plugin.Manifest.Name, command);
    }

    /// <summary>
    /// Starts a plugin that was just installed, rather than making someone close Post to
    /// use the thing they have only now chosen. Right-click menus are built each time they
    /// open, so they pick this up at once; Tools is added to here.
    ///
    /// Returns false when it could not be started now — an update to a plugin already
    /// loaded, most often, since an assembly cannot be replaced underneath itself.
    /// </summary>
    internal bool StartPluginNow(PluginManifest manifest)
    {
        try
        {
            var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
            var host = _pluginHosts[manifest.Id] = new PluginHost(this, manifest.Id);
            if (_plugins.LoadNow(manifest, host, version) is not { } loaded) return false;
            RegisterCommands(loaded);
            return true;
        }
        catch
        {
            // It is installed either way; a restart will try again and report properly.
            return false;
        }
    }

    /// <summary>Adds a plugin's own entry under Tools, below the Plugin Manager.</summary>
    private void AddPluginToolsCommand(string pluginName, ToolsCommand command)
    {
        if (ToolsMenu is null) return;
        var item = new MenuItem { Header = command.Header, ToolTip = $"From the {pluginName} plugin" };
        item.Click += async (_, _) => await RunPluginCommandAsync(pluginName, command.Invoke);
        ToolsMenu.Items.Insert(ToolsMenu.Items.Count, item);
    }

    /// <summary>Adds whatever plugins offer for this clip to its right-click menu.</summary>
    private void AddPluginClipCommands(ContextMenu menu, TimelineLayer layer, TimelinePlacement placement)
    {
        if (_pluginClipCommands.Count == 0) return;
        var context = new ClipContext(placement.Id, layer.Id, placement.Clip.SourcePath, placement.Start,
            placement.Duration, placement.Clip.Media.HasAudio, placement.Clip.Media.HasVideo, placement.InPoint);

        var offered = _pluginClipCommands.Where(item => Safe(item.Command.AppliesTo, context)).ToArray();
        if (offered.Length == 0) return;

        menu.Items.Add(new Separator());
        foreach (var (pluginName, command) in offered)
        {
            var item = new MenuItem { Header = command.Header, ToolTip = $"From the {pluginName} plugin" };
            item.Click += async (_, _) => await RunPluginCommandAsync(pluginName, () => command.Invoke(context));
            menu.Items.Add(item);
        }
    }

    /// <summary>Adds whatever plugins offer for this overlay to its right-click menu.</summary>
    private void AddPluginTextCommands(ContextMenu menu, TimelineLayer layer, GraphicsOverlay graphic)
    {
        if (_pluginTextCommands.Count == 0) return;
        var context = new TextContext(graphic.Id, layer.Id, graphic.Text ?? "", graphic.Start, graphic.Duration);

        var offered = _pluginTextCommands.Where(item => Safe(item.Command.AppliesTo, context)).ToArray();
        if (offered.Length == 0) return;

        menu.Items.Add(new Separator());
        foreach (var (pluginName, command) in offered)
        {
            var item = new MenuItem { Header = command.Header, ToolTip = $"From the {pluginName} plugin" };
            item.Click += async (_, _) => await RunPluginCommandAsync(pluginName, () => command.Invoke(context));
            menu.Items.Add(item);
        }
    }

    private static bool Safe(Func<ClipContext, bool> predicate, ClipContext context)
    {
        try { return predicate(context); } catch { return false; }
    }

    private static bool Safe(Func<TextContext, bool> predicate, TextContext context)
    {
        try { return predicate(context); } catch { return false; }
    }

    /// <summary>A plugin throwing is reported, not left to take the app down.</summary>
    private async Task RunPluginCommandAsync(string pluginName, Func<Task> work)
    {
        try { await work(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"{pluginName} did not finish:{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Plugins", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- what plugins are allowed to do -------------------------------------

    internal void ReportFromPlugin(string message)
    {
        // Shown where Post already talks, rather than in a message box of the plugin's own.
        if (BusyText is not null) BusyText.Text = message;
        if (CompositionStatus is not null) CompositionStatus.ToolTip = message;
    }

    internal async Task<T> RunPluginWorkAsync<T>(string title, Func<IProgress<double>, CancellationToken, Task<T>> work)
    {
        var result = default(T)!;
        await RunBusyAsync(title, async token =>
        {
            var progress = new Progress<double>(value => BusyProgress.Value = Math.Clamp(value, 0, 1));
            result = await work(progress, token);
        });
        return result;
    }

    /// <summary>Post's own ffmpeg, lent to a plugin that needs to hear a clip.</summary>
    internal Task ExtractAudioForPluginAsync(
        string sourcePath, TimeSpan start, TimeSpan duration, int sampleRate, int channels, string target,
        CancellationToken token)
        => _engine.ExtractAudioAsync(sourcePath, start, duration, sampleRate, channels, target, token);

    internal TimelineSnapshot ReadTimelineForPlugin() => new(
        [.. _composition.Layers.Select(layer => new LayerSnapshot(
            layer.Id, layer.Name, layer.Kind == TimelineLayerKind.Audio,
            [.. layer.Placements.Select(item => new PlacementSnapshot(item.Id, item.Clip.SourcePath, item.Start, item.Duration))]))],
        _composition.DisplayDuration);

    internal Guid AddPluginLayer(string name, bool audio)
    {
        var layer = new TimelineLayer { Name = name, Kind = audio ? TimelineLayerKind.Audio : TimelineLayerKind.Video };
        _composition.Layers.Insert(0, layer);
        return layer.Id;
    }

    /// <summary>
    /// Puts a file a plugin produced onto the timeline. It is probed like anything else,
    /// so a plugin cannot smuggle in something Post cannot actually play.
    /// </summary>
    internal Guid AddPluginMedia(string filePath, TimeSpan start, Guid? layerId, string? layerName, bool audio)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The plugin gave a file that is not there.", filePath);

        // Probed off the UI thread deliberately. ProbeAsync resumes on whatever context it
        // was started from, so waiting on it here from the UI thread would be waiting for
        // work that can only run on the thread doing the waiting — a deadlock the whole
        // window never comes back from. Task.Run leaves that context behind.
        var media = Task.Run(() => _probe.ProbeAsync(filePath)).GetAwaiter().GetResult();
        var clip = new ClipItem { SourcePath = filePath, Media = media };
        clip.Segments.Add(new MediaSegment { SourceStart = TimeSpan.Zero, SourceEnd = media.Duration });
        _clips.Add(clip);
        _histories[clip] = new UndoHistory<ClipSnapshot>(30);
        _histories[clip].Push(clip.Snapshot());

        var layer = FindLayer(layerId) ?? _composition.Layers.FirstOrDefault(item =>
                        item.Kind == (audio ? TimelineLayerKind.Audio : TimelineLayerKind.Video) && item.Name == layerName)
                    ?? NewLayerFor(layerName ?? (audio ? "Audio" : "Video"), audio);

        var placement = new TimelinePlacement { Clip = clip, Start = start, InPoint = TimeSpan.Zero, Length = media.Duration };
        layer.Placements.Add(placement);
        ExtendWorkspace(placement.End);
        return placement.Id;
    }

    /// <summary>One layer for many captions, named so it is obvious where they came from.</summary>
    internal Guid AddPluginCaptionLayer(string name)
    {
        var layer = CreateGraphicsLayer(GraphicsOverlayKind.Text);
        if (!string.IsNullOrWhiteSpace(name)) layer.Name = name;
        return layer.Id;
    }

    internal Guid AddPluginTextOverlay(string text, TimeSpan start, TimeSpan duration, Guid? layerId)
    {
        var layer = FindLayer(layerId) ?? CreateGraphicsLayer(GraphicsOverlayKind.Text);
        var graphic = new GraphicsOverlay
        {
            Kind = GraphicsOverlayKind.Text, Text = text, Start = start, Duration = duration,
            X = .1, Y = .78, Width = .8, Height = .15,
        };
        layer.Graphics.Add(graphic);
        ExtendWorkspace(graphic.End);
        return graphic.Id;
    }

    internal bool RemovePluginPlacement(Guid placementId)
    {
        foreach (var layer in _composition.Layers)
            if (layer.Placements.FirstOrDefault(item => item.Id == placementId) is { } placement)
                return layer.Placements.Remove(placement);
        return false;
    }

    internal bool MovePluginPlacement(Guid placementId, TimeSpan start, TimeSpan? duration)
    {
        foreach (var layer in _composition.Layers)
            if (layer.Placements.FirstOrDefault(item => item.Id == placementId) is { } placement)
            {
                placement.Start = start < TimeSpan.Zero ? TimeSpan.Zero : start;
                if (duration is { } value && value > TimeSpan.Zero) placement.Length = value;
                ExtendWorkspace(placement.End);
                return true;
            }
        return false;
    }

    private TimelineLayer? FindLayer(Guid? id) => id is { } value ? _composition.Layers.FirstOrDefault(item => item.Id == value) : null;

    private TimelineLayer NewLayerFor(string name, bool audio)
    {
        var layer = new TimelineLayer { Name = name, Kind = audio ? TimelineLayerKind.Audio : TimelineLayerKind.Video };
        _composition.Layers.Insert(0, layer);
        return layer;
    }

    /// <summary>
    /// Everything a plugin does between this and disposing lands as one undo step, and
    /// the editor is refreshed once at the end rather than after every call.
    /// </summary>
    internal IDisposable BeginPluginEdit(string description)
    {
        EnsureProjectHistory();
        return new PluginEdit(this);
    }

    private sealed class PluginEdit(MainWindow window) : IDisposable
    {
        public void Dispose()
        {
            window.CommitProjectEdit();
            window.InvalidateCompositionPreview();
            window.RefreshTray();
            window.RefreshLayerStack();
            window.DrawCuts();
        }
    }
}
