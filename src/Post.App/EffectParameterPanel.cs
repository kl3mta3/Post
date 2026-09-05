using Microsoft.Win32;
using Post.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        var library = new ListBox
        {
            MaxHeight = 132, Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), Margin = new Thickness(0, 4, 0, 4),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(library, ScrollBarVisibility.Auto);

        void Fill(string? select)
        {
            library.Items.Clear();
            // The ready-made looks are listed whether or not their LUT has been generated
            // yet; picking one writes it. Everything else is a LUT that was added.
            foreach (var style in LookStyles.All)
                library.Items.Add(new ListBoxItem
                {
                    Content = style.Name, Tag = LookStyles.CubePath(style, LutLibrary.Folder),
                    Foreground = Brushes.White, Padding = new Thickness(6, 3, 6, 3), ToolTip = style.Description,
                });
            foreach (var item in LutLibrary.All().Where(path => !LutLibrary.IsBuiltIn(path)))
                library.Items.Add(new ListBoxItem { Content = LutLibrary.DisplayName(item), Tag = item, Foreground = Brushes.White, Padding = new Thickness(6, 3, 6, 3), ToolTip = item });
            library.SelectedItem = library.Items.Cast<ListBoxItem>().FirstOrDefault(item => (string)item.Tag! == select);
        }

        void Choose(string chosen)
        {
            // A built-in look is only written out when it is actually asked for.
            if (!File.Exists(chosen) && LookStyles.StyleForFile(chosen) is { } style)
            {
                try { chosen = LookStyles.EnsureCube(style, LutLibrary.Folder); }
                catch (Exception exception) { MessageBox.Show(_owner, exception.Message, "Effects", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            }
            _lutPath = chosen; path.Text = chosen;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        library.SelectionChanged += (_, _) =>
        {
            if (library.SelectedItem is ListBoxItem { Tag: string chosen } && chosen != _lutPath) Choose(chosen);
        };

        var browse = new Button { Content = "Add .cube file…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Color lookup table|*.cube|All files|*.*", Title = "Add a LUT" };
            if (dialog.ShowDialog(_owner) != true) return;
            // Kept alongside the saved grades, so it is still there next time.
            var kept = LutLibrary.Keep(dialog.FileName);
            Choose(kept); Fill(kept);
        };

        var remove = new Button { Content = "Remove", Padding = new Thickness(10, 4, 10, 4) };
        remove.Click += (_, _) =>
        {
            if (library.SelectedItem is not ListBoxItem { Tag: string chosen }) return;
            if (LookStyles.StyleForFile(chosen) is not null)
            { MessageBox.Show(_owner, "The ready-made looks are always available and cannot be removed.", "Effects", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (MessageBox.Show(_owner, $"Delete {LutLibrary.DisplayName(chosen)} from your LUTs?", "Effects", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!LutLibrary.Forget(chosen)) return;
            if (_lutPath == chosen) { _lutPath = null; path.Text = ""; Changed?.Invoke(this, EventArgs.Empty); }
            Fill(_lutPath);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(browse); buttons.Children.Add(remove);

        Children.Add(Field("LUT file", path));
        Children.Add(new TextBlock { Text = "Looks and LUTs", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 0, 0) });
        Children.Add(library);
        Children.Add(buttons);
        Children.Add(new TextBlock { Text = "Any standard .cube LUT works, and one you add is copied here so it stays. The Color Grading window saves its grades to the same place.", Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });
        Fill(_lutPath);
    }

    private void AddSlider(string label, double minimum, double maximum, double value, string hint)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        // A slider alone cannot be set precisely. The number is typable, and the arrows
        // step it by a hundredth of the range for the last bit of adjustment.
        var box = new TextBox { Text = Number(slider.Value), Width = 64, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = $"Type a value between {Number(minimum)} and {Number(maximum)}" };
        var step = Math.Max((maximum - minimum) / 100, .001);
        var syncing = false;

        slider.ValueChanged += (_, args) =>
        {
            if (!syncing) { syncing = true; box.Text = Number(args.NewValue); syncing = false; }
            Changed?.Invoke(this, EventArgs.Empty);
        };
        box.TextChanged += (_, _) =>
        {
            if (syncing || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed)) return;
            syncing = true; slider.Value = Math.Clamp(typed, minimum, maximum); syncing = false;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        // Typing something unusable leaves the slider where it was; correct it on the way out.
        box.LostFocus += (_, _) => { syncing = true; box.Text = Number(slider.Value); syncing = false; };

        var steppers = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        steppers.Children.Add(Stepper("▲", () => slider.Value = Math.Clamp(slider.Value + step, minimum, maximum)));
        steppers.Children.Add(Stepper("▼", () => slider.Value = Math.Clamp(slider.Value - step, minimum, maximum)));

        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 1); Grid.SetColumn(steppers, 2);
        row.Children.Add(slider); row.Children.Add(box); row.Children.Add(steppers);

        _sliders[label] = slider;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 1) });
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 11, Margin = new Thickness(2, 2, 0, 0) });
        Children.Add(panel);
    }

    /// <summary>One of the small arrows beside a value, repeating while it is held.</summary>
    private static RepeatButton Stepper(string glyph, Action nudge)
    {
        var button = new RepeatButton
        {
            Content = glyph, FontSize = 7, Width = 18, Height = 13, Padding = new Thickness(0),
            Delay = 400, Interval = 60, VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => nudge();
        return button;
    }

    private static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 3) }); panel.Children.Add(control); return panel;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
