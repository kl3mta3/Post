using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// An eight-band graphic equalizer for the timeline's mixed audio. Changes are written
/// straight to the composition; the live preview player cannot filter audio, so the
/// result is heard in a rendered preview or the export.
/// </summary>
internal sealed class EqualizerWindow : Window
{
    private readonly AudioEqualizer _equalizer;
    private readonly Action _changed;
    private readonly List<Slider> _bandSliders = [];
    private readonly Slider _gain;
    private readonly CheckBox _enabled;
    private readonly TextBlock _note = new() { Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private bool _loading;

    private const string DefaultNote = "Applies to the whole timeline's audio. The preview player uses an equalized copy of each clip, rebuilt a moment after the sliders settle.";

    /// <summary>Shows progress while the equalized preview files are rebuilt.</summary>
    public void SetStatus(string text) => _note.Text = string.IsNullOrWhiteSpace(text) ? DefaultNote : text;

    public EqualizerWindow(AudioEqualizer equalizer, Action changed, Window owner)
    {
        _equalizer = equalizer; _changed = changed;
        Title = "Audio EQ"; Width = 700; Height = 470; MinWidth = 620; MinHeight = 420; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        if (_equalizer.Bands.Count == 0) _equalizer.CopyFrom(AudioEqualizer.Flat());
        _enabled = new CheckBox { Content = "Equalizer enabled", IsChecked = _equalizer.IsEnabled, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _gain = new Slider { Minimum = -12, Maximum = 12, Value = _equalizer.GainDb, Width = 170, VerticalAlignment = VerticalAlignment.Center };

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(_enabled);
        var presets = new ComboBox { Width = 180, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var preset in AudioEqualizer.Presets) presets.Items.Add(preset.Name);
        presets.SelectedIndex = 0;
        presets.SelectionChanged += (_, _) => ApplyPreset(presets.SelectedIndex);
        top.Children.Add(new TextBlock { Text = "Preset", Margin = new Thickness(18, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(presets);
        var gainReadout = new TextBlock { Text = Db(_gain.Value), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray, Width = 60 };
        top.Children.Add(new TextBlock { Text = "Output gain", Margin = new Thickness(18, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_gain); top.Children.Add(gainReadout);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var flat = new Button { Content = "Flatten", Padding = new Thickness(12, 6, 12, 6) };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 6, 12, 6) };
        footer.Children.Add(flat); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        _note.Text = DefaultNote;
        DockPanel.SetDock(_note, Dock.Bottom); root.Children.Add(_note);

        var bands = new UniformGrid { Rows = 1, Columns = _equalizer.Bands.Count };
        foreach (var band in _equalizer.Bands)
        {
            var column = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
            var readout = new TextBlock { Text = Db(band.GainDb), HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = -18, Maximum = 18, Value = Math.Clamp(band.GainDb, -18, 18),
                Height = 210, HorizontalAlignment = HorizontalAlignment.Center, TickFrequency = 3, IsSnapToTickEnabled = false,
                ToolTip = $"{Frequency(band.FrequencyHz)} — drag to cut or boost",
            };
            var current = band;
            slider.ValueChanged += (_, args) => { readout.Text = Db(args.NewValue); current.GainDb = args.NewValue; if (!_loading) Commit(); };
            _bandSliders.Add(slider);
            column.Children.Add(readout); column.Children.Add(slider);
            column.Children.Add(new TextBlock { Text = Frequency(band.FrequencyHz), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
            bands.Children.Add(column);
        }
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(6, 15, 30)), Padding = new Thickness(10), Child = bands });
        Content = root;

        _enabled.Checked += (_, _) => { _equalizer.IsEnabled = true; Commit(); };
        _enabled.Unchecked += (_, _) => { _equalizer.IsEnabled = false; Commit(); };
        _gain.ValueChanged += (_, args) => { gainReadout.Text = Db(args.NewValue); _equalizer.GainDb = args.NewValue; if (!_loading) Commit(); };
        flat.Click += (_, _) => ApplyPreset(0);
        close.Click += (_, _) => Close();
    }

    private void ApplyPreset(int index)
    {
        if (index < 0 || index >= AudioEqualizer.Presets.Count) return;
        var gains = AudioEqualizer.Presets[index].Gains;
        _loading = true;
        for (var i = 0; i < _bandSliders.Count && i < gains.Length; i++) _bandSliders[i].Value = gains[i];
        _loading = false;
        Commit();
    }

    private void Commit() => _changed();

    private static string Db(double value) => $"{(value >= 0 ? "+" : "")}{value.ToString("0.#", CultureInfo.InvariantCulture)} dB";
    private static string Frequency(double hertz) => hertz >= 1000 ? $"{(hertz / 1000).ToString("0.#", CultureInfo.InvariantCulture)}k" : hertz.ToString("0", CultureInfo.InvariantCulture);
}
