namespace Post.Core;

/// <summary>How this copy of Post got onto the machine.</summary>
public enum PostInstallKind
{
    /// <summary>Put here by the installer, which knows how to replace itself.</summary>
    Installed,

    /// <summary>A folder someone unzipped, which the installer knows nothing about.</summary>
    Portable,
}

/// <summary>
/// Telling an installed Post from a portable one, because updating them is not the same
/// job. Running the installer over a portable copy quietly installs a second Post
/// somewhere else and leaves the folder the person is actually using untouched.
/// </summary>
public static class InstallKind
{
    /// <summary>The folder Post is running from.</summary>
    public static string AppFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>
    /// Inno Setup always writes its uninstaller into the folder it installs to, so its
    /// presence beside Post is the plainest evidence that this copy was installed. No
    /// registry lookup, and it stays true whether the install was per-user or per-machine.
    /// </summary>
    public static PostInstallKind Current => File.Exists(Path.Combine(AppFolder, "unins000.exe"))
        ? PostInstallKind.Installed
        : PostInstallKind.Portable;

    /// <summary>Which release asset suits this copy: the installer, or the zip.</summary>
    public static string ExtensionFor(PostInstallKind kind) => kind == PostInstallKind.Installed ? ".exe" : ".zip";
}
