namespace Post.Core;

/// <summary>
/// The LUTs Post keeps. Grades saved from the Color Grading window already landed here;
/// a .cube chosen from anywhere else used to be referenced where it sat, so it vanished
/// from the app the moment the file moved. Everything picked is copied in, which makes
/// the collection worth listing.
/// </summary>
public static class LutLibrary
{
    public static string Folder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "luts");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>Every LUT in the collection, newest first.</summary>
    public static IReadOnlyList<string> All()
    {
        try
        {
            return new DirectoryInfo(Folder).GetFiles("*.cube")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToArray();
        }
        catch { return []; }
    }

    /// <summary>
    /// Takes a copy of a LUT so it stays available, and hands back the path to use. A file
    /// already in the collection is left alone; a name already taken gets a suffix rather
    /// than overwriting someone else's LUT.
    /// </summary>
    public static string Keep(string path)
    {
        try
        {
            var folder = Folder;
            if (Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(folder, StringComparison.OrdinalIgnoreCase) == true) return path;

            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(folder, name + ".cube");
            for (var attempt = 2; File.Exists(target) && !SameContent(path, target); attempt++)
                target = Path.Combine(folder, $"{name} ({attempt}).cube");
            if (!File.Exists(target)) File.Copy(path, target);
            return target;
        }
        catch { return path; }   // keep working from where it sits rather than failing the pick
    }

    /// <summary>Removes a LUT from the collection.</summary>
    public static bool Forget(string path)
    {
        try
        {
            if (!Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Folder, StringComparison.OrdinalIgnoreCase)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// A readable name for a LUT. A file generated for one of the built-in looks carries a
    /// machine-made name, so it answers to the look's own name instead.
    /// </summary>
    public static string DisplayName(string path)
        => LookStyles.StyleForFile(path)?.Name ?? Path.GetFileNameWithoutExtension(path);

    /// <summary>True when this file was generated for a built-in look.</summary>
    public static bool IsBuiltIn(string path) => LookStyles.StyleForFile(path) is not null;

    private static bool SameContent(string first, string second)
    {
        try
        {
            var a = new FileInfo(first); var b = new FileInfo(second);
            return a.Length == b.Length && File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
        }
        catch { return false; }
    }
}
