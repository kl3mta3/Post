using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Post.App;

/// <summary>One destination's outcome, with a link when the platform gave one back.</summary>
internal sealed record PublishSummaryLine(string Platform, string Message, string? Url);

/// <summary>
/// What happened to a publish. A message box could not offer the link, which is the
/// one thing worth taking away from a finished upload.
/// </summary>
internal sealed class PublishSummaryWindow : Window
{
    private readonly TextBlock _status = new() { Foreground = Theme.Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    public PublishSummaryWindow(IReadOnlyList<PublishSummaryLine> lines, string outputPath, Window owner)
    {
        Title = "Published"; Width = 560; SizeToContent = SizeToContent.Height; MinHeight = 200; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = lines.Count == 1 ? "Published" : $"Published to {lines.Count} accounts", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });

        foreach (var line in lines) panel.Children.Add(BuildLine(line));

        panel.Children.Add(new TextBlock { Text = "Rendered file", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var showFile = new Button { Content = "Show", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        var copyPath = new Button { Content = "Copy path", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        var copyFile = new Button { Content = "Copy file", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Put the file on the clipboard, ready to paste into a chat or a folder" };
        showFile.Click += (_, _) => Reveal(outputPath);
        copyPath.Click += (_, _) => Copy(outputPath, "Path copied.");
        copyFile.Click += (_, _) => CopyFile(outputPath);
        DockPanel.SetDock(showFile, Dock.Right); pathRow.Children.Add(showFile);
        DockPanel.SetDock(copyFile, Dock.Right); pathRow.Children.Add(copyFile);
        DockPanel.SetDock(copyPath, Dock.Right); pathRow.Children.Add(copyPath);
        pathRow.Children.Add(new TextBlock { Text = outputPath, Foreground = Brushes.LightGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = outputPath });
        panel.Children.Add(pathRow);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Content = "Close", Padding = new Thickness(16, 6, 16, 6), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        footer.Children.Add(_status);
        panel.Children.Add(footer);

        Content = panel;
    }

    private FrameworkElement BuildLine(PublishSummaryLine line)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = line.Platform, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(122, 226, 168)) });
        panel.Children.Add(new TextBlock { Text = line.Message, Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        if (!string.IsNullOrWhiteSpace(line.Url))
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var copy = new Button { Content = "Copy link", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            copy.Click += (_, _) => Copy(line.Url!, "Link copied.");
            DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);

            // The address is clickable too, for going straight to the video.
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Open in your browser" };
            var link = new Hyperlink(new Run(line.Url)) { NavigateUri = new Uri(line.Url!), Foreground = new SolidColorBrush(Color.FromRgb(96, 214, 236)) };
            link.RequestNavigate += (_, args) =>
            {
                try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                args.Handled = true;
            };
            text.Inlines.Add(link);
            row.Children.Add(text);
            panel.Children.Add(row);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(11, 24, 44)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 61, 94)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = panel,
        };
    }

    private void Copy(string value, string confirmation)
    {
        try { Clipboard.SetText(value); _status.Text = confirmation; }
        catch (Exception exception) { _status.Text = $"Could not copy: {exception.Message}"; }
    }

    /// <summary>Copies the file itself, the way a finished export does.</summary>
    private void CopyFile(string path)
    {
        try
        {
            if (!File.Exists(path)) { _status.Text = "The file is no longer there."; return; }
            var files = new System.Collections.Specialized.StringCollection { path };
            var data = new DataObject();
            data.SetFileDropList(files);
            Clipboard.SetDataObject(data, true);
            _status.Text = "File copied — paste it anywhere.";
        }
        catch (Exception exception) { _status.Text = $"Could not copy: {exception.Message}"; }
    }

    private static void Reveal(string path)
    {
        try
        {
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(path) ? $"\"{path}\"" : null;
            if (arguments is null) return;
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch { }
    }
}
