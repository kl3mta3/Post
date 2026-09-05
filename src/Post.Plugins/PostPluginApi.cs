using System.Windows;

namespace Post.Plugins;

/// <summary>
/// What a Post plugin implements. One class per plugin; Post finds it by looking for this
/// interface in the assembly the manifest names.
/// </summary>
public interface IPostPlugin
{
    /// <summary>Matches the id in plugin.json, and the folder it installed into.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Called once as Post starts. Everything the plugin contributes is registered here.</summary>
    void Initialize(IPostHost host);

    /// <summary>Called as Post closes.</summary>
    void Shutdown() { }
}

/// <summary>
/// A plugin whose files are not the whole of it — a model to fetch, a voice set, anything
/// too large to keep in the repository. Post runs this while installing, so one install is
/// one wait rather than a surprise the first time the plugin is used.
///
/// It runs before the plugin has been initialized and without a host: nothing but a folder
/// to write into. Downloading is all it should do.
/// </summary>
public interface IPostPluginSetup
{
    /// <summary>What is being fetched, shown before it starts. "voices and model", say.</summary>
    string SetupDescription { get; }

    /// <summary>
    /// Fetches whatever is missing into the plugin's own data folder. Called again on a
    /// reinstall, so it should do nothing when everything is already there.
    ///
    /// Report as it goes: the stage is what a person reads while waiting, and a plugin
    /// knows what it is doing where Post can only guess.
    /// </summary>
    Task PrepareAsync(string dataFolder, IProgress<PluginSetupProgress> progress, CancellationToken token);
}

/// <summary>How far a plugin's setup has got, and what it is doing.</summary>
/// <param name="Fraction">0 to 1 across the whole of this plugin's setup.</param>
/// <param name="Stage">Short and readable — "Fetching voices", "Fetching the model".</param>
public readonly record struct PluginSetupProgress(double Fraction, string Stage);

/// <summary>Everything a plugin is allowed to reach.</summary>
public interface IPostHost
{
    /// <summary>What the plugin may add to the menus.</summary>
    IPostMenus Menus { get; }

    /// <summary>Reading and changing the timeline.</summary>
    IPostTimeline Timeline { get; }

    /// <summary>A folder of the plugin's own, and its settings.</summary>
    IPostStorage Storage { get; }

    /// <summary>Getting at what a clip actually contains, rather than only where it lives.</summary>
    IPostMedia Media { get; }

    /// <summary>The window to parent a dialog to, so it centres and blocks correctly.</summary>
    Window Window { get; }

    /// <summary>Says something in Post's own status line rather than in a message box.</summary>
    void Report(string message);

    /// <summary>
    /// Runs slow work behind Post's own progress overlay, so a plugin does not have to
    /// invent one and the app stays responsive while it works.
    /// </summary>
    Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<double>, CancellationToken, Task<T>> work);
}

/// <summary>
/// Reading what is inside a clip. Post already carries the tools to decode any container
/// it can play; a plugin that wants to listen to audio should not have to ship its own.
/// </summary>
public interface IPostMedia
{
    /// <summary>
    /// Decodes part of a media file to a plain WAV in the plugin's own folder, at the
    /// sample rate and channel count asked for — 16000 and 1 for most speech models.
    /// Pass <see cref="TimeSpan.Zero"/> as the duration for all of it.
    /// </summary>
    Task<string> ExtractAudioAsync(
        string sourcePath, TimeSpan start, TimeSpan duration, int sampleRate, int channels,
        IProgress<double>? progress = null, CancellationToken token = default);
}

/// <summary>Menu entries a plugin contributes.</summary>
public interface IPostMenus
{
    /// <summary>
    /// Adds an entry to the right-click menu of a clip on the timeline. The predicate
    /// decides whether it appears at all, so a plugin can ignore what it cannot use.
    /// </summary>
    void AddClipCommand(string header, Func<ClipContext, bool> appliesTo, Func<ClipContext, Task> invoke);

    /// <summary>
    /// Adds an entry to the right-click menu of a text or graphic overlay. Separate from
    /// clips because an overlay carries words rather than a file, and what is worth doing
    /// to one is different.
    /// </summary>
    void AddTextCommand(string header, Func<TextContext, bool> appliesTo, Func<TextContext, Task> invoke);

    /// <summary>
    /// Adds an entry to the right-click menu of a selection of several things. Separate
    /// from the single-clip entry because what is worth doing to five clips at once is
    /// rarely what is worth doing to one — and because a plugin that cannot work on many
    /// should be able to say so by declining, rather than by not being offered.
    ///
    /// The entry is shown whether the predicate accepts or not; declining greys it out.
    /// </summary>
    void AddSelectionCommand(string header, Func<SelectionContext, bool> appliesTo, Func<SelectionContext, Task> invoke);

