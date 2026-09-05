using Microsoft.Win32;
using Post.Core;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Shown at startup when ffmpeg cannot be found. Post reads, plays and exports everything
/// through it, so without it there is nothing to open the main window for.
/// </summary>
internal sealed class FfmpegSetupWindow : Window
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly TextBlock _status = new() { Foreground = Theme.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _download;
    private readonly Button _locate;
    private readonly Button _quit;

    /// <summary>Where ffmpeg was found, once this window closes successfully.</summary>
    public FfmpegTools? Tools { get; private set; }

    public FfmpegSetupWindow()
    {
        Title = "Post needs FFmpeg";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = "FFmpeg is missing", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Post reads, plays and exports every file through FFmpeg, and cannot do any of it without one. Post can fetch a copy now — about 106 MB, checked against the checksum published with it, and kept in your app data folder rather than anywhere shared.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"From {FfmpegInstaller.SourceDescription}.",
            Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(_progress);
        panel.Children.Add(_status);

        _download = new Button { Content = "Download FFmpeg", Padding = new Thickness(16, 6, 16, 6), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        _locate = new Button { Content = "I already have it…", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        _quit = new Button { Content = "Quit", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(_download); buttons.Children.Add(_locate); buttons.Children.Add(_quit);
        panel.Children.Add(buttons);

        _download.Click += async (_, _) => await DownloadAsync();
        _locate.Click += (_, _) => Locate();
        _quit.Click += (_, _) => { DialogResult = false; };
        Closed += (_, _) => _client.Dispose();

        _ = ShowVersionAsync();
        Content = panel;
    }

    private async Task ShowVersionAsync()
    {
        if (await FfmpegInstaller.LatestVersionAsync(_client) is { } version)
            _status.Text = $"The current release is {version}.";
    }

    private async Task DownloadAsync()
    {
        SetBusy(true);
        _progress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<double>(value =>
            {
                _progress.Value = value;
                _status.Text = $"Downloading… {value * 100:0}%";
            });
            _status.Text = "Downloading…";
            Tools = await FfmpegInstaller.InstallAsync(_client, progress);
            _status.Text = "FFmpeg is ready.";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _status.Text = "The download did not finish.";
            MessageBox.Show(this,
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}You can try again, or point Post at a copy you already have.",
                "Post needs FFmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    /// <summary>Points Post at an existing copy, for anyone who already keeps one.</summary>
    private void Locate()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Find ffmpeg.exe",
            FileName = "ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var folder = Path.GetDirectoryName(dialog.FileName);
        if (folder is null) return;
        if (FfmpegLocator.TryFind(folder) is not { } found)
        {
            MessageBox.Show(this, "That folder has ffmpeg.exe but not ffprobe.exe. Post needs both, and they normally sit together.",
                "Post needs FFmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Copied in, so moving or deleting the original later does not break Post.
        try
        {
            Directory.CreateDirectory(FfmpegLocator.DownloadedToolsFolder);
            File.Copy(found.Ffmpeg, Path.Combine(FfmpegLocator.DownloadedToolsFolder, "ffmpeg.exe"), overwrite: true);
            File.Copy(found.Ffprobe, Path.Combine(FfmpegLocator.DownloadedToolsFolder, "ffprobe.exe"), overwrite: true);
            Tools = FfmpegLocator.TryFind() ?? found;
        }
        catch { Tools = found; }   // using it where it sits is better than refusing to start

        DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        _download.IsEnabled = !busy;
        _locate.IsEnabled = !busy;
        _quit.IsEnabled = !busy;
    }
}
