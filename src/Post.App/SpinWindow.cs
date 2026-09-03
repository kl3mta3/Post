using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Sets a constant turn for one item, in degrees per second. Rotation keyframes cover
/// deliberate turns; this covers the thing people actually want most of the time, which
/// is something that simply keeps spinning.
/// </summary>
internal sealed class SpinWindow : Window
{
    private readonly Slider _speed;
    private readonly TextBox _value;
    private bool _syncing;

    public double Speed { get; private set; }

    public SpinWindow(string targetName, double current, Window owner)
    {
        Speed = current;
        Title = $"Spin — {targetName}";
        Width = 520; Height = 320; Owner = owner; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Turn speed in degrees per second", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Positive turns clockwise, negative anticlockwise. 360 is one full turn every second; 0 stops it. The turn happens about this layer's anchor point.",
            Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });

        _speed = new Slider { Minimum = -720, Maximum = 720, Value = Math.Clamp(current, -720, 720), TickFrequency = 15, IsSnapToTickEnabled = false };
        _value = new TextBox { Text = current.ToString("0.##", CultureInfo.InvariantCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_speed);
        panel.Children.Add(_value);

        _speed.ValueChanged += (_, args) =>
        {
            if (_syncing) return;
            _syncing = true; _value.Text = args.NewValue.ToString("0.##", CultureInfo.InvariantCulture); _syncing = false;
        };
        _value.TextChanged += (_, _) =>
        {
            if (_syncing || !double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed)) return;
            _syncing = true; _speed.Value = Math.Clamp(typed, -720, 720); _syncing = false;
        };

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (label, value) in new[] { ("Stop", 0d), ("Slow ↻", 45d), ("One turn / sec ↻", 360d), ("Slow ↺", -45d) })
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            var speed = value;
            button.Click += (_, _) => { _speed.Value = speed; _value.Text = speed.ToString("0.##", CultureInfo.InvariantCulture); };
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Apply Spin", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        save.Click += (_, _) =>
        {
            if (!double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
            { MessageBox.Show(this, "Enter a number of degrees per second.", "Spin"); return; }
            Speed = Math.Clamp(typed, -3600, 3600);
            DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
