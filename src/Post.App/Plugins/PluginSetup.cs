using Post.Core.Plugins;
using Post.Plugins;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Post.App.Plugins;

/// <summary>
/// The part of installing that is not files: a plugin fetching its own model or data.
///
/// This runs a freshly installed plugin's setup before Post has loaded it for real, in a
/// context of its own that is dropped afterwards. Nothing of the plugin is kept: it is
/// asked to download what it needs, and then forgotten until Post restarts.
/// </summary>
internal static class PluginSetup
{
    /// <summary>What the plugin says it is fetching, or null if it asks for nothing.</summary>
    public static string? DescriptionFor(PluginManifest manifest)
    {
        try
        {
            using var loaded = Open(manifest);
            return loaded?.Setup.SetupDescription;
        }
        catch { return null; }
    }

    /// <summary>
    /// Runs the plugin's own setup. Does nothing, quietly, for a plugin that has none —
    /// most plugins are only their files.
    /// </summary>
    public static async Task RunAsync(PluginManifest manifest, IProgress<PluginSetupProgress> progress, CancellationToken token)
    {
        using var loaded = Open(manifest);
        if (loaded is null) return;

        var data = Path.Combine(PluginStore.FolderFor(manifest.Id), "data");
        Directory.CreateDirectory(data);
        await loaded.Setup.PrepareAsync(data, progress, token);
    }

    private sealed class Opened(IPostPluginSetup setup, AssemblyLoadContext context) : IDisposable
    {
        public IPostPluginSetup Setup { get; } = setup;

        // Collectible, so a later reinstall is not blocked by files this pinned. It only
        // unloads once nothing of it is referenced, which is why nothing is kept.
        public void Dispose() { try { context.Unload(); } catch { } }
    }

    private static Opened? Open(PluginManifest manifest)
    {
        var folder = PluginStore.FolderFor(manifest.Id);
        var entry = Path.Combine(folder, manifest.Entry);
        if (!File.Exists(entry)) return null;

        var context = new SetupLoadContext(entry);
        var assembly = context.LoadFromAssemblyPath(entry);

        var type = assembly.GetTypes().FirstOrDefault(item =>
            typeof(IPostPluginSetup).IsAssignableFrom(item) && item is { IsAbstract: false, IsInterface: false });
        if (type is null || Activator.CreateInstance(type) is not IPostPluginSetup setup)
        {
            try { context.Unload(); } catch { }
            return null;
        }

        return new Opened(setup, context);
    }

    /// <summary>The loader's context again, but collectible: this one is meant to go away.</summary>
    private sealed class SetupLoadContext(string entryPath) : AssemblyLoadContext(isCollectible: true)
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
