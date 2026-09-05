using Microsoft.Win32;
using Post.Core;
using System.IO;
using System.Windows;

namespace Post.App;

/// <summary>
/// Finding media again after it moves, and packaging a project so it can be handed on.
///
/// A project references its media rather than holding a copy, which is how editors of
/// this kind work: media is large, several projects share the same files, and you often
/// want the original rather than a snapshot of it. The cost is that files move, so what
/// matters is being able to point a project back at them.
/// </summary>
public partial class MainWindow
{
    private async void PackageProject_Click(object sender, RoutedEventArgs e) => await PackageProjectAsync();

    private void PluginManager_Click(object sender, RoutedEventArgs e) => new PluginManagerWindow(this).ShowDialog();

    /// <summary>Every clip whose source could not be found.</summary>
    private IReadOnlyList<ClipItem> OfflineClips => _clips.Where(clip => clip.IsOffline).ToArray();

    /// <summary>
    /// Points a clip at a file the user chooses, then offers to find the rest of the
    /// missing media in the same folder, since they usually moved together.
    /// </summary>
    private async Task RelinkClipAsync(ClipItem clip)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate {clip.DisplayName}",
            FileName = clip.DisplayName,
            Filter = $"{clip.DisplayName}|{clip.DisplayName}|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!await RelinkToAsync(clip, dialog.FileName)) return;

        var folder = Path.GetDirectoryName(dialog.FileName);
        var stillMissing = OfflineClips;
        if (folder is null || stillMissing.Count == 0)
        {
            FinishRelink();
            return;
        }

        var answer = MessageBox.Show(this,
            $"{stillMissing.Count} other file{(stillMissing.Count == 1 ? " is" : "s are")} still missing. Look for {(stillMissing.Count == 1 ? "it" : "them")} in that folder?",
            "Relink media", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            foreach (var other in stillMissing)
                if (MediaPaths.FindByName(folder, other.DisplayName) is { } candidate)
                    await RelinkToAsync(other, candidate);

        FinishRelink();
    }

