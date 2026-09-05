using Post.Core.Plugins;
using Post.Plugins;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Post.App.Plugins;

/// <summary>One plugin that loaded, and where it came from.</summary>
internal sealed record LoadedPlugin(PluginManifest Manifest, IPostPlugin Instance);

/// <summary>
/// Finds the plugins installed on this machine and starts them.
///
/// Each folder gets its own <see cref="AssemblyLoadContext"/>, so two plugins can want
/// different versions of the same library without one of them losing. The contract
/// assembly is deliberately shared: a plugin's IPostPlugin has to be the same type Post
/// is looking for, and it would not be if each folder loaded its own copy.
/// </summary>
internal sealed class PluginLoader
{
    private readonly List<LoadedPlugin> _loaded = [];
    private readonly List<string> _failures = [];

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    /// <summary>Why a plugin did not start, for showing rather than swallowing.</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>
    /// Starts every installed plugin. The host is asked for per plugin rather than shared,
    /// so each one gets its own folder and settings.
    /// </summary>
    public void LoadAll(Func<PluginManifest, IPostHost> hostFor, Version postVersion)
    {
        foreach (var manifest in PluginStore.Installed())
        {
            try { Load(manifest, hostFor(manifest), postVersion); }
            catch (Exception exception)
            {
                _failures.Add($"{manifest.Name}: {Innermost(exception).Message}");
            }
        }
    }

    /// <summary>
    /// Starts one plugin that has just been installed, so it can be used without closing
    /// Post first. Returns what loaded, or null if it was already running — an assembly
    /// cannot be swapped underneath itself, so an update still waits for a restart.
    /// </summary>
    public LoadedPlugin? LoadNow(PluginManifest manifest, IPostHost host, Version postVersion)
    {
        if (_loaded.Any(item => item.Manifest.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))) return null;
        Load(manifest, host, postVersion);
        return _loaded[^1];
    }

    private void Load(PluginManifest manifest, IPostHost host, Version postVersion)
    {
        // Refused here rather than failing at first click, which is the whole point of
        // the plugin stating a minimum.
        if (!string.IsNullOrWhiteSpace(manifest.MinimumPostVersion)
            && Version.TryParse(manifest.MinimumPostVersion, out var minimum)
            && postVersion < minimum)
            throw new InvalidOperationException($"needs Post {minimum} or later, and this is {postVersion}");

        var folder = PluginStore.FolderFor(manifest.Id);
        var entry = Path.Combine(folder, manifest.Entry);
        if (!File.Exists(entry)) throw new FileNotFoundException($"{manifest.Entry} is not in the plugin's folder");

        var context = new PluginLoadContext(entry);
        var assembly = context.LoadFromAssemblyPath(entry);

        var type = assembly.GetTypes().FirstOrDefault(item =>
            typeof(IPostPlugin).IsAssignableFrom(item) && item is { IsAbstract: false, IsInterface: false });
        if (type is null) throw new InvalidOperationException("no class implementing IPostPlugin was found in it");

        if (Activator.CreateInstance(type) is not IPostPlugin plugin)
            throw new InvalidOperationException($"{type.Name} could not be created");

        plugin.Initialize(host);
        _loaded.Add(new LoadedPlugin(manifest, plugin));
    }

    public void ShutdownAll()
    {
        foreach (var plugin in _loaded)
        {
            try { plugin.Instance.Shutdown(); } catch { }
        }
        _loaded.Clear();
    }

    private static Exception Innermost(Exception exception)
        => exception is ReflectionTypeLoadException { LoaderExceptions: [{ } first, ..] } ? first
            : exception.InnerException is { } inner ? Innermost(inner) : exception;

    /// <summary>
    /// Resolves a plugin's own dependencies out of its folder, while leaving anything Post
    /// already has — the contract assembly above all — to the default context.
    /// </summary>
    private sealed class PluginLoadContext(string entryPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "Post.Plugins") return null;   // shared, or the interface would not match
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
