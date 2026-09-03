using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Post.App;

/// <summary>Everything the timeline needs to know about the item it is editing.</summary>
internal sealed record KeyframeBinding(
    ICollection<AnimationKeyframe> Keyframes, TimeSpan Offset, TimeSpan Duration,
    IReadOnlyList<KeyframeProperty> Properties, Func<KeyframeProperty, double> Fallback, string TargetName,
    Action Changed, Action<TimeSpan> CaretChanged, Func<TimeSpan> PlaybackOffset, Func<bool> IsPlaying, TimeSpan FrameDuration);

/// <summary>
/// The keyframe timeline itself, as a control rather than a window, so it can sit
/// pinned above the layer stack and still be hosted in a window when more room helps.
/// It is bound to whatever is selected and rebound as the selection changes.
/// </summary>
internal sealed class KeyframeTimeline : Grid
{
    private sealed record InterpolationChoice(string Name, KeyframeInterpolation Value) { public override string ToString() => Name; }
    private sealed record PropertyChoice(string Name, KeyframeProperty Value) { public override string ToString() => Name; }

    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly ComboBox _property = new() { MinWidth = 110 };
    private readonly ComboBox _interpolation = new() { MinWidth = 110 };
    private readonly TextBox _value = new() { MinWidth = 70 };
    private readonly TextBlock _range = new() { Foreground = Brushes.LightGray, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _caretText = new() { FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
    private readonly TextBlock _targetText = new() { Foreground = Brushes.LightGray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new()
    {
        Text = "Select a clip, audio item, or overlay to keyframe it.",
        Foreground = Theme.Hint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ListBox _list = new() { MinHeight = 115, Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(55, 82, 116)) };
    private readonly Style _listItemStyle = CreateListItemStyle();
    private readonly Canvas _timeline = new() { Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)), ClipToBounds = true };
    private readonly Grid _controls = new();
    private KeyframeBinding? _binding;
    private TimeSpan _offset;

    public bool Changed { get; private set; }

    public KeyframeTimeline(bool showList)
    {
        _interpolation.Items.Add(new InterpolationChoice("Linear", KeyframeInterpolation.Linear));
        _interpolation.Items.Add(new InterpolationChoice("Hold value", KeyframeInterpolation.Discrete));
        _interpolation.Items.Add(new InterpolationChoice("Smooth", KeyframeInterpolation.Smooth));
        _interpolation.SelectedIndex = 0;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 74 });
        if (showList) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // One row of controls, so the strip stays shallow enough to live above the layers.
        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 5; i++) _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _controls.Margin = new Thickness(0, 0, 0, 6);

        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(_caretText); heading.Children.Add(_targetText);
        _controls.Children.Add(heading);
        AddField("Property", _property, 1);
        var valuePanel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        valuePanel.Children.Add(Label("Value")); valuePanel.Children.Add(_value); valuePanel.Children.Add(_range);
        SetColumn(valuePanel, 2); _controls.Children.Add(valuePanel);
        AddField("Interpolation after keyframe", _interpolation, 3);

        var set = new Button { Content = "◆ Add / Update", MinWidth = 112, Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 0) };
        var remove = new Button { Content = "Remove", MinWidth = 72, Padding = new Thickness(8, 5, 8, 5), VerticalAlignment = VerticalAlignment.Bottom };
        set.Click += Set_Click; remove.Click += Remove_Click;
        SetColumn(set, 4); SetColumn(remove, 5); _controls.Children.Add(set); _controls.Children.Add(remove);
        Children.Add(_controls);

        var timelineBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(55, 82, 116)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Child = _timeline,
        };
        SetRow(timelineBorder, 1); Children.Add(timelineBorder);
        SetRow(_empty, 1); Children.Add(_empty);

        if (showList)
        {
            var listPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var listHeading = new TextBlock { Text = "Keyframes on selected item", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(listHeading, Dock.Top); listPanel.Children.Add(listHeading); listPanel.Children.Add(_list);
            SetRow(listPanel, 2); Children.Add(listPanel);
        }

        _property.SelectionChanged += (_, _) => { RefreshEditor(); DrawTimeline(); };
        _list.SelectionChanged += List_SelectionChanged;
        _timeline.MouseLeftButtonDown += Timeline_MouseLeftButtonDown;
        _timeline.SizeChanged += (_, _) => DrawTimeline();
        PreviewMouseWheel += Timeline_PreviewMouseWheel;
        PreviewKeyDown += Timeline_PreviewKeyDown;
        _playbackTimer.Tick += (_, _) => { if (_binding?.IsPlaying() == true) DrawTimeline(); };
        Loaded += (_, _) => _playbackTimer.Start();
        Unloaded += (_, _) => _playbackTimer.Stop();
        Unbind();
    }

    /// <summary>Points the timeline at an item, replacing whatever it was showing.</summary>
    public void Bind(KeyframeBinding binding)
    {
        _binding = binding;
        _offset = Clamp(binding.Offset);
        _property.SelectionChanged -= Property_Changed;
        _property.Items.Clear();
        foreach (var property in binding.Properties) _property.Items.Add(new PropertyChoice(Friendly(property), property));
        if (_property.Items.Count > 0) _property.SelectedIndex = 0;
        _property.SelectionChanged += Property_Changed;
        _targetText.Text = binding.TargetName;
        SetContentVisible(true);
        RefreshAll();
    }

    /// <summary>Nothing is selected, so there is nothing to keyframe.</summary>
    public void Unbind()
    {
        _binding = null;
        _list.Items.Clear();
        _timeline.Children.Clear();
        _caretText.Text = "EDIT CARET  —";
        _targetText.Text = "";
        SetContentVisible(false);
    }

    /// <summary>Moves the caret from outside, when the edit position changes elsewhere.</summary>
    public void SetCaretFromOutside(TimeSpan offset)
    {
        if (_binding is null) return;
        _offset = Clamp(offset);
        RefreshEditor(); DrawTimeline();
    }

    public void Refresh() { if (_binding is not null) RefreshAll(); }

    private void SetContentVisible(bool bound)
    {
        _empty.Visibility = bound ? Visibility.Collapsed : Visibility.Visible;
        _controls.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
        _timeline.Visibility = bound ? Visibility.Visible : Visibility.Hidden;
        _list.Visibility = bound ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Property_Changed(object? sender, SelectionChangedEventArgs e) { RefreshEditor(); DrawTimeline(); }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
    private void AddField(string label, Control control, int column)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Bottom };
        panel.Children.Add(Label(label)); panel.Children.Add(control);
        SetColumn(panel, column); _controls.Children.Add(panel);
    }

    private TimeSpan Duration => _binding?.Duration ?? TimeSpan.Zero;
    private ICollection<AnimationKeyframe> Keyframes => _binding?.Keyframes ?? [];
    private IReadOnlyList<KeyframeProperty> Properties => _binding?.Properties ?? [];
    private TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value > Duration ? Duration : value;
    private void SetCaret(TimeSpan value, bool notify = true)
    {
        _offset = Clamp(value);
        if (notify) _binding?.CaretChanged(_offset);
        RefreshEditor(); DrawTimeline();
    }
    private void RefreshAll() { RefreshEditor(); RefreshList(); DrawTimeline(); }

    private void RefreshEditor()
    {
        if (_binding is null) return;
        _caretText.Text = $"EDIT CARET  {TimeText.Format(_offset)}   /   {TimeText.Format(Duration)}";
        if (SelectedProperty is not { } property) return;
        var existing = AtCaret(property);
        _value.Text = (existing?.Value ?? KeyframeEvaluator.Evaluate(Keyframes, property, _offset, _binding.Fallback(property))).ToString("0.###", CultureInfo.InvariantCulture);
        if (existing is not null) SelectInterpolation(existing.Interpolation);
        _range.Text = property switch
        {
            KeyframeProperty.PositionX or KeyframeProperty.PositionY => "− left/up, + right/down",
            KeyframeProperty.Scale => "0.01 to 20", KeyframeProperty.Opacity => "0 to 1",
            KeyframeProperty.Volume => "0 to 4", KeyframeProperty.Rotation => "degrees, + clockwise", _ => ""
        };
    }

    private void SelectInterpolation(KeyframeInterpolation value) => _interpolation.SelectedItem = _interpolation.Items.Cast<InterpolationChoice>().First(item => item.Value == value);

    private void Set_Click(object sender, RoutedEventArgs e)
    {
        if (_binding is null) return;
        if (SelectedProperty is not { } property || !double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        { MessageBox.Show(Window.GetWindow(this), "Enter a numeric keyframe value.", "Keyframes"); return; }
        value = property switch { KeyframeProperty.Scale => Math.Clamp(value, .01, 20), KeyframeProperty.Opacity => Math.Clamp(value, 0, 1), KeyframeProperty.Volume => Math.Clamp(value, 0, 4), KeyframeProperty.Rotation => Math.Clamp(value, -3600, 3600), _ => value };
        var interpolation = (_interpolation.SelectedItem as InterpolationChoice)?.Value ?? KeyframeInterpolation.Linear;
        KeyframeEvaluator.UpsertWithBaseline(Keyframes, property, _offset, value, interpolation, _binding.Fallback(property), Duration);
        Changed = true; _binding.Changed(); RefreshAll();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_binding is null || SelectedProperty is not { } property) return;
        if ((AtCaret(property) ?? Nearest(property, _offset)) is not { } existing)
        {
            MessageBox.Show(Window.GetWindow(this), $"There is no {Friendly(property)} keyframe at the caret. Click one of its diamonds first.", "Keyframes");
            return;
        }
        Keyframes.Remove(existing); Changed = true; _binding.Changed(); RefreshAll();
    }

    private AnimationKeyframe? AtCaret(KeyframeProperty property) => Keyframes.FirstOrDefault(item => item.Property == property && (item.Offset - _offset).Duration() <= TimeSpan.FromMilliseconds(1));

    /// <summary>
    /// The keyframe of this property nearest the given time, within roughly a diamond's
    /// width on screen, or null when the click was not aimed at one.
    /// </summary>
    private AnimationKeyframe? Nearest(KeyframeProperty property, TimeSpan time)
    {
        if (Duration <= TimeSpan.Zero) return null;
        var width = Math.Max(1, _timeline.ActualWidth - 110);
        var tolerance = TimeSpan.FromSeconds(Math.Max(Duration.TotalSeconds * 8 / width, _binding?.FrameDuration.TotalSeconds ?? 0));
        return Keyframes.Where(item => item.Property == property)
            .Select(item => (Keyframe: item, Distance: (item.Offset - time).Duration()))
            .Where(item => item.Distance <= tolerance)
            .OrderBy(item => item.Distance)
            .Select(item => item.Keyframe)
            .FirstOrDefault();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var item in Keyframes.OrderBy(item => item.Offset).ThenBy(item => item.Property))
            _list.Items.Add(new ListBoxItem { Content = $"{TimeText.Format(item.Offset)}   {Friendly(item.Property)}: {item.Value:0.###}   {Friendly(item.Interpolation)}", Tag = item, Style = _listItemStyle });
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: AnimationKeyframe item }) return;
        SelectProperty(item.Property); SelectInterpolation(item.Interpolation); SetCaret(item.Offset);
        _value.Text = item.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_binding is null || Properties.Count == 0) return;
        var point = e.GetPosition(_timeline); var width = Math.Max(1, _timeline.ActualWidth - 110);
        var row = Math.Clamp((int)((point.Y - 26) / Math.Max(1, (_timeline.ActualHeight - 28) / Properties.Count)), 0, Properties.Count - 1);
        var property = Properties[row];
        SelectProperty(property);
        var time = TimeSpan.FromSeconds(Math.Clamp((point.X - 105) / width, 0, 1) * Duration.TotalSeconds);
        SetCaret(Nearest(property, time) is { } keyframe ? keyframe.Offset : time);
    }

    private void DrawTimeline()
    {
        _timeline.Children.Clear();
        if (_binding is null || Properties.Count == 0 || _timeline.ActualWidth < 20) return;
        var left = 105d; var width = Math.Max(1, _timeline.ActualWidth - left - 5); var height = Math.Max(1, (_timeline.ActualHeight - 28) / Properties.Count);
        for (var tick = 0; tick <= 10; tick++)
        {
            var x = left + width * tick / 10;
            _timeline.Children.Add(new Line { X1 = x, X2 = x, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = new SolidColorBrush(Color.FromRgb(27, 47, 70)), StrokeThickness = 1 });
            var label = new TextBlock { Text = TimeText.Format(TimeSpan.FromSeconds(Duration.TotalSeconds * tick / 10)), FontSize = 9, Foreground = Brushes.LightGray };
            Canvas.SetLeft(label, x - 18); Canvas.SetTop(label, 3); _timeline.Children.Add(label);
        }
        for (var row = 0; row < Properties.Count; row++)
        {
            var y = 28 + row * height; var selected = SelectedProperty == Properties[row];
            var band = new Rectangle { Width = _timeline.ActualWidth, Height = height, Fill = new SolidColorBrush(selected ? Color.FromArgb(70, 35, 91, 125) : Color.FromArgb(row % 2 == 0 ? (byte)28 : (byte)12, 60, 100, 135)) };
            Canvas.SetTop(band, y); _timeline.Children.Add(band);
            var label = new TextBlock { Text = Friendly(Properties[row]), FontSize = 11, Foreground = selected ? Brushes.White : Brushes.LightGray, FontWeight = selected ? FontWeights.Bold : FontWeights.Normal };
            Canvas.SetLeft(label, 8); Canvas.SetTop(label, y + height / 2 - 8); _timeline.Children.Add(label);
            foreach (var keyframe in Keyframes.Where(item => item.Property == Properties[row]))
            {
                var x = left + Ratio(keyframe.Offset) * width;
                var diamond = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(255, 211, 77)), Stroke = Brushes.Black, StrokeThickness = 1, RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(45), ToolTip = $"{keyframe.Value:0.###} • {Friendly(keyframe.Interpolation)}" };
                Canvas.SetLeft(diamond, x - 5); Canvas.SetTop(diamond, y + height / 2 - 5); _timeline.Children.Add(diamond);
            }
        }
        var playbackX = left + Ratio(_binding.PlaybackOffset()) * width;
        var playback = new Line { X1 = playbackX, X2 = playbackX, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = new SolidColorBrush(Color.FromRgb(244, 63, 94)), StrokeThickness = 2.5, IsHitTestVisible = false };
        Panel.SetZIndex(playback, 49); _timeline.Children.Add(playback);
        var caretX = left + Ratio(_offset) * width;
        var caret = new Line { X1 = caretX, X2 = caretX, Y1 = 20, Y2 = _timeline.ActualHeight, Stroke = Brushes.White, StrokeThickness = 2 };
        Panel.SetZIndex(caret, 50); _timeline.Children.Add(caret);
    }

    private double Ratio(TimeSpan time) => Duration <= TimeSpan.Zero ? 0 : Math.Clamp(time.TotalSeconds / Duration.TotalSeconds, 0, 1);
    /// <summary>The row currently being edited, which the step buttons follow.</summary>
    public KeyframeProperty? SelectedProperty => (_property.SelectedItem as PropertyChoice)?.Value;
    private void SelectProperty(KeyframeProperty property)
    {
        var match = _property.Items.Cast<PropertyChoice>().FirstOrDefault(item => item.Value == property);
        if (match is not null) _property.SelectedItem = match;
    }

    private void Timeline_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_binding is null || e.Key is not (Key.Left or Key.Right)) return;
        SetCaret(_offset + (e.Key == Key.Right ? _binding.FrameDuration : -_binding.FrameDuration)); e.Handled = true;
    }

    private void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl+wheel belongs to the timeline zoom in the layers pane behind this strip.
        if (_binding is null || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SetCaret(_offset + (e.Delta > 0 ? _binding.FrameDuration : -_binding.FrameDuration)); e.Handled = true;
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