    /// <summary>
    /// Adds an entry under Tools ▸ Settings, for the plugin's own settings, beside Post's.
    /// </summary>
    void AddToolsCommand(string header, Func<Task> invoke);

    /// <summary>
    /// Adds an entry under Tools ▸ Windows, for a command that opens a window of its own.
    /// That is where the rest of Post's windows are listed, and where someone looks for one.
    /// </summary>
    void AddWindowCommand(string header, Func<Task> invoke);
}

/// <summary>
/// Everything selected when a command was invoked on it, in the order it sits on the
/// timeline. A mixed selection carries both lists; either may be empty.
/// </summary>
/// <param name="Clips">The clips selected, earliest first.</param>
/// <param name="Overlays">The text and graphic overlays selected, earliest first.</param>
public sealed record SelectionContext(
    IReadOnlyList<ClipContext> Clips,
    IReadOnlyList<TextContext> Overlays)
{
    /// <summary>How many things are selected altogether.</summary>
    public int Count => Clips.Count + Overlays.Count;

    /// <summary>Where the earliest of them starts, and where the latest of them ends.</summary>
    public TimeSpan Start => Clips.Select(item => item.Start).Concat(Overlays.Select(item => item.Start))
        .DefaultIfEmpty(TimeSpan.Zero).Min();

    public TimeSpan End => Clips.Select(item => item.Start + item.Duration)
        .Concat(Overlays.Select(item => item.Start + item.Duration))
        .DefaultIfEmpty(TimeSpan.Zero).Max();
}

/// <summary>The clip a command was invoked on.</summary>
/// <param name="SelectionCount">
/// How many things are selected. A plugin decides for itself whether it makes sense on
/// more than one — generating speech for fifteen clips at once does not, colouring them
/// might — so this is offered rather than the host ruling on it.
/// </param>
/// <param name="Start">Where it sits on the timeline.</param>
/// <param name="SourceStart">
/// Where it starts inside its own file. A trimmed clip plays from partway in, and anything
/// reading the source has to start there or everything it finds is offset by the trim.
/// </param>
public sealed record ClipContext(
    Guid PlacementId,
    Guid LayerId,
    string SourcePath,
    TimeSpan Start,
    TimeSpan Duration,
    bool HasAudio,
    bool HasVideo,
    TimeSpan SourceStart = default,
    int SelectionCount = 1);

/// <summary>The overlay a command was invoked on.</summary>
public sealed record TextContext(
    Guid GraphicId,
    Guid LayerId,
    string Text,
    TimeSpan Start,
    TimeSpan Duration,
    int SelectionCount = 1);

/// <summary>Reading the timeline, and changing it.</summary>
public interface IPostTimeline
{
    /// <summary>A snapshot. A plugin never holds a reference into Post's own objects.</summary>
    TimelineSnapshot Read();

    /// <summary>Adds an audio file. A layer is made when none is named.</summary>
    Guid AddAudio(string filePath, TimeSpan start, Guid? layerId = null, string? layerName = null);

    /// <summary>Adds a video or still.</summary>
    Guid AddClip(string filePath, TimeSpan start, Guid? layerId = null, string? layerName = null);

    /// <summary>
    /// Adds a text overlay, for captions and titles. With no layer named a new one is made
    /// for it, which is right for a single title and wrong for fifty captions: make one
    /// layer with <see cref="AddCaptionLayer"/> and pass its id for those.
    /// </summary>
    Guid AddTextOverlay(string text, TimeSpan start, TimeSpan duration, Guid? layerId = null);

    /// <summary>
    /// A layer to put text overlays on, returned so many can share it. Without this a
    /// plugin writing captions leaves one layer per caption.
    /// </summary>
    Guid AddCaptionLayer(string name);

    Guid AddLayer(string name, bool audio);

    /// <summary>Removes a placement. Returns false when it was already gone.</summary>
    bool RemovePlacement(Guid placementId);

    /// <summary>Moves or retimes a placement.</summary>
    bool MovePlacement(Guid placementId, TimeSpan start, TimeSpan? duration = null);

    /// <summary>
    /// Groups everything done inside into one undo step. Without it a plugin that adds
    /// twenty captions leaves twenty steps, and undoing its work becomes a chore.
    /// </summary>
    IDisposable BeginEdit(string description);
}

public sealed record TimelineSnapshot(IReadOnlyList<LayerSnapshot> Layers, TimeSpan Duration);
public sealed record LayerSnapshot(Guid Id, string Name, bool IsAudio, IReadOnlyList<PlacementSnapshot> Placements);
public sealed record PlacementSnapshot(Guid Id, string SourcePath, TimeSpan Start, TimeSpan Duration);

/// <summary>A plugin's own folder and settings.</summary>
public interface IPostStorage
{
    /// <summary>Somewhere to keep models, caches and anything the plugin renders.</summary>
    string Folder { get; }

    T? Get<T>(string key);
    void Set<T>(string key, T value);
}
