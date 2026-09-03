using Microsoft.Win32;
using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Post.App;

/// <summary>
/// Grades the current frame and can bake the result into a .cube LUT. The preview is
/// rendered through that same LUT, so what you see is exactly what the effect applies.
/// </summary>
internal sealed class ColorGradingWindow : Window
{
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, MinHeight = 200 };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Func<ColorGrade, Task<string?>> _render;
    private readonly Action<ColorGrade, bool> _addAsLut;
    private bool _rendering;
    private bool _pending;

    public ColorGradingWindow(Func<ColorGrade, Task<string?>> render, Action<ColorGrade, bool> addAsLut, Window owner)
    {
        _render = render; _addAsLut = addAsLut;
        Title = "Color Grading"; Width = 940; Height = 660; MinWidth = 720; MinHeight = 520; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var controls = new StackPanel { Margin = new Thickness(2, 2, 12, 2) };
        controls.Children.Add(Heading("GRADE"));
        AddSlider(controls, "Exposure", -1, 1, 0, "lifts or drops the whole picture");
        AddSlider(controls, "Contrast", 0, 3, 1, "1 is unchanged");
        AddSlider(controls, "Saturation", 0, 3, 1, "0 is greyscale");
        AddSlider(controls, "Gamma", .1, 3, 1, "midtone weighting");
        AddSlider(controls, "Hue", -180, 180, 0, "degrees of rotation");
        controls.Children.Add(Heading("WHITE BALANCE"));
        AddSlider(controls, "Temperature", -1, 1, 0, "cool to warm");
        AddSlider(controls, "Tint", -1, 1, 0, "green to magenta");
        controls.Children.Add(Heading("CHANNEL GAIN"));
        AddSlider(controls, "Red", 0, 2, 1, "");
        AddSlider(controls, "Green", 0, 2, 1, "");
        AddSlider(controls, "Blue", 0, 2, 1, "");
        var reset = new Button { Content = "Reset to neutral", Padding = new Thickness(11, 5, 11, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        reset.Click += (_, _) => Reset();
        controls.Children.Add(reset);
        var scroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);

        var right = new DockPanel { Margin = new Thickness(6, 2, 2, 2) };
        var actions = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveCube = new Button { Content = "Save .cube…", Padding = new Thickness(12, 6, 12, 6) };
        var addClip = new Button { Content = "Add to selected clip", Padding = new Thickness(12, 6, 12, 6) };
        var addTimeline = new Button { Content = "Add to timeline", Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(saveCube); buttons.Children.Add(addClip); buttons.Children.Add(addTimeline);
        actions.Children.Add(buttons); actions.Children.Add(_status);
        DockPanel.SetDock(actions, Dock.Bottom); right.Children.Add(actions);
        right.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(4, 10, 22)), Padding = new Thickness(6), Child = _preview });
        Grid.SetColumn(right, 1); root.Children.Add(right);
        Content = root;

        saveCube.Click += (_, _) => SaveCube();
        addClip.Click += (_, _) => { _addAsLut(CurrentGrade(), false); _status.Text = "Added to the selected clip as a LUT effect."; };
        addTimeline.Click += (_, _) => { _addAsLut(CurrentGrade(), true); _status.Text = "Added to the timeline output as a LUT effect."; };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RenderPreviewAsync(); };
        Loaded += async (_, _) => await RenderPreviewAsync();
    }

    public ColorGrade CurrentGrade() => new()
    {
        Brightness = Value("Exposure"), Contrast = Value("Contrast"), Saturation = Value("Saturation"),
        Gamma = Value("Gamma"), Hue = Value("Hue"), Temperature = Value("Temperature"), Tint = Value("Tint"),
        GainRed = Value("Red"), GainGreen = Value("Green"), GainBlue = Value("Blue"),
    };

    private void Reset()
    {
        foreach (var (label, slider) in _sliders)
            slider.Value = label switch { "Contrast" or "Saturation" or "Gamma" or "Red" or "Green" or "Blue" => 1, _ => 0 };
    }

    private void SaveCube()
    {
        var dialog = new SaveFileDialog { FileName = $"Post_Grade_{DateTime.Now:yyyyMMdd_HHmmss}.cube", Filter = "Colour lookup table|*.cube", DefaultExt = "cube", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { CurrentGrade().SaveCube(dialog.FileName, System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)); _status.Text = $"Saved {dialog.FileName}"; }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Color Grading", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task RenderPreviewAsync()
    {
        if (_rendering) { _pending = true; return; }
        _rendering = true;
        try
        {
            var path = await _render(CurrentGrade());
            if (path is null) { _status.Text = "Move the playhead over a video clip to preview the grade."; return; }
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit();
            _preview.Source = bitmap;
            if (_status.Text.StartsWith("Move the playhead")) _status.Text = "";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
        finally
        {
            _rendering = false;
            if (_pending) { _pending = false; _debounce.Stop(); _debounce.Start(); }
        }
    }

    private double Value(string label) => _sliders.TryGetValue(label, out var slider) ? slider.Value : 0;

    private void AddSlider(Panel host, string label, double minimum, double maximum, double value, string hint)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, Margin = new Thickness(0, 3, 0, 0) };
        var readout = new TextBlock { Text = Number(value), Foreground = Brushes.LightGray, FontSize = 11 };
        slider.ValueChanged += (_, args) => { readout.Text = Number(args.NewValue); _debounce.Stop(); _debounce.Start(); };
        _sliders[label] = slider;
        var header = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
        header.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        var panel = new StackPanel { Margin = new Thickness(0, 0, 4, 9) };
        panel.Children.Add(header); panel.Children.Add(slider);
        if (!string.IsNullOrEmpty(hint)) panel.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 11 });
        host.Children.Add(panel);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(0, 6, 0, 8) };
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
