using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// The keyframe timeline in a window of its own, for when the strip above the layers is
/// too shallow to work in. It is the same control, with the full keyframe list shown.
/// </summary>
internal sealed class KeyframeEditor : Window
{
    private readonly KeyframeTimeline _timeline = new(showList: true);

    public bool Changed => _timeline.Changed;

    public KeyframeEditor(KeyframeBinding binding)
    {
        Title = $"Keyframe Timeline — {binding.TargetName}";
        Width = 760; Height = 650; MinWidth = 600; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_timeline);

        var hint = new TextBlock
        {
            Text = "Click the timeline to position the white edit caret. Mouse wheel or Left/Right steps one frame. Diamonds hold their entered value; interpolation controls the change to the next diamond.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        var close = new Button { Content = "Close", Width = 95, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        var footer = new StackPanel();
        footer.Children.Add(hint); footer.Children.Add(close);
        Grid.SetRow(footer, 1); root.Children.Add(footer);

        Content = root;
        _timeline.Bind(binding);
    }

    /// <summary>Keeps the caret in step when the edit position moves in the main window.</summary>
    public void SetCaret(TimeSpan offset) => _timeline.SetCaretFromOutside(offset);

    public void Refresh() => _timeline.Refresh();
}
