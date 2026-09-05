using Microsoft.Win32;
using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Grading, as a panel rather than a window with a photograph in it.
///
/// It used to bake a .cube and have ffmpeg render one still frame into a preview box. The
/// grade now goes straight to the preview shader, which samples a table baked with the very
/// maths the export uses — so the picture on the timeline is the preview, moving, and the
/// panel is only the controls.
///
/// The sections are tabs because there are four wheels and a dozen sliders, and a single
/// column of them was a scroll bar with a grading tool somewhere inside it.
/// </summary>
internal sealed class ColorGradingPanel : Grid
{
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly ColorWheel _lift = new("Shadows · Lift", -.5, .5, 0);
    private readonly ColorWheel _gamma = new("Midtones · Gamma", .25, 2.5, 1);
    private readonly ColorWheel _gain = new("Highlights · Gain", 0, 2, 1);
    private readonly ColorWheel _offset = new("Offset", -.5, .5, 0);
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private readonly Action<ColorGrade?> _live;
    private readonly Action<ColorGrade, bool> _addAsLut;

    public ColorGradingPanel(Action<ColorGrade?> live, Action<ColorGrade, bool> addAsLut)
    {
        _live = live; _addAsLut = addAsLut;

        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Margin = new Thickness(10);

        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Wheels", Content = Scroll(BuildWheels()) });
        tabs.Items.Add(new TabItem { Header = "Basics", Content = Scroll(BuildBasics()) });
        tabs.Items.Add(new TabItem { Header = "White Balance", Content = Scroll(BuildWhiteBalance()) });
        tabs.Items.Add(new TabItem { Header = "Channel Gain", Content = Scroll(BuildChannelGain()) });
        Children.Add(tabs);

        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var reset = new Button { Content = "Reset to neutral", Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 8, 0) };
        var saveCube = new Button { Content = "Save .cube…", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var addClip = new Button { Content = "Add to selected clip", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var addTimeline = new Button { Content = "Add to timeline", Padding = new Thickness(12, 5, 12, 5) };

        reset.Click += (_, _) => Reset();
        saveCube.Click += (_, _) => SaveCube();
        addClip.Click += (_, _) => { _addAsLut(CurrentGrade(), false); _status.Text = "Added to the selected clip as a LUT effect."; };
        addTimeline.Click += (_, _) => { _addAsLut(CurrentGrade(), true); _status.Text = "Added to the timeline output as a LUT effect."; };

        buttons.Children.Add(reset); buttons.Children.Add(saveCube); buttons.Children.Add(addClip); buttons.Children.Add(addTimeline);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);
        SetRow(footer, 1); Children.Add(footer);

        foreach (var wheel in Wheels) wheel.Changed += Live;
        _status.Text = "The grade shows on the preview as you work. Add it to a clip or the timeline to keep it.";
    }

    private ColorWheel[] Wheels => [_lift, _gamma, _gain, _offset];

    /// <summary>Everything the wheels and sliders currently add up to.</summary>
    public ColorGrade CurrentGrade()
    {
        var lift = _lift.Offsets;
        var gamma = _gamma.Offsets;
        var gain = _gain.Offsets;
        var offset = _offset.Offsets;

        return new ColorGrade
        {
            Brightness = Value("Exposure") + _offset.Luma,
            Contrast = Value("Contrast"),
            Saturation = Value("Saturation"),
            Gamma = Value("Gamma"),
            Hue = Value("Hue"),
            Temperature = Value("Temperature"),
            Tint = Value("Tint"),

            // The wheel's tint rides on top of the channel gain sliders, so the two agree
            // rather than fighting over the same three numbers.
            GainRed = Value("Red") * _gain.Luma + gain.R,
            GainGreen = Value("Green") * _gain.Luma + gain.G,
            GainBlue = Value("Blue") * _gain.Luma + gain.B,

            LiftRed = lift.R + _lift.Luma,
            LiftGreen = lift.G + _lift.Luma,
            LiftBlue = lift.B + _lift.Luma,

            GammaRed = _gamma.Luma + gamma.R,
            GammaGreen = _gamma.Luma + gamma.G,
            GammaBlue = _gamma.Luma + gamma.B,

            OffsetRed = offset.R,
            OffsetGreen = offset.G,
            OffsetBlue = offset.B,
        };
    }

