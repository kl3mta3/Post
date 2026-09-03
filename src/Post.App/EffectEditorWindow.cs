using Post.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>Edits one already-applied effect, with an optional live preview.</summary>
internal sealed class EffectEditorWindow : Window
{
    private readonly EffectParameterPanel _panel;
    private readonly VideoEffect _effect;
    private readonly Action<VideoEffect?, Guid?> _setPreview;
    private readonly CheckBox _preview = new() { Content = "Preview on the paused frame", Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 12) };

    public EffectEditorWindow(VideoEffect effect, Action<VideoEffect?, Guid?> setPreview, Window owner)
    {
        _effect = effect; _setPreview = setPreview;
        Title = $"Edit {effect.DisplayName}"; Width = 430; Height = 520; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        _panel = new EffectParameterPanel(effect.Kind, effect, this);
        _panel.Changed += (_, _) => UpdatePreview();
        _preview.Checked += (_, _) => UpdatePreview();
        _preview.Unchecked += (_, _) => _setPreview(null, null);

        var save = new Button { Content = "Save changes", Padding = new Thickness(12, 6, 12, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(save);

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = effect.DisplayName, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(_panel); body.Children.Add(_preview); body.Children.Add(buttons);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        save.Click += (_, _) => { if (!_panel.Validate(this)) return; DialogResult = true; };
        Closed += (_, _) => _setPreview(null, null);
    }

    /// <summary>Writes the edited values onto the original effect.</summary>
    public void Commit() => _panel.WriteTo(_effect);

    // While editing, the saved version of this effect is suppressed in the preview so
    // the pending values are not applied on top of it.
    private void UpdatePreview() => _setPreview(_preview.IsChecked == true ? _panel.Snapshot(_effect.Id) : null, _preview.IsChecked == true ? _effect.Id : null);
}
