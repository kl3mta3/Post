using System.Text.Json.Serialization;

namespace Post.Core.Plugins;

/// <summary>
/// What a plugin folder says about itself, in its plugin.json. One of these sits in each
/// folder of the plugins repository, and a copy is kept beside a plugin once installed so
/// Post knows what version is on disk.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>A stable name, used as the folder it installs into.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";

    /// <summary>The assembly Post loads out of the folder.</summary>
    public string Entry { get; init; } = "";

    /// <summary>Refused below this version of Post, rather than failing at first click.</summary>
    public string? MinimumPostVersion { get; init; }

    /// <summary>
    /// The folder in the plugins repository holding this plugin's files. Left out, the id
    /// is used, which is how the repository is laid out.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>
    /// Where this plugin's files are fetched from, without the file name. Filled in from
    /// the repository when the index does not say, so an index need only list names.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The files that make up the plugin, by name, relative to its folder. This is how the
    /// repository holds a plugin: loose files, no archive and no release.
    /// </summary>
    public string[] Files { get; init; } = [];

    /// <summary>An archive to fetch instead, for a plugin published as one file.</summary>
    public string Download { get; init; } = "";

    /// <summary>
    /// One checksum for the whole plugin, and it is required. For an archive it is the
    /// archive's, checked before anything is unpacked; for loose files it covers all of
    /// them together, checked once they are down and before they are kept.
    ///
    /// It proves what arrived is what was published. It says nothing about whether the
    /// code inside is safe — that is what review on the repository is for.
    /// </summary>
    public string Sha256 { get; init; } = "";

    /// <summary>What the plugin says it needs, shown before installing.</summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// What this plugin fetches for itself when it installs — a model, a voice pack — named
    /// so it can be said out loud and given its share of the progress before a single file
    /// is downloaded. Left out by a plugin that is only its files.
    ///
    /// The plugin is asked directly once its files are down, so this being absent costs the
    /// split rather than the step: an undeclared setup still runs, just without warning.
    /// </summary>
    public string? Setup { get; init; }

    /// <summary>Where in the repository this came from, for showing the source.</summary>
    [JsonIgnore] public string? SourceUrl { get; init; }

    /// <summary>
    /// Held as loose files in its own folder, with one checksum covering all of them. The
    /// checksum is required: one for the whole plugin is little to publish and is what
    /// says the download is the thing that was reviewed.
    /// </summary>
    public bool HasFiles => Files.Length > 0
        && Array.TrueForAll(Files, file => !string.IsNullOrWhiteSpace(file))
        && !string.IsNullOrWhiteSpace(Sha256);

    /// <summary>Held as one archive, checked as a whole before anything is unpacked.</summary>
    public bool HasArchive => !string.IsNullOrWhiteSpace(Download) && !string.IsNullOrWhiteSpace(Sha256);

    /// <summary>Enough to offer for installing: a name, and somewhere to fetch it from.</summary>
    public bool CanBeInstalled => !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && (HasFiles || HasArchive);

    /// <summary>
    /// Enough to load from disk. A plugin already installed needs no download URL: how it
    /// arrived stopped mattering once it was verified and unpacked.
    /// </summary>
    public bool CanBeLoaded => !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Entry);
}
