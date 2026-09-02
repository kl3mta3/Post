using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace Post.App;

internal sealed class EffectsWindow : Window
{
    internal sealed record EffectOptions(string Name, double DurationSeconds, double From, double To);
    private enum ParameterKind { None, Opacity, PositionX, Scale, Volume }
    private sealed record Preset(string Category, string Name, string Description, ParameterKind Parameters = ParameterKind.None,
        double Duration = 1, double From = 0, double To = 1, bool Available = true);
    private static readonly Preset[] Presets =
    [
        new("Motion", "Fade In", "Animate opacity from transparent to fully visible.", ParameterKind.Opacity, 1, 0, 1),
        new("Motion", "Fade Out", "Animate opacity from fully visible to transparent.", ParameterKind.Opacity, 1, 1, 0),
        new("Motion", "Slide In From Left", "Enter from the left side and settle in the center.", ParameterKind.PositionX, 1, -1, .5),
        new("Motion", "Slide In From Right", "Enter from the right side and settle in the center.", ParameterKind.PositionX, 1, 1.5, .5),
        new("Transform, Distort and Perspective", "Zoom In", "Smoothly grow from one scale to another.", ParameterKind.Scale, 1, .75, 1),
        new("Transform, Distort and Perspective", "Zoom Out", "Smoothly shrink from one scale to another.", ParameterKind.Scale, 1, 1.25, 1),
        new("Volume and Dynamics", "Audio Fade In", "Raise volume smoothly from silence.", ParameterKind.Volume, 1, 0, 1),
        new("Volume and Dynamics", "Audio Fade Out", "Lower volume smoothly to silence.", ParameterKind.Volume, 1, 1, 0),
        new("Blur and Sharpen", "Blur", "Video blur effects are planned for a future update.", Available: false),
        new("Color and Image correction", "Color Grading", "Color grading controls are planned for a future update.", Available: false),
        new("Channels", "Audio EQ", "Audio equalization controls are planned for a future update.", Available: false),
        new("Stylize", "Stylize", "Additional stylized effects are planned for a future update.", Available: false),
    ];

    private readonly Action<EffectOptions> _apply;
    private readonly TreeView _tree = new();
    private readonly TextBlock _name = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 14) };
    private readonly StackPanel _options = new();
    private TextBox _duration = new();
    private TextBox _from = new();
    private TextBox _to = new();
    private readonly Button _applyButton = new() { Content = "Apply to selected item", Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };

    public EffectsWindow(Action<EffectOptions> apply)
    {
        _apply = apply; Title = "Effects"; Width = 610; Height = 570; MinWidth = 500; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;
        var root = new Grid { Margin = new Thickness(12) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var browser = new DockPanel(); var heading = new TextBlock { Text = "EFFECTS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(4, 2, 0, 8) }; DockPanel.SetDock(heading, Dock.Top); browser.Children.Add(heading); browser.Children.Add(_tree);
        var border = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(4), Child = browser }; root.Children.Add(border);
        var details = new StackPanel { Margin = new Thickness(14, 8, 8, 8) }; details.Children.Add(_name); details.Children.Add(_description); details.Children.Add(_options); details.Children.Add(_applyButton); Grid.SetColumn(details, 2); root.Children.Add(details); Content = root;
        foreach (var category in Presets.Select(item => item.Category).Distinct())
        {
            var group = new TreeViewItem { Header = category, IsExpanded = category is "Motion" or "Transform, Distort and Perspective" };
            foreach (var preset in Presets.Where(item => item.Category == category)) group.Items.Add(new TreeViewItem { Header = preset.Name + (preset.Available ? "" : "  (coming soon)"), Tag = preset, IsEnabled = preset.Available });
            _tree.Items.Add(group);
        }
        _tree.SelectedItemChanged += (_, _) => SelectPreset((_tree.SelectedItem as TreeViewItem)?.Tag as Preset);
        _applyButton.Click += (_, _) => ApplySelected();
    }

    private void SelectPreset(Preset? preset)
    {
        _applyButton.Tag = preset; _applyButton.IsEnabled = preset?.Available == true;
        _name.Text = preset?.Name ?? "Choose an effect";
        _description.Text = preset?.Description ?? "Select an effect from the browser. Available effects create editable keyframes on the currently selected clip or overlay.";
        BuildOptions(preset);
    }

    private void BuildOptions(Preset? preset)
    {
        _options.Children.Clear();
        if (preset?.Available != true || preset.Parameters == ParameterKind.None) return;
        _options.Children.Add(new TextBlock { Text = "EFFECT OPTIONS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(0, 0, 0, 7) });
        _duration = new TextBox { Text = Number(preset.Duration) };
        _from = new TextBox { Text = Number(preset.From) };
        _to = new TextBox { Text = Number(preset.To) };
        _options.Children.Add(Field("Duration (seconds)", _duration));
        var (fromLabel, toLabel, hint) = preset.Parameters switch
        {
            ParameterKind.Opacity => ("From opacity", "To opacity", "Opacity range: 0 to 1"),
            ParameterKind.PositionX => ("From position X", "To position X", "Position can be negative; 0.5 is centered"),
            ParameterKind.Scale => ("From scale", "To scale", "Scale: 1 is 100%"),
            ParameterKind.Volume => ("From volume", "To volume", "Volume: 0 is silent; 1 is original"),
            _ => ("From", "To", "")
        };
        var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition());
        var from = Field(fromLabel, _from); var to = Field(toLabel, _to); Grid.SetColumn(to, 1); pair.Children.Add(from); pair.Children.Add(to); _options.Children.Add(pair);
        _options.Children.Add(new TextBlock { Text = hint, Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(2, 1, 0, 12) });
    }

    private static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 3) }); panel.Children.Add(control); return panel;
    }

    private void ApplySelected()
    {
        if (_applyButton.Tag is not Preset preset) return;
        if (!TryNumber(_duration.Text, out var duration) || duration <= 0 || !TryNumber(_from.Text, out var from) || !TryNumber(_to.Text, out var to))
        { MessageBox.Show(this, "Enter valid numeric values. Duration must be greater than zero.", "Effect options", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        switch (preset.Parameters)
        {
            case ParameterKind.Opacity: from = Math.Clamp(from, 0, 1); to = Math.Clamp(to, 0, 1); break;
            case ParameterKind.Scale: from = Math.Clamp(from, .01, 20); to = Math.Clamp(to, .01, 20); break;
            case ParameterKind.Volume: from = Math.Clamp(from, 0, 4); to = Math.Clamp(to, 0, 4); break;
        }
        _from.Text = Number(from); _to.Text = Number(to); _apply(new EffectOptions(preset.Name, duration, from, to));
    }

    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
