using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Post.App;

/// <summary>Callbacks the animations browser uses to reach the editor.</summary>
internal sealed record AnimationHost(
    Func<IReadOnlyList<string>> List,
    Action<string> Remember,
    Action<string> Forget,
    Action<string, bool> AddToTimeline,
    Func<string, BitmapSource?> Thumbnail,
    Func<string, string> Describe);

/// <summary>
/// Imports Lottie animations and drops them onto the timeline as their own layer.
/// Position, size, timing and keyframes are then edited like any other overlay.
/// </summary>
internal sealed class AnimationsWindow : Window
{
    private readonly AnimationHost _host;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public AnimationsWindow(AnimationHost host, Window owner)
    {
        _host = host;
        Title = "Animations"; Width = 560; Height = 620; MinWidth = 460; MinHeight = 420; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var import = new Button { Content = "Import Lottie…", Padding = new Thickness(12, 6, 12, 6) };
        import.Click += (_, _) => Import();
        top.Children.Add(import);
        top.Children.Add(new TextBlock
        {
            Text = "  .json or .lottie", Foreground = Theme.Hint, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var footer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Activated += (_, _) => Refresh();
        Refresh();
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Lottie animation|*.json;*.lottie|All files|*.*", Title = "Import a Lottie animation", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) _host.Remember(path);
        Refresh();
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var animations = _host.List();
        if (animations.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No animations yet. Import a Lottie .json and it appears here, ready to drop onto the timeline.",
                Foreground = Theme.Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var path in animations)
        {
            var row = new DockPanel { LastChildFill = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var add = new Button { Content = "Add to timeline", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            var remove = new Button { Content = "Remove", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0) };
            var current = path;
            add.Click += (_, _) => { _host.AddToTimeline(current, true); _status.Text = $"Added {Path.GetFileName(current)} to its own layer at the playhead."; };
            remove.Click += (_, _) => { _host.Forget(current); Refresh(); };
            buttons.Children.Add(add); buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);

            var preview = new Border
            {
                Width = 76, Height = 56, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromRgb(4, 10, 22)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 61, 94)), BorderThickness = new Thickness(1),
            };
            if (_host.Thumbnail(path) is { } thumbnail) preview.Child = new Image { Source = thumbnail, Stretch = Stretch.Uniform, Margin = new Thickness(3) };
            else preview.Child = new TextBlock { Text = "?", Foreground = Theme.Hint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(preview, Dock.Left); row.Children.Add(preview);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = _host.Describe(path), Foreground = Brushes.LightGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            _list.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(11, 24, 44)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 61, 94)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), Child = row,
            });
        }
    }
}
