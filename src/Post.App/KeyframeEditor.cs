using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Post.App;

internal sealed class KeyframeEditor : Window
{
    private sealed record InterpolationChoice(string Name, KeyframeInterpolation Value) { public override string ToString() => Name; }
    private sealed record PropertyChoice(string Name, KeyframeProperty Value) { public override string ToString() => Name; }
    private readonly ICollection<AnimationKeyframe> _keyframes;
    private readonly TimeSpan _duration;
    private readonly Func<KeyframeProperty, double> _fallback;
    private readonly KeyframeProperty[] _properties;
    private readonly Action _changed;
    private readonly Action<TimeSpan> _caretChanged;
    private readonly Func<TimeSpan> _playbackOffset;
    private readonly Func<bool> _isPlaying;
    private readonly TimeSpan _frameDuration;
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ComboBox _property = new();
    private readonly ComboBox _interpolation = new();
    private readonly TextBox _value = new();
    private readonly TextBlock _range = new() { Foreground = Brushes.LightGray };
    private readonly TextBlock _caretText = new() { FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
    private readonly ListBox _list = new() { MinHeight = 115, Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(55, 82, 116)) };
    private readonly Style _listItemStyle = CreateListItemStyle();
    private readonly Canvas _timeline = new() { Height = 210, Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)), ClipToBounds = true };
    private TimeSpan _offset;
    public bool Changed { get; private set; }

    public KeyframeEditor(ICollection<AnimationKeyframe> keyframes, TimeSpan offset, TimeSpan duration,
        IEnumerable<KeyframeProperty> properties, Func<KeyframeProperty, double> fallback,
        string targetName, Action changed, Action<TimeSpan> caretChanged, Func<TimeSpan> playbackOffset, Func<bool> isPlaying, TimeSpan frameDuration)
    {
        _keyframes = keyframes; _offset = Clamp(offset); _duration = duration; _fallback = fallback;
        _properties = properties.ToArray(); _changed = changed; _caretChanged = caretChanged;
        _playbackOffset = playbackOffset; _isPlaying = isPlaying; _frameDuration = frameDuration > TimeSpan.Zero ? frameDuration : TimeSpan.FromSeconds(1d / 30);
        Title = $"Keyframe Timeline — {targetName}"; Width = 760; Height = 650; MinWidth = 600; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;
        foreach (var property in _properties) _property.Items.Add(new PropertyChoice(Friendly(property), property));
        _interpolation.Items.Add(new InterpolationChoice("Linear", KeyframeInterpolation.Linear));
        _interpolation.Items.Add(new InterpolationChoice("Hold value", KeyframeInterpolation.Discrete));
        _interpolation.Items.Add(new InterpolationChoice("Smooth", KeyframeInterpolation.Smooth));
        _property.SelectedIndex = 0; _interpolation.SelectedIndex = 0;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(220) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var intro = new StackPanel(); intro.Children.Add(_caretText);
        intro.Children.Add(new TextBlock { Text = "Click the timeline to position the white edit caret. Mouse wheel or Left/Right steps one frame. Diamonds hold their entered value; interpolation controls the change to the next diamond.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 9) });
        root.Children.Add(intro);
        var timelineBorder = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(55, 82, 116)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = _timeline };
        Grid.SetRow(timelineBorder, 1); root.Children.Add(timelineBorder);
        var editor = new Grid { Margin = new Thickness(0, 10, 0, 8) };
        editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddEditorField(editor, "Property", _property, 0);
        var valuePanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; valuePanel.Children.Add(Label("Value")); valuePanel.Children.Add(_value); valuePanel.Children.Add(_range); Grid.SetColumn(valuePanel, 1); editor.Children.Add(valuePanel);
        AddEditorField(editor, "Interpolation after keyframe", _interpolation, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var set = new Button { Content = "◆ Add / Update", MinWidth = 112, Padding = new Thickness(8, 5, 8, 5) }; set.Click += Set_Click;
        var remove = new Button { Content = "Remove", MinWidth = 72, Padding = new Thickness(8, 5, 8, 5) }; remove.Click += Remove_Click;
        buttons.Children.Add(set); buttons.Children.Add(remove); Grid.SetColumn(buttons, 3); editor.Children.Add(buttons);
        Grid.SetRow(editor, 2); root.Children.Add(editor);
        var listPanel = new DockPanel(); var heading = new TextBlock { Text = "Keyframes on selected item", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(heading, Dock.Top); listPanel.Children.Add(heading); listPanel.Children.Add(_list); Grid.SetRow(listPanel, 3); root.Children.Add(listPanel);
        var close = new Button { Content = "Close", Width = 95, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) }; close.Click += (_, _) => Close(); Grid.SetRow(close, 4); root.Children.Add(close);
        Content = root;
        _property.SelectionChanged += (_, _) => { RefreshEditor(); DrawTimeline(); };
        _list.SelectionChanged += List_SelectionChanged; _timeline.MouseLeftButtonDown += Timeline_MouseLeftButtonDown; _timeline.SizeChanged += (_, _) => DrawTimeline();
        PreviewMouseWheel += KeyframeEditor_PreviewMouseWheel;
        PreviewKeyDown += KeyframeEditor_PreviewKeyDown;
        _playbackTimer.Tick += (_, _) => { if (_isPlaying()) DrawTimeline(); };
        Closed += (_, _) => _playbackTimer.Stop();
        _playbackTimer.Start(); RefreshAll();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
    private static void AddEditorField(Grid parent, string label, Control control, int column) { var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; panel.Children.Add(Label(label)); panel.Children.Add(control); Grid.SetColumn(panel, column); parent.Children.Add(panel); }
    private TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value > _duration ? _duration : value;
    private void SetCaret(TimeSpan value, bool notify = true) { _offset = Clamp(value); if (notify) _caretChanged(_offset); RefreshEditor(); DrawTimeline(); }
    private void RefreshAll() { RefreshEditor(); RefreshList(); DrawTimeline(); }

    private void RefreshEditor()
    {
        _caretText.Text = $"EDIT CARET  {TimeText.Format(_offset)}   /   {TimeText.Format(_duration)}";
        if (SelectedProperty is not { } property) return;
        var existing = AtCaret(property);
        _value.Text = (existing?.Value ?? KeyframeEvaluator.Evaluate(_keyframes, property, _offset, _fallback(property))).ToString("0.###", CultureInfo.InvariantCulture);
        if (existing is not null) SelectInterpolation(existing.Interpolation);
        _range.Text = property switch
        {
            KeyframeProperty.PositionX or KeyframeProperty.PositionY => "Negative moves left/up; positive moves right/down",
            KeyframeProperty.Scale => "Scale: 0.01 to 20", KeyframeProperty.Opacity => "Opacity: 0 to 1",
            KeyframeProperty.Volume => "Volume: 0 to 4", _ => ""
        };
    }

    private void SelectInterpolation(KeyframeInterpolation value) => _interpolation.SelectedItem = _interpolation.Items.Cast<InterpolationChoice>().First(item => item.Value == value);
    private void Set_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProperty is not { } property || !double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        { MessageBox.Show(this, "Enter a numeric keyframe value.", "Keyframes"); return; }
        value = property switch { KeyframeProperty.Scale => Math.Clamp(value, .01, 20), KeyframeProperty.Opacity => Math.Clamp(value, 0, 1), KeyframeProperty.Volume => Math.Clamp(value, 0, 4), _ => value };
        var interpolation = (_interpolation.SelectedItem as InterpolationChoice)?.Value ?? KeyframeInterpolation.Linear;
        KeyframeEvaluator.UpsertWithBaseline(_keyframes, property, _offset, value, interpolation, _fallback(property), _duration); Changed = true; _changed(); RefreshAll();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProperty is not { } property || AtCaret(property) is not { } existing) return;
        _keyframes.Remove(existing); Changed = true; _changed(); RefreshAll();
    }
    private AnimationKeyframe? AtCaret(KeyframeProperty property) => _keyframes.FirstOrDefault(item => item.Property == property && (item.Offset - _offset).Duration() <= TimeSpan.FromMilliseconds(1));
    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var item in _keyframes.OrderBy(item => item.Offset).ThenBy(item => item.Property))
            _list.Items.Add(new ListBoxItem { Content = $"{TimeText.Format(item.Offset)}   {Friendly(item.Property)}: {item.Value:0.###}   {Friendly(item.Interpolation)}", Tag = item, Style = _listItemStyle });
    }
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: AnimationKeyframe item }) return;
        SelectProperty(item.Property); SelectInterpolation(item.Interpolation); SetCaret(item.Offset); _value.Text = item.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(_timeline); var width = Math.Max(1, _timeline.ActualWidth - 110);
        var row = Math.Clamp((int)((point.Y - 26) / Math.Max(1, (_timeline.ActualHeight - 28) / _properties.Length)), 0, _properties.Length - 1);
        SelectProperty(_properties[row]); SetCaret(TimeSpan.FromSeconds(Math.Clamp((point.X - 105) / width, 0, 1) * _duration.TotalSeconds));
    }
    private void DrawTimeline()
    {
        _timeline.Children.Clear(); if (_properties.Length == 0 || _timeline.ActualWidth < 20) return;
        var left = 105d; var width = Math.Max(1, _timeline.ActualWidth - left - 5); var height = Math.Max(1, (_timeline.ActualHeight - 28) / _properties.Length);
        for (var tick = 0; tick <= 10; tick++) { var x = left + width * tick / 10; _timeline.Children.Add(new Line { X1 = x, X2 = x, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = new SolidColorBrush(Color.FromRgb(27, 47, 70)), StrokeThickness = 1 }); var label = new TextBlock { Text = TimeText.Format(TimeSpan.FromSeconds(_duration.TotalSeconds * tick / 10)), FontSize = 9, Foreground = Brushes.LightGray }; Canvas.SetLeft(label, x - 18); Canvas.SetTop(label, 3); _timeline.Children.Add(label); }
        for (var row = 0; row < _properties.Length; row++)
        {
            var y = 28 + row * height; var selected = SelectedProperty == _properties[row];
            var band = new Rectangle { Width = _timeline.ActualWidth, Height = height, Fill = new SolidColorBrush(selected ? Color.FromArgb(70, 35, 91, 125) : Color.FromArgb(row % 2 == 0 ? (byte)28 : (byte)12, 60, 100, 135)) }; Canvas.SetTop(band, y); _timeline.Children.Add(band);
            var label = new TextBlock { Text = Friendly(_properties[row]), FontSize = 11, Foreground = selected ? Brushes.White : Brushes.LightGray, FontWeight = selected ? FontWeights.Bold : FontWeights.Normal }; Canvas.SetLeft(label, 8); Canvas.SetTop(label, y + height / 2 - 8); _timeline.Children.Add(label);
            foreach (var keyframe in _keyframes.Where(item => item.Property == _properties[row]))
            { var x = left + Ratio(keyframe.Offset) * width; var diamond = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(255, 211, 77)), Stroke = Brushes.Black, StrokeThickness = 1, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45), ToolTip = $"{keyframe.Value:0.###} • {Friendly(keyframe.Interpolation)}" }; Canvas.SetLeft(diamond, x - 5); Canvas.SetTop(diamond, y + height / 2 - 5); _timeline.Children.Add(diamond); }
        }
        var playbackX = left + Ratio(_playbackOffset()) * width;
        var playback = new Line { X1 = playbackX, X2 = playbackX, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = new SolidColorBrush(Color.FromRgb(244, 63, 94)), StrokeThickness = 2.5, IsHitTestVisible = false };
        Panel.SetZIndex(playback, 49); _timeline.Children.Add(playback);
        var caretX = left + Ratio(_offset) * width; var caret = new Line { X1 = caretX, X2 = caretX, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = Brushes.White, StrokeThickness = 2 }; Panel.SetZIndex(caret, 50); _timeline.Children.Add(caret);
    }
    private double Ratio(TimeSpan time) => _duration <= TimeSpan.Zero ? 0 : Math.Clamp(time.TotalSeconds / _duration.TotalSeconds, 0, 1);
    private KeyframeProperty? SelectedProperty => (_property.SelectedItem as PropertyChoice)?.Value;
    private void SelectProperty(KeyframeProperty property) => _property.SelectedItem = _property.Items.Cast<PropertyChoice>().First(item => item.Value == property);
    private void KeyframeEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        SetCaret(_offset + (e.Key == Key.Right ? _frameDuration : -_frameDuration)); e.Handled = true;
    }
    private void KeyframeEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetCaret(_offset + (e.Delta > 0 ? _frameDuration : -_frameDuration)); e.Handled = true;
    }
    private static string Friendly(KeyframeProperty property) => property switch { KeyframeProperty.PositionX => "Position X", KeyframeProperty.PositionY => "Position Y", _ => property.ToString() };
    private static string Friendly(KeyframeInterpolation interpolation) => interpolation == KeyframeInterpolation.Discrete ? "Hold value" : interpolation.ToString();
    private static Style CreateListItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(5, 13, 27))));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(26, 54, 84)))); style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(23, 75, 104))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); style.Triggers.Add(selected);
        return style;
    }
}
