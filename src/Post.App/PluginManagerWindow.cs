using Post.App.Plugins;
using Post.Plugins;
using Post.Core.Plugins;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Post.App;

/// <summary>
/// Browsing and installing plugins from the Post plugins repository.
///
/// Installing one downloads code that will later run inside Post with everything Post
/// can reach. The checksum proves the archive arrived as published; it cannot say the
/// code is safe. So nothing installs without being asked, nothing updates on its own,
/// and the window says whose code it is and where it came from.
/// </summary>
internal sealed class PluginManagerWindow : Window
{
    private readonly PluginCatalog _catalog = new();
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { Foreground = Theme.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _refresh = new() { Content = "Check again", Padding = new Thickness(12, 5, 12, 5) };

    /// <summary>Only there while something is installing; a bar at rest says nothing.</summary>
    private readonly ProgressBar _progress = new()
    {
        Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 8, 0, 0),
        Background = new SolidColorBrush(Color.FromRgb(18, 34, 58)),
        Foreground = new SolidColorBrush(Color.FromRgb(96, 173, 224)),
        BorderThickness = new Thickness(0),
    };

    public PluginManagerWindow(Window owner)
    {
        Title = "Plugin Manager";
        Width = 640; Height = 560; MinWidth = 520; MinHeight = 420; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(18) };

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Plugins", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var source = new TextBlock { Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        source.Inlines.Add(new Run("Published at "));
        var link = new Hyperlink(new Run(PluginCatalog.RepositoryUrl))
        {
            NavigateUri = new Uri(PluginCatalog.RepositoryUrl),
            Foreground = new SolidColorBrush(Color.FromRgb(76, 215, 208)),
        };
        link.RequestNavigate += (_, args) =>
        {
            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            args.Handled = true;
        };
        source.Inlines.Add(link);
        source.Inlines.Add(new Run(". Anyone may propose one, and nothing appears there until it has been reviewed and merged."));
        heading.Children.Add(source);

        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "Close", Padding = new Thickness(16, 5, 16, 5), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var openFolder = new Button { Content = "Open plugins folder", Padding = new Thickness(12, 5, 12, 5) };
        openFolder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(PluginStore.Folder) { UseShellExecute = true }); } catch { } };
        _refresh.Click += async (_, _) => await LoadAsync(refresh: true);
        footer.Children.Add(openFolder); footer.Children.Add(_refresh); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        DockPanel.SetDock(_progress, Dock.Bottom);
        root.Children.Add(_progress);

        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;

        Loaded += async (_, _) => await LoadAsync(refresh: false);
        Closed += (_, _) => _client.Dispose();
    }

    private async Task LoadAsync(bool refresh)
    {
        _refresh.IsEnabled = false;
        _status.Text = "Looking for plugins…";
        _list.Children.Clear();
        try
        {
            var available = await _catalog.ListAsync(refresh);
            var installed = PluginStore.Installed();
            Render(available, installed);
        }
        catch (Exception exception)
        {
            _status.Text = $"The plugin list could not be fetched: {exception.Message}";
            Render([], PluginStore.Installed());
        }
        finally { _refresh.IsEnabled = true; }
    }

    private void Render(IReadOnlyList<PluginManifest> available, IReadOnlyList<PluginManifest> installed)
    {
        _list.Children.Clear();

        // Anything installed that the repository no longer lists still belongs on screen,
        // or it could never be removed from here.
        var everything = available
            .Concat(installed.Where(item => !available.Any(offer => offer.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))))
            .ToArray();

        if (everything.Length == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No plugins have been published yet. When one appears in the repository it will show up here.",
                Foreground = Theme.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 12, 2, 0),
            });
            _status.Text = $"Nothing to install. Checked {PluginCatalog.Repository}.";
            return;
        }

        foreach (var plugin in everything)
        {
            var current = installed.FirstOrDefault(item => item.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase));
            _list.Children.Add(BuildRow(plugin, current));
        }
        _status.Text = $"{available.Count} available, {installed.Count} installed.";
    }

    private Border BuildRow(PluginManifest plugin, PluginManifest? installed)
    {
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = plugin.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        details.Children.Add(new TextBlock
        {
            Text = $"{plugin.Version}  •  by {(string.IsNullOrWhiteSpace(plugin.Author) ? "unstated" : plugin.Author)}",
            Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 2, 0, 4),
        });
        if (!string.IsNullOrWhiteSpace(plugin.Description))
            details.Children.Add(new TextBlock { Text = plugin.Description, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (plugin.Capabilities.Length > 0)
            details.Children.Add(new TextBlock
            {
                Text = "Asks for: " + string.Join(", ", plugin.Capabilities),
                Foreground = Theme.Hint, FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
            });

        var action = new Button { Padding = new Thickness(14, 5, 14, 5), MinWidth = 92, VerticalAlignment = VerticalAlignment.Top };
        var remove = new Button { Content = "Remove", Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(action);

        // Either way of publishing counts. Asking only about the archive left a plugin
        // held as loose files listed but not installable, with no button and no reason
        // given for it.
        var offered = plugin.CanBeInstalled;
        if (installed is null)
        {
            action.Content = "Install";
            action.IsEnabled = offered;
        }
        else
        {
            // A version string is what an author remembers to change. The checksum is what
            // actually differs, so a rebuilt plugin published under the same version is
            // still offered — and reinstalling an identical one stays possible, because a
            // half-installed plugin looks identical to a working one.
            var sameVersion = installed.Version.Equals(plugin.Version, StringComparison.OrdinalIgnoreCase);
            var sameFiles = sameVersion
                && installed.Sha256.Trim().Equals(plugin.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);

            action.Content = sameFiles ? "Reinstall" : sameVersion ? "Update" : $"Update to {plugin.Version}";
            action.IsEnabled = offered;
            details.Children.Add(new TextBlock
            {
                Text = sameFiles
                    ? $"Installed, version {installed.Version}."
                    : sameVersion
                        ? $"Version {installed.Version} is installed, and this is a newer build of it."
                        : $"Version {installed.Version} is installed.",
                Foreground = new SolidColorBrush(Color.FromRgb(126, 214, 160)), FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
            });
            buttons.Children.Add(remove);
        }

        action.Click += async (_, _) => await InstallAsync(plugin);
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove {plugin.Name} from this machine?", "Plugin Manager", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!PluginStore.Remove(plugin.Id)) { MessageBox.Show(this, "That plugin's folder could not be removed. It may be in use.", "Plugin Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            _status.Text = $"{plugin.Name} removed. It stops being loaded when Post restarts.";
            _ = LoadAsync(refresh: false);
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        row.Children.Add(details); row.Children.Add(buttons);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromRgb(9, 20, 38)), Child = row,
        };
    }

    private async Task InstallAsync(PluginManifest plugin)
    {
        // Just the question. What a plugin can reach is said once, in the window this was
        // clicked from; saying it again here is a wall of text between someone and the
        // thing they already decided to do.
        var answer = MessageBox.Show(this,
            $"Install {plugin.Name} {plugin.Version} by {(string.IsNullOrWhiteSpace(plugin.Author) ? "an unstated author" : plugin.Author)}?",
            "Install plugin", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var updating = PluginStore.InstalledVersionOf(plugin.Id) is not null;

        _refresh.IsEnabled = false;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        try
        {
            // Installing is one wait, not two. A plugin that needs a model of its own says
            // so, and fetching it happens here rather than surprising someone the first
            // time they use it. The files are the small half, so they take the small share
            // of the bar when there is anything after them.
            // What it fetches for itself comes from the manifest, because the plugin cannot
            // be asked yet: its .dll is the thing being downloaded. Asking first was the
            // bug that stopped a model ever being fetched on a first install — the file was
            // not there, the answer was always no, and the step was silently skipped.
            var filesShare = plugin.Setup is null ? 1 : .25;

            Report(0, $"Fetching {plugin.Name}");
            await PluginStore.InstallAsync(plugin, _client,
                new Progress<double>(value => Report(value * filesShare, $"Fetching {plugin.Name}")));

            // Now it is on disk it can speak for itself, which covers a plugin whose
            // manifest never mentioned its setup.
            var extra = plugin.Setup ?? await Task.Run(() => PluginSetup.DescriptionFor(plugin));

            if (extra is not null)
            {
                Report(filesShare, $"Fetching {extra}");
                await PluginSetup.RunAsync(plugin,
                    new Progress<PluginSetupProgress>(step =>
                        Report(filesShare + step.Fraction * (1 - filesShare), step.Stage)),
                    CancellationToken.None);
            }

            // Started here rather than at the next launch. Nobody installs a thing in order
            // to close the application.
            //
            // An update is the exception: the copy already running cannot be unloaded, and
            // starting the new one beside it would put every menu entry on twice.
            Report(1, "Starting it");
            var started = !updating && Owner is MainWindow main && main.StartPluginNow(plugin);

            _status.Text = started
                ? $"{plugin.Name} {plugin.Version} installed, and ready to use."
                : updating
                    ? $"{plugin.Name} updated to {plugin.Version}. The copy already running stays until Post restarts."
                    : $"{plugin.Name} {plugin.Version} installed. It is loaded when Post restarts.";
            await LoadAsync(refresh: false);
        }
        catch (Exception exception)
        {
            _status.Text = $"{plugin.Name} was not installed.";
            MessageBox.Show(this, exception.Message, "Plugin Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refresh.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Moves the bar and says what is happening, as one thing.</summary>
    private void Report(double fraction, string stage)
    {
        _progress.Value = Math.Clamp(fraction, 0, 1);
        _status.Text = $"{stage}… {fraction * 100:0}%";
    }
}