    /// <summary>Stops shading the preview — for when the panel is closed.</summary>
    public void StopPreview() => _live(null);

    private void Live() => _live(CurrentGrade());

    // ---- the tabs -----------------------------------------------------------

    private UIElement BuildWheels()
    {
        var panel = new StackPanel();
        panel.Children.Add(Note("Drag a puck out from the middle to tint that range; the slider under each is how bright it is. Right-click a wheel, or its reset, to put it back."));

        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var wheel in Wheels) row.Children.Add(wheel);
        panel.Children.Add(row);
        return panel;
    }

    private UIElement BuildBasics()
    {
        var panel = new StackPanel();
        AddSlider(panel, "Exposure", -1, 1, 0, "lifts or drops the whole picture");
        AddSlider(panel, "Contrast", 0, 3, 1, "1 is unchanged");
        AddSlider(panel, "Saturation", 0, 3, 1, "0 is greyscale");
        AddSlider(panel, "Gamma", .1, 3, 1, "midtone weighting");
        AddSlider(panel, "Hue", -180, 180, 0, "degrees of rotation");
        return panel;
    }

    private UIElement BuildWhiteBalance()
    {
        var panel = new StackPanel();
        panel.Children.Add(Note("What the camera should have been set to."));
        AddSlider(panel, "Temperature", -1, 1, 0, "cool to warm");
        AddSlider(panel, "Tint", -1, 1, 0, "green to magenta");
        return panel;
    }

    private UIElement BuildChannelGain()
    {
        var panel = new StackPanel();
        panel.Children.Add(Note("Straight multiplication, before anything else. The Highlights wheel rides on top of these."));
        AddSlider(panel, "Red", 0, 2, 1, "");
        AddSlider(panel, "Green", 0, 2, 1, "");
        AddSlider(panel, "Blue", 0, 2, 1, "");
        return panel;
    }

    // ---- the rest -----------------------------------------------------------

    private void Reset()
    {
        foreach (var (label, slider) in _sliders)
            slider.Value = label switch { "Contrast" or "Saturation" or "Gamma" or "Red" or "Green" or "Blue" => 1, _ => 0 };
        _lift.Reset(0); _gamma.Reset(1); _gain.Reset(1); _offset.Reset(0);
        Live();
    }

    private void SaveCube()
    {
        var dialog = new SaveFileDialog { FileName = $"Post_Grade_{DateTime.Now:yyyyMMdd_HHmmss}.cube", Filter = "Color lookup table|*.cube", DefaultExt = "cube", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            CurrentGrade().SaveCube(dialog.FileName, System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
            _status.Text = $"Saved {dialog.FileName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Color Grading", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private double Value(string label) => _sliders.TryGetValue(label, out var slider) ? slider.Value : 0;

    private void AddSlider(Panel host, string label, double minimum, double maximum, double value, string hint)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, Margin = new Thickness(0, 3, 0, 0) };
        var readout = new TextBlock { Text = Number(value), Foreground = Brushes.LightGray, FontSize = 11 };
        slider.ValueChanged += (_, args) => { readout.Text = Number(args.NewValue); Live(); };
        _sliders[label] = slider;

        var header = new DockPanel();
        DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
        header.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });

        var panel = new StackPanel { Margin = new Thickness(0, 0, 4, 9) };
        panel.Children.Add(header); panel.Children.Add(slider);
        if (!string.IsNullOrEmpty(hint)) panel.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 11 });
        host.Children.Add(panel);
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = new Border { Padding = new Thickness(10, 8, 10, 8), Child = content },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
    };

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
