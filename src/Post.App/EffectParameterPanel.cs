using Microsoft.Win32;
using Post.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// The parameter editor for one effect kind. Shared by the effects browser (creating
/// a new effect) and the editor dialog (changing one that is already applied).
/// </summary>
internal sealed class EffectParameterPanel : StackPanel
{
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly VideoEffectKind _kind;
    private readonly Window _owner;
    private string? _lutPath;

    /// <summary>Raised whenever a value changes, so callers can drive a live preview.</summary>
    public event EventHandler? Changed;

    public EffectParameterPanel(VideoEffectKind kind, VideoEffect? initial, Window owner)
    {
        _kind = kind; _owner = owner; _lutPath = initial?.FilePath;
        switch (kind)
        {
            case VideoEffectKind.Lut:
                BuildLutPicker();
                break;
            case VideoEffectKind.ColorCorrection:
                AddSlider("Brightness", -1, 1, initial?.Brightness ?? 0, "0 is unchanged");
                AddSlider("Contrast", 0, 3, initial?.Contrast ?? 1, "1 is unchanged");
                AddSlider("Saturation", 0, 3, initial?.Saturation ?? 1, "0 is greyscale");
                AddSlider("Gamma", .1, 3, initial?.Gamma ?? 1, "1 is unchanged");
                AddSlider("Hue", -180, 180, initial?.Hue ?? 0, "degrees of hue rotation");
                break;
            default:
                AddSlider("Amount", 0, 1, initial?.Amount ?? (kind == VideoEffectKind.Vignette ? .6 : .5), kind switch
                {
                    VideoEffectKind.Vignette => "how far the darkening reaches into the frame",
                    VideoEffectKind.Blur => "blur radius",
                    _ => "sharpening strength",
                });
                break;
        }
    }

    public string? LutPath => _lutPath;

    /// <summary>True when the panel holds everything the effect needs.</summary>
    public bool Validate(Window owner)
    {
        if (_kind != VideoEffectKind.Lut || (!string.IsNullOrWhiteSpace(_lutPath) && File.Exists(_lutPath))) return true;
        MessageBox.Show(owner, "Choose a .cube LUT file first.", "Effects", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>Copies the edited values onto an effect.</summary>
    public void WriteTo(VideoEffect effect)
    {
        effect.Kind = _kind;
        effect.Amount = Value("Amount", effect.Amount);
        effect.Brightness = Value("Brightness", effect.Brightness);
        effect.Contrast = Value("Contrast", effect.Contrast);
        effect.Saturation = Value("Saturation", effect.Saturation);
        effect.Gamma = Value("Gamma", effect.Gamma);
        effect.Hue = Value("Hue", effect.Hue);
        if (_kind == VideoEffectKind.Lut) effect.FilePath = _lutPath;
    }

    /// <summary>Builds a throwaway effect carrying the current values, for previewing.</summary>
    public VideoEffect Snapshot(Guid? id = null)
    {
        var effect = id is { } value ? new VideoEffect { Id = value } : new VideoEffect();
        WriteTo(effect);
        return effect;
    }

    private double Value(string label, double fallback) => _sliders.TryGetValue(label, out var slider) ? slider.Value : fallback;

    private void BuildLutPicker()
    {
        var path = new TextBox { Text = _lutPath ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var browse = new Button { Content = "Choose .cube file…", Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 10) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Colour lookup table|*.cube|All files|*.*", Title = "Choose a LUT" };
            if (dialog.ShowDialog(_owner) != true) return;
            _lutPath = dialog.FileName; path.Text = dialog.FileName; Changed?.Invoke(this, EventArgs.Empty);
        };
        Children.Add(Field("LUT file", path)); Children.Add(browse);
        Children.Add(new TextBlock { Text = "Any standard .cube LUT works. The Color Grading window can create one for you.", Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });
    }

    private void AddSlider(string label, double minimum, double maximum, double value, string hint)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), IsSnapToTickEnabled = false, Margin = new Thickness(0, 4, 0, 0) };
        var readout = new TextBlock { Text = Number(slider.Value), Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(2, 0, 0, 2) };
        slider.ValueChanged += (_, args) => { readout.Text = Number(args.NewValue); Changed?.Invoke(this, EventArgs.Empty); };
        _sliders[label] = slider;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 1) });
        panel.Children.Add(slider); panel.Children.Add(readout);
        panel.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 11, Margin = new Thickness(2, 0, 0, 0) });
        Children.Add(panel);
    }

    private static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 3) }); panel.Children.Add(control); return panel;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
