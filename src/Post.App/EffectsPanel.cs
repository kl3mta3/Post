using Microsoft.Win32;
using Post.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;

namespace Post.App;

/// <summary>Callbacks the effects browser uses to read and change the editor's state.</summary>
internal sealed record EffectHost(
    Action<EffectsPanel.EffectOptions> ApplyPreset,
    Action<VideoEffect, bool> Add,
    Action<Guid, bool> Remove,
    Action<Guid, bool, bool> SetEnabled,
    Func<bool, IReadOnlyList<VideoEffect>> List,
    Func<bool, string> Describe,
    Action<Guid, bool, VideoEffect> Update,
    Action<VideoEffect?, Guid?> SetPreview,
    Action<string, bool> AddStyle);

internal sealed class EffectsPanel : Grid
{
    internal sealed record EffectOptions(string Name, double DurationSeconds, double From, double To);
    private enum ParameterKind { None, Opacity, PositionX, Scale, Volume }
    private sealed record Preset(string Category, string Name, string Description, ParameterKind Parameters = ParameterKind.None,
        double Duration = 1, double From = 0, double To = 1, bool Available = true, VideoEffectKind? Effect = null, string? Style = null);
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
        new("Blur and Sharpen", "Blur", "Soften the picture with a gaussian blur.", Effect: VideoEffectKind.Blur),
        new("Blur and Sharpen", "Sharpen", "Add local contrast to bring out detail.", Effect: VideoEffectKind.Sharpen),
        new("Color and Image correction", "Color Correction", "Adjust brightness, contrast, saturation, gamma and hue.", Effect: VideoEffectKind.ColorCorrection),
        new("Color and Image correction", "LUT (.cube)", "Apply a 3D colour lookup table for a graded look.", Effect: VideoEffectKind.Lut),
        new("Stylize", "Vignette", "Darken the edges of the frame towards the corners.", Effect: VideoEffectKind.Vignette),
        .. LookStyles.All.Select(style => new Preset("Presets", style.Name, style.Description + " Applies as a LUT you can tweak in the Color Grading window.", Style: style.Name)),
    ];

    private readonly EffectHost _host;
    private readonly TreeView _tree = new();
    private readonly TextBlock _name = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 14) };
    private readonly StackPanel _options = new();
    private readonly StackPanel _applied = new();
    private readonly TextBlock _target = new() { Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly CheckBox _wholeTimeline = new() { Content = "Apply to the whole timeline instead of the selected clip", Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 12) };
    private TextBox _duration = new();
    private TextBox _from = new();
    private TextBox _to = new();
    private EffectParameterPanel? _parameters;
    private readonly CheckBox _preview = new() { Content = "Preview on the paused frame (nothing is saved until you add it)", Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 10) };
    private readonly Button _applyButton = new() { Content = "Apply to selected item", Padding = new Thickness(12, 7, 12, 7), IsEnabled = false };

    public EffectsPanel(EffectHost host)
    {
        _host = host; MinWidth = 460;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38));
        TextElement.SetForeground(this, Brushes.White);
        var root = new Grid { Margin = new Thickness(12) }; root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); root.ColumnDefinitions.Add(new ColumnDefinition());
        var browser = new DockPanel(); var heading = new TextBlock { Text = "EFFECTS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(4, 2, 0, 8) }; DockPanel.SetDock(heading, Dock.Top); browser.Children.Add(heading); browser.Children.Add(_tree);
        var border = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(48, 72, 99)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(4), Child = browser }; root.Children.Add(border);

        var details = new StackPanel { Margin = new Thickness(14, 8, 8, 8) };
        details.Children.Add(_name); details.Children.Add(_description); details.Children.Add(_target); details.Children.Add(_options); details.Children.Add(_applyButton);
        details.Children.Add(new TextBlock { Text = "APPLIED EFFECTS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(0, 18, 0, 7) });
        details.Children.Add(_applied);
        var scroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2); root.Children.Add(scroll); Children.Add(root);

        foreach (var category in Presets.Select(item => item.Category).Distinct())
        {
            var group = new TreeViewItem { Header = category, IsExpanded = category is "Motion" or "Transform, Distort and Perspective" };
            foreach (var preset in Presets.Where(item => item.Category == category)) group.Items.Add(new TreeViewItem { Header = preset.Name + (preset.Available ? "" : "  (coming soon)"), Tag = preset, IsEnabled = preset.Available });
            _tree.Items.Add(group);
        }
        _tree.SelectedItemChanged += (_, _) => SelectPreset((_tree.SelectedItem as TreeViewItem)?.Tag as Preset);
        _wholeTimeline.Checked += (_, _) => { UpdateApplyLabel(); RefreshApplied(); };
        _wholeTimeline.Unchecked += (_, _) => { UpdateApplyLabel(); RefreshApplied(); };
        _applyButton.Click += (_, _) => ApplySelected();
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshApplied(); else _host.SetPreview(null, null); };
        _preview.Checked += (_, _) => PushPreview();
        _preview.Unchecked += (_, _) => _host.SetPreview(null, null);
        Unloaded += (_, _) => _host.SetPreview(null, null);
        RefreshApplied();
    }

    /// <summary>Re-reads the effect stack; called when the editor's selection changes.</summary>
    public void RefreshApplied()
    {
        _applied.Children.Clear();
        UpdateTarget();
        var effects = _host.List(_wholeTimeline.IsChecked == true);
        if (effects.Count == 0)
        {
            _applied.Children.Add(new TextBlock { Text = "No effects yet. Pick one on the left and apply it.", Foreground = Theme.Hint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var effect in effects)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            var remove = new Button { Content = "Remove", Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(6, 0, 0, 0) };
            var edit = new Button { Content = "Edit", Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(6, 0, 0, 0) };
            var id = effect.Id; var whole = _wholeTimeline.IsChecked == true; var current = effect;
            remove.Click += (_, _) => { _host.Remove(id, whole); RefreshApplied(); };
            edit.Click += (_, _) => EditEffect(current, whole);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            var toggle = new CheckBox { IsChecked = effect.IsEnabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Enable or bypass this effect" };
            toggle.Checked += (_, _) => _host.SetEnabled(id, whole, true);
            toggle.Unchecked += (_, _) => _host.SetEnabled(id, whole, false);
            DockPanel.SetDock(toggle, Dock.Left); row.Children.Add(toggle);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = effect.DisplayName, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = effect.Summary, Foreground = Brushes.LightGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(11, 24, 44)), BorderBrush = new SolidColorBrush(Color.FromRgb(38, 61, 94)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 0, 6), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Click to edit this effect", Child = row };
            card.MouseLeftButtonUp += (_, args) => { if (args.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) EditEffect(current, whole); };
            _applied.Children.Add(card);
        }
    }

    private void UpdateTarget() => _target.Text = _host.Describe(_wholeTimeline.IsChecked == true);

    private void SelectPreset(Preset? preset)
    {
        _applyButton.Tag = preset; _applyButton.IsEnabled = preset?.Available == true;
        _name.Text = preset?.Name ?? "Choose an effect";
        _description.Text = preset?.Description ?? "Select an effect from the browser. Motion effects create editable keyframes; the others are added to the effect stack of the selected clip or of the whole timeline.";
        UpdateApplyLabel();
        BuildOptions(preset);
        RefreshApplied();
    }

    private void UpdateApplyLabel()
    {
        var preset = _applyButton.Tag as Preset;
        var stacked = preset?.Effect is not null || preset?.Style is not null;
        _applyButton.Content = !stacked ? "Apply to selected item" : _wholeTimeline.IsChecked == true ? "Add to timeline output" : "Add to selected clip";
    }

    /// <summary>Opens the editor for an applied effect and writes back what it returns.</summary>
    private void EditEffect(VideoEffect effect, bool wholeTimeline)
    {
        var draft = effect.Clone();
        var editor = new EffectEditorWindow(draft, _host.SetPreview, Window.GetWindow(this)!);
        if (editor.ShowDialog() != true) return;
        editor.Commit();
        _host.Update(effect.Id, wholeTimeline, draft);
        RefreshApplied();
    }

    private void PushPreview()
    {
        if (_preview.IsChecked != true || _parameters is null) { _host.SetPreview(null, null); return; }
        if ((_applyButton.Tag as Preset)?.Effect == VideoEffectKind.Lut && _parameters.LutPath is null) { _host.SetPreview(null, null); return; }
        _host.SetPreview(_parameters.Snapshot(), null);
    }

    private void BuildOptions(Preset? preset)
    {
        _options.Children.Clear(); _parameters = null; _host.SetPreview(null, null);
        if (preset?.Available != true) return;
        if (preset.Style is not null) { BuildStyleOptions(); return; }
        if (preset.Effect is { } kind) { BuildEffectOptions(kind); return; }
        if (preset.Parameters == ParameterKind.None) return;
        _options.Children.Add(Heading("EFFECT OPTIONS"));
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

    private void BuildStyleOptions()
    {
        _options.Children.Add(Heading("STYLE"));
        _options.Children.Add(new TextBlock
        {
            Text = "Adds the look as a LUT effect. Nothing else in the stack is replaced, so a style can sit on top of your own corrections.",
            Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10),
        });
        _options.Children.Add(_wholeTimeline);
    }

    private void BuildEffectOptions(VideoEffectKind kind)
    {
        _options.Children.Add(Heading("EFFECT OPTIONS"));
        _parameters = new EffectParameterPanel(kind, null, Window.GetWindow(this)!);
        _parameters.Changed += (_, _) => PushPreview();
        _options.Children.Add(_parameters);
        _preview.IsChecked = false;
        _options.Children.Add(_preview);
        _options.Children.Add(_wholeTimeline);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)), Margin = new Thickness(0, 0, 0, 7) };

    private static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 7) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 3) }); panel.Children.Add(control); return panel;
    }

    private void ApplySelected()
    {
        if (_applyButton.Tag is not Preset preset) return;
        if (preset.Style is { } style) { _host.AddStyle(style, _wholeTimeline.IsChecked == true); RefreshApplied(); return; }
        if (preset.Effect is { } kind) { ApplyEffect(kind); return; }
        if (!TryNumber(_duration.Text, out var duration) || duration <= 0 || !TryNumber(_from.Text, out var from) || !TryNumber(_to.Text, out var to))
        { MessageBox.Show(Window.GetWindow(this), "Enter valid numeric values. Duration must be greater than zero.", "Effect options", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        switch (preset.Parameters)
        {
            case ParameterKind.Opacity: from = Math.Clamp(from, 0, 1); to = Math.Clamp(to, 0, 1); break;
            case ParameterKind.Scale: from = Math.Clamp(from, .01, 20); to = Math.Clamp(to, .01, 20); break;
            case ParameterKind.Volume: from = Math.Clamp(from, 0, 4); to = Math.Clamp(to, 0, 4); break;
        }
        _from.Text = Number(from); _to.Text = Number(to); _host.ApplyPreset(new EffectOptions(preset.Name, duration, from, to));
    }

    private void ApplyEffect(VideoEffectKind kind)
    {
        if (_parameters is null || !_parameters.Validate(Window.GetWindow(this)!)) return;
        _host.Add(_parameters.Snapshot(), _wholeTimeline.IsChecked == true);
        // The effect is real now, so a pending preview would double up on it.
        _preview.IsChecked = false; _host.SetPreview(null, null);
        RefreshApplied();
    }

    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
