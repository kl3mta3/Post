using Microsoft.Win32;

namespace Post.App;
internal static class ShellIntegration
{
    private const string MenuName = "Quick Edit with Post";
    public static void EnsureRegistered()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the Post executable path.");
        foreach (var extension in Post.Core.MediaProbeService.SupportedExtensions)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\ClipEdit", false);
            using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\Post");
            shell?.SetValue("", MenuName); shell?.SetValue("Icon", executable);
            using var command = shell?.CreateSubKey("command"); command?.SetValue("", $"\"{executable}\" \"%1\"");
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ClipEdit.Project", false);
        using (var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.post")) extension?.SetValue("", "Post.Project");
        using (var legacyExtension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.clipedit")) legacyExtension?.SetValue("", "Post.Project");
        using (var project = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Post.Project")) { project?.SetValue("", "Post Project"); project?.SetValue("FriendlyTypeName", "Post Project"); }
        using (var icon = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Post.Project\DefaultIcon")) icon?.SetValue("", executable);
        using (var command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Post.Project\shell\open\command")) command?.SetValue("", $"\"{executable}\" \"%1\"");
    }
    public static void Remove()
    {
        foreach (var extension in Post.Core.MediaProbeService.SupportedExtensions)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\Post", false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\SystemFileAssociations\{extension}\shell\ClipEdit", false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Post.Project", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\ClipEdit.Project", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.post", false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.clipedit", false);
    }
}