    /// <summary>
    /// Repoints one clip at a file. The clip object is kept, so every placement, effect
    /// and keyframe that refers to it carries on pointing at the same thing.
    /// </summary>
    private async Task<bool> RelinkToAsync(ClipItem clip, string path)
    {
        try
        {
            var media = await _probe.ProbeAsync(path);
            clip.SourcePath = path;
            clip.Media = media;
            clip.IsOffline = false;
            clip.PreviewPath = null;
            // Segments were kept while the clip was offline; trim any that outlast the file.
            foreach (var segment in clip.Segments.ToArray())
            {
                segment.SourceStart = ClampTime(segment.SourceStart, TimeSpan.Zero, media.Duration);
                segment.SourceEnd = ClampTime(segment.SourceEnd, segment.SourceStart, media.Duration);
            }
            if (clip.Segments.All(segment => segment.Duration <= TimeSpan.Zero))
            {
                clip.Segments.Clear();
                clip.Segments.Add(new MediaSegment { SourceStart = TimeSpan.Zero, SourceEnd = media.Duration });
            }
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} could not be read.\n\n{exception.Message}", "Relink media", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void FinishRelink()
    {
        InvalidateCompositionPreview();
        CommitProjectEdit();
        RefreshTray();
        RefreshLayerStack();
        var remaining = OfflineClips.Count;
        if (remaining > 0)
            MessageBox.Show(this, $"{remaining} file{(remaining == 1 ? " is" : "s are")} still offline. Click one in the Media panel to locate it.",
                "Relink media", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Copies every source the project uses into a folder beside it and points the project
    /// at the copies, so the whole thing can be moved or handed on as one piece. This is
    /// the deliberate version of owning the media, rather than doing it on every save.
    /// </summary>
    private async Task PackageProjectAsync()
    {
        if (_project.FilePath is null)
        {
            MessageBox.Show(this, "Save the project first, so there is somewhere to put its media.", "Package project", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (OfflineClips.Count > 0)
        {
            MessageBox.Show(this, "Some media is offline. Relink it first, or it cannot be packaged.", "Package project", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var projectFolder = Path.GetDirectoryName(_project.FilePath)!;
        var mediaFolder = Path.Combine(projectFolder, $"{Path.GetFileNameWithoutExtension(_project.FilePath)} Media");
        var sources = _clips.Select(clip => clip.SourcePath)
            .Concat(_animations)
            .Concat(_composition.Layers.SelectMany(layer => layer.Graphics).Select(graphic => graphic.ImagePath).OfType<string>())
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var total = sources.Sum(path => new FileInfo(path).Length);
        var answer = MessageBox.Show(this,
            $"Copy {sources.Length} file{(sources.Length == 1 ? "" : "s")} ({total / 1024d / 1024:0.#} MB) into\n{mediaFolder}\n\nThe project will then use the copies.",
            "Package project", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        await RunBusyAsync("Packaging project…", async token =>
        {
            Directory.CreateDirectory(mediaFolder);
            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                var target = UniqueTarget(mediaFolder, source, moved);
                if (!File.Exists(target)) await Task.Run(() => File.Copy(source, target), token);
                moved[source] = target;
            }

            foreach (var clip in _clips) if (moved.TryGetValue(clip.SourcePath, out var target)) clip.SourcePath = target;
            for (var i = 0; i < _animations.Count; i++) if (moved.TryGetValue(_animations[i], out var target)) _animations[i] = target;
            foreach (var graphic in _composition.Layers.SelectMany(layer => layer.Graphics))
                if (graphic.ImagePath is { } image && moved.TryGetValue(image, out var target)) graphic.ImagePath = target;
        });

        CommitProjectEdit();
        RefreshTray();
        await SaveProjectAsync(_project);
        MessageBox.Show(this, $"The project and its media are now in\n{projectFolder}", "Package project", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>A free name in the media folder, so two files called the same thing both survive.</summary>
    private static string UniqueTarget(string folder, string source, Dictionary<string, string> taken)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var target = Path.Combine(folder, name + extension);
        for (var attempt = 2; taken.ContainsValue(target) || (File.Exists(target) && new FileInfo(target).Length != new FileInfo(source).Length); attempt++)
            target = Path.Combine(folder, $"{name} ({attempt}){extension}");
        return target;
    }

    /// <summary>
    /// Asked once, on the first import into a project: media is referenced rather than
    /// copied, and this is the moment that choice actually matters.
    /// </summary>
    private void OfferCopyOnImport()
    {
        if (!_settings.AskAboutCopyOnImport || _clips.Count > 0 || _askedAboutCopyOnImport) return;
        _askedAboutCopyOnImport = true;

        var again = new System.Windows.Controls.CheckBox
        {
            Content = "Don't ask again", Margin = new Thickness(0, 14, 0, 0),
            Foreground = System.Windows.Media.Brushes.White,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Copy media into this project's folder?",
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Post normally points at your files where they are, which keeps projects small and lets several of them share the same footage. Copying instead makes this project self-contained, at the cost of disk space and slower imports — and changes to the original file stop reaching it.",
            Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(again);

        var yes = new System.Windows.Controls.Button { Content = "Copy them", Padding = new Thickness(16, 6, 16, 6), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var no = new System.Windows.Controls.Button { Content = "Leave them where they are", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(yes); buttons.Children.Add(no);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Media", Width = 520, SizeToContent = SizeToContent.Height, Content = panel,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        var copy = false;
        yes.Click += (_, _) => { copy = true; window.DialogResult = true; };
        no.Click += (_, _) => { window.DialogResult = false; };
        window.ShowDialog();

        var folder = copy ? ChooseMediaFolder() : null;
        if (copy && folder is null) copy = false;   // no folder means nowhere to copy to
        _settings = _settings with
        {
            CopyMediaOnImport = copy,
            MediaCopyFolder = folder ?? _settings.MediaCopyFolder,
            AskAboutCopyOnImport = again.IsChecked != true,
        };
        _settings.Save();

        MessageBox.Show(this,
            copy ? $"Imported media will be copied into{Environment.NewLine}{folder}{Environment.NewLine}{Environment.NewLine}You can change this in Settings, under Copy media on import."
                 : "Imported media will stay where it is. You can change this in Settings, under Copy media on import.",
            "Media", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool _askedAboutCopyOnImport;

    /// <summary>
    /// Asks where copied media should live. Returns null when the choice is declined, so
    /// callers can leave the setting alone rather than switching it on with nowhere to go.
    /// </summary>
    private string? ChooseMediaFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should copied media be kept?",
            InitialDirectory = string.IsNullOrWhiteSpace(_settings.MediaCopyFolder) ? _settings.DefaultOutputFolder : _settings.MediaCopyFolder,
        };
        if (dialog.ShowDialog(this) != true) return null;
        try { Directory.CreateDirectory(dialog.FolderName); } catch { return null; }
        return dialog.FolderName;
    }

    /// <summary>
    /// Copying needs a folder to copy into. One chosen in Settings is used as it is;
    /// without one, a project that has never been saved has nowhere to put anything. Rather than refusing the copy, the project gets a home: named
    /// after the first file being imported, which is nearly always what it is about.
    ///
    /// It asks where, rather than choosing silently, because everything imported from
    /// here is about to be duplicated into that folder and that can run to gigabytes.
    /// </summary>
    private async Task<bool> EnsureProjectHomeAsync(string firstImport)
    {
        if (!_settings.CopyMediaOnImport || _project.FilePath is not null) return true;
        // A media folder was already chosen, so the project does not need saving first.
        if (!string.IsNullOrWhiteSpace(_settings.MediaCopyFolder)) return true;

        var suggested = Path.GetFileNameWithoutExtension(firstImport);
        if (string.IsNullOrWhiteSpace(suggested)) suggested = "Untitled Project";
        _project.Name = suggested;

        var dialog = new SaveFileDialog
        {
            Title = "Where should this project and its media live?",
            FileName = $"{suggested}.post",
            Filter = "Post Project|*.post", DefaultExt = "post", AddExtension = true,
            InitialDirectory = _settings.DefaultOutputFolder,
        };
        if (dialog.ShowDialog(this) != true)
        {
            // Not saving is a fair answer; this import just references its files instead.
            MessageBox.Show(this,
                "The project has not been saved, so there is nowhere to copy media into. These files will be used where they are, and copying resumes once the project is saved.",
                "Media", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _project.FilePath = dialog.FileName;
        _project.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
        await SaveProjectAsync(_project);
        return true;
    }

    /// <summary>
    /// Copies an imported file into the project's media folder when that is switched on,
    /// and hands back the path to actually use.
    /// </summary>
    private string ImportedPath(string path)
    {
        if (!_settings.CopyMediaOnImport) return path;
        try
        {
            // The folder chosen in Settings, or one beside the project when there is none.
            var folder = _settings.MediaCopyFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                if (_project.FilePath is null) return path;
                folder = Path.Combine(Path.GetDirectoryName(_project.FilePath)!, $"{Path.GetFileNameWithoutExtension(_project.FilePath)} Media");
            }
            Directory.CreateDirectory(folder);
            var target = UniqueTarget(folder, path, []);
            if (!File.Exists(target)) File.Copy(path, target);
            return target;
        }
        catch { return path; }   // a failed copy should not stop the import
    }
}
