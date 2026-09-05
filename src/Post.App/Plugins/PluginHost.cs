using Post.Core;
using Post.Plugins;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Post.App.Plugins;

/// <summary>What a plugin asked to appear on a clip's right-click menu.</summary>
internal sealed record ClipCommand(string Header, Func<ClipContext, bool> AppliesTo, Func<ClipContext, Task> Invoke);

/// <summary>What a plugin asked to appear on an overlay's right-click menu.</summary>
internal sealed record TextCommand(string Header, Func<TextContext, bool> AppliesTo, Func<TextContext, Task> Invoke);

/// <summary>What a plugin asked to appear when several things are selected.</summary>
internal sealed record SelectionCommand(string Header, Func<SelectionContext, bool> AppliesTo, Func<SelectionContext, Task> Invoke);

/// <summary>What a plugin asked to appear under Tools.</summary>
internal sealed record ToolsCommand(string Header, Func<Task> Invoke);

/// <summary>What a plugin asked to appear under Tools ▸ Windows: something that opens.</summary>
internal sealed record WindowCommand(string Header, Func<Task> Invoke);

/// <summary>
/// The host handed to each plugin. It is the only way in: a plugin never touches Post's
/// own objects, only the snapshots and the operations below.
/// </summary>
internal sealed class PluginHost(MainWindow window, string pluginId) : IPostHost, IPostMenus, IPostStorage, IPostMedia
{
    private readonly List<ClipCommand> _clipCommands = [];
    private readonly List<TextCommand> _textCommands = [];
    private readonly List<SelectionCommand> _selectionCommands = [];
    private readonly List<ToolsCommand> _toolsCommands = [];
    private readonly List<WindowCommand> _windowCommands = [];

    public IReadOnlyList<ClipCommand> ClipCommands => _clipCommands;
    public IReadOnlyList<TextCommand> TextCommands => _textCommands;
    public IReadOnlyList<SelectionCommand> SelectionCommands => _selectionCommands;
    public IReadOnlyList<ToolsCommand> ToolsCommands => _toolsCommands;
    public IReadOnlyList<WindowCommand> WindowCommands => _windowCommands;

    public IPostMenus Menus => this;
    public IPostStorage Storage => this;
    public IPostTimeline Timeline { get; } = new PluginTimeline(window);
    public IPostMedia Media => this;
    public Window Window => window;

    // ---- media --------------------------------------------------------------

    /// <summary>
    /// Decodes into the plugin's own folder, using Post's ffmpeg. The plugin never learns
    /// where that is or how it is called: it asks for audio and gets a file.
    /// </summary>
    public async Task<string> ExtractAudioAsync(
        string sourcePath, TimeSpan start, TimeSpan duration, int sampleRate, int channels,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("That media file is not there.", sourcePath);

        var target = Path.Combine(Folder, $"audio-{Guid.NewGuid():N}.wav");
        progress?.Report(.05);
        await window.ExtractAudioForPluginAsync(sourcePath, start, duration, sampleRate, channels, target, token);
        progress?.Report(1);
        return target;
    }

    public void Report(string message) => window.ReportFromPlugin(message);

    public Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<double>, CancellationToken, Task<T>> work)
        => window.RunPluginWorkAsync(title, work);

    // ---- menus --------------------------------------------------------------

    public void AddClipCommand(string header, Func<ClipContext, bool> appliesTo, Func<ClipContext, Task> invoke)
        => _clipCommands.Add(new ClipCommand(header, appliesTo, invoke));

    public void AddTextCommand(string header, Func<TextContext, bool> appliesTo, Func<TextContext, Task> invoke)
        => _textCommands.Add(new TextCommand(header, appliesTo, invoke));

    public void AddSelectionCommand(string header, Func<SelectionContext, bool> appliesTo, Func<SelectionContext, Task> invoke)
        => _selectionCommands.Add(new SelectionCommand(header, appliesTo, invoke));

    public void AddToolsCommand(string header, Func<Task> invoke)
        => _toolsCommands.Add(new ToolsCommand(header, invoke));

    public void AddWindowCommand(string header, Func<Task> invoke)
        => _windowCommands.Add(new WindowCommand(header, invoke));

    // ---- storage ------------------------------------------------------------

    public string Folder
    {
        get
        {
            var folder = Path.Combine(Core.Plugins.PluginStore.FolderFor(pluginId), "data");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private string SettingsPath => Path.Combine(Folder, "settings.json");

    public T? Get<T>(string key)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return default;
            var all = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath));
            return all is not null && all.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;
        }
        catch { return default; }
    }

    public void Set<T>(string key, T value)
    {
        try
        {
            var all = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath)) ?? []
                : [];
            all[key] = JsonSerializer.SerializeToElement(value);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>
/// The timeline as a plugin sees it: snapshots out, operations in, and every operation
/// landing on the same undo stack as a person's own edits.
/// </summary>
internal sealed class PluginTimeline(MainWindow window) : IPostTimeline
{
    public TimelineSnapshot Read() => window.ReadTimelineForPlugin();

    public Guid AddAudio(string filePath, TimeSpan start, Guid? layerId = null, string? layerName = null)
        => window.AddPluginMedia(filePath, start, layerId, layerName, audio: true);

    public Guid AddClip(string filePath, TimeSpan start, Guid? layerId = null, string? layerName = null)
        => window.AddPluginMedia(filePath, start, layerId, layerName, audio: false);

    public Guid AddTextOverlay(string text, TimeSpan start, TimeSpan duration, Guid? layerId = null)
        => window.AddPluginTextOverlay(text, start, duration, layerId);

    public Guid AddCaptionLayer(string name) => window.AddPluginCaptionLayer(name);

    public Guid AddLayer(string name, bool audio) => window.AddPluginLayer(name, audio);

    public bool RemovePlacement(Guid placementId) => window.RemovePluginPlacement(placementId);

    public bool MovePlacement(Guid placementId, TimeSpan start, TimeSpan? duration = null)
        => window.MovePluginPlacement(placementId, start, duration);

    public IDisposable BeginEdit(string description) => window.BeginPluginEdit(description);
}
