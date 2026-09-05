namespace Post.Core;

/// <summary>
/// Finding the files a project points at. A project references its media rather than
/// holding a copy, the way every editor of this kind does, so the paths have to survive
/// the folder being moved, renamed, or opened on another machine.
/// </summary>
public static class MediaPaths
{
    /// <summary>
    /// Where this file sits relative to the project, when it can be said at all. Media on
    /// another drive has no relative path, and gets none.
    /// </summary>
    public static string? RelativeTo(string? projectFolder, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return null;
        try
        {
            if (!Path.IsPathRooted(absolutePath)) return absolutePath;
            var root = Path.GetPathRoot(Path.GetFullPath(projectFolder));
            if (!string.Equals(root, Path.GetPathRoot(Path.GetFullPath(absolutePath)), StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetRelativePath(projectFolder, absolutePath);
        }
        catch { return null; }
    }

    /// <summary>
    /// The file this entry means now, or null when it cannot be found. The stored path is
    /// tried first, then the same spot relative to the project, then the project's own
    /// folder by name, which covers a project and its media being moved together.
    /// </summary>
    public static string? Resolve(string? absolutePath, string? relativePath, string? projectFolder)
    {
        if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath)) return absolutePath;
        if (string.IsNullOrWhiteSpace(projectFolder)) return null;

        foreach (var candidate in Candidates(absolutePath, relativePath, projectFolder))
        {
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? absolutePath, string? relativePath, string projectFolder)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            yield return Path.Combine(projectFolder, relativePath);
            var name = Path.GetFileName(relativePath);
            if (!string.IsNullOrWhiteSpace(name)) yield return Path.Combine(projectFolder, name);
        }
        if (!string.IsNullOrWhiteSpace(absolutePath))
        {
            var name = Path.GetFileName(absolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return Path.Combine(projectFolder, name);
                // Packaged projects keep their media in a folder beside the project file.
                foreach (var folder in SafeDirectories(projectFolder)) yield return Path.Combine(folder, name);
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.GetDirectories(folder); }
        catch { return []; }
    }

    /// <summary>
    /// Looks for a file by name under a folder, for relinking the rest of a project's
    /// media once one file has been found. Subfolders are searched, deepest last.
    /// </summary>
    public static string? FindByName(string folder, string fileName)
    {
        try
        {
            var direct = Path.Combine(folder, fileName);
            if (File.Exists(direct)) return direct;
            return Directory.EnumerateFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }
}
