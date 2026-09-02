using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Post.App;

internal sealed class GraphicsOverlayEditor : Window
{
    private readonly GraphicsOverlay _target;
    private readonly Canvas _stage = new() { Width = 640, Height = 360, Background = Brushes.Black, ClipToBounds = true };
    private readonly Border _item = new() { BorderBrush = Brushes.White, BorderThickness = new Thickness(1), MinWidth = 30, MinHeight = 20 };
    private readonly TextBox _text = new() { AcceptsReturn = true, Height = 74, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _font = new() { IsEditable = true };
    private readonly Slider _fontSize = new() { Minimum = 10, Maximum = 220, TickFrequency = 1 };
    private readonly ComboBox _foreground = ColorBox();
    private readonly ComboBox _background = ColorBox();
    private readonly ComboBox _fill1 = ColorBox();
    private readonly ComboBox _fill2 = ColorBox();
    private readonly CheckBox _useSecondFill = new() { Content = "Blend to a second color", Foreground = Brushes.White };
    private readonly ComboBox _gradientKind = new();
    private readonly Slider _gradientAngle = new() { Minimum = -180, Maximum = 180, TickFrequency = 1 };
    private readonly TextBlock _gradientAngleLabel = new() { Foreground = Brushes.LightGray };
    private readonly Slider _opacity = new() { Minimum = .05, Maximum = 1, TickFrequency = .05 };
    private readonly CheckBox _aspect = new() { Content = "Lock image aspect ratio", Foreground = Brushes.White };
    private readonly Line _verticalGuide = new() { Stroke = new SolidColorBrush(Color.FromRgb(69, 210, 235)), StrokeThickness = 1.25, StrokeDashArray = new DoubleCollection([5, 3]), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Line _horizontalGuide = new() { Stroke = new SolidColorBrush(Color.FromRgb(69, 210, 235)), StrokeThickness = 1.25, StrokeDashArray = new DoubleCollection([5, 3]), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private Point? _dragOrigin;
    private Point _itemOrigin;
    private bool _refreshQueued;

    public GraphicsOverlayEditor(GraphicsOverlay target)
    {
        _target = target;
        Title = target.Kind switch { GraphicsOverlayKind.Text => "Text Overlay", GraphicsOverlayKind.Image => "Image Overlay", GraphicsOverlayKind.SolidColor => "Solid Color Graphic", GraphicsOverlayKind.Gradient => "Gradient Graphic", _ => "Graphic" };
        Width = 980; Height = 610; MinWidth = 820; MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        foreach (var family in Fonts.SystemFontFamilies.OrderBy(value => value.Source))
        {
            var option = new ComboBoxItem { Content = family.Source, FontFamily = family, Tag = family.Source };
            TextSearch.SetText(option, family.Source); _font.Items.Add(option);
        }
        _text.Text = target.Text;
        _font.Text = string.IsNullOrWhiteSpace(target.FontFamily) ? "Segoe UI" : target.FontFamily;
        _font.FontFamily = new FontFamily(_font.Text); _fontSize.Value = target.FontSize;
        _foreground.Text = string.IsNullOrWhiteSpace(target.Foreground) ? "White" : target.Foreground;
        _background.Text = string.IsNullOrWhiteSpace(target.Background) ? "Transparent" : target.Background;
        _fill1.Text = string.IsNullOrWhiteSpace(target.FillColor1) ? "White" : target.FillColor1;
        _fill2.Text = string.IsNullOrWhiteSpace(target.FillColor2) ? "Black" : target.FillColor2;
        _useSecondFill.IsChecked = target.UseSecondFillColor; _gradientKind.Items.Add(GraphicGradientKind.Linear); _gradientKind.Items.Add(GraphicGradientKind.Radial); _gradientKind.SelectedItem = target.GradientKind;
        _gradientAngle.Value = target.GradientAngle; _gradientAngleLabel.Text = $"Angle: {target.GradientAngle:0}°";
        _opacity.Value = target.Opacity; _aspect.IsChecked = target.PreserveAspectRatio;

        var settings = new StackPanel { Margin = new Thickness(16) };
        if (target.Kind == GraphicsOverlayKind.Text)
        {
            settings.Children.Add(Label("Text")); settings.Children.Add(_text);
            settings.Children.Add(Label("Font")); settings.Children.Add(_font);
            settings.Children.Add(Label("Size")); settings.Children.Add(_fontSize);
            settings.Children.Add(Label("Text color (name or #AARRGGBB)")); settings.Children.Add(_foreground);
            settings.Children.Add(Label("Background (Transparent supported)")); settings.Children.Add(_background);
        }
        else if (target.Kind == GraphicsOverlayKind.Image) { settings.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(target.ImagePath), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }); settings.Children.Add(_aspect); }
        else
        {
            settings.Children.Add(Label(target.Kind == GraphicsOverlayKind.SolidColor ? "Color" : "Color 1")); settings.Children.Add(_fill1);
            if (target.Kind == GraphicsOverlayKind.Gradient)
            {
                settings.Children.Add(_useSecondFill); settings.Children.Add(Label("Color 2")); settings.Children.Add(_fill2);
                settings.Children.Add(Label("Blend")); settings.Children.Add(_gradientKind); settings.Children.Add(Label("Angle")); settings.Children.Add(_gradientAngle); settings.Children.Add(_gradientAngleLabel);
            }
        }
        settings.Children.Add(Label("Opacity")); settings.Children.Add(_opacity);
        settings.Children.Add(new TextBlock { Text = "Drag the overlay to position it. Its center snaps to the 25%, 50%, and 75% guides. Drag the cyan corner to resize it.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });

        var previewWrap = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(54, 169, 225)), BorderThickness = new Thickness(1), Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = _stage };
        var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); root.ColumnDefinitions.Add(new ColumnDefinition()); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(settings); Grid.SetColumn(previewWrap, 1); root.Children.Add(previewWrap);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 }; var apply = new Button { Content = "Apply Overlay", IsDefault = true, MinWidth = 125, Background = new SolidColorBrush(Color.FromRgb(69, 199, 179)), Foreground = Brushes.Black };
        apply.Click += Apply_Click; buttons.Children.Add(cancel); buttons.Children.Add(apply); Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons); Content = root;

        _item.Width = Math.Max(30, target.Width * _stage.Width); _item.Height = Math.Max(20, target.Height * _stage.Height);
        _item.Opacity = target.Opacity;
        Canvas.SetLeft(_item, target.X * _stage.Width); Canvas.SetTop(_item, target.Y * _stage.Height);
        _item.PreviewMouseLeftButtonDown += (_, e) => { if (IsInsideThumb(e.OriginalSource as DependencyObject)) return; _dragOrigin = e.GetPosition(_stage); _itemOrigin = new Point(Canvas.GetLeft(_item), Canvas.GetTop(_item)); _item.CaptureMouse(); e.Handled = true; };
        _item.PreviewMouseMove += (_, e) =>
        {
            if (_dragOrigin is not { } origin || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(_stage);
            var left = Math.Clamp(_itemOrigin.X + p.X - origin.X, 0, _stage.Width - _item.Width);
            var top = Math.Clamp(_itemOrigin.Y + p.Y - origin.Y, 0, _stage.Height - _item.Height);
            var centerX = SnapCenter(left + _item.Width / 2, _stage.Width, out var verticalGuide);
            var centerY = SnapCenter(top + _item.Height / 2, _stage.Height, out var horizontalGuide);
            Canvas.SetLeft(_item, Math.Clamp(centerX - _item.Width / 2, 0, _stage.Width - _item.Width));
            Canvas.SetTop(_item, Math.Clamp(centerY - _item.Height / 2, 0, _stage.Height - _item.Height));
            ShowGuides(verticalGuide, horizontalGuide);
        };
        _item.PreviewMouseLeftButtonUp += (_, _) => { _dragOrigin = null; _item.ReleaseMouseCapture(); HideGuides(); };
        _item.LostMouseCapture += (_, _) => HideGuides();
        var handle = new Thumb { Width = 16, Height = 16, Background = new SolidColorBrush(Color.FromRgb(69, 199, 220)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = System.Windows.Input.Cursors.SizeNWSE };
        handle.DragDelta += (_, e) => { var ratio = Math.Max(.05, _item.Width / Math.Max(1, _item.Height)); var width = Math.Clamp(_item.Width + e.HorizontalChange, 30, _stage.Width - Canvas.GetLeft(_item)); var height = Math.Clamp(_item.Height + e.VerticalChange, 20, _stage.Height - Canvas.GetTop(_item)); if (target.Kind == GraphicsOverlayKind.Image && _aspect.IsChecked == true) height = Math.Clamp(width / ratio, 20, _stage.Height - Canvas.GetTop(_item)); _item.Width = width; _item.Height = height; };
        var selection = new Grid(); selection.Children.Add(CreateContent()); selection.Children.Add(handle); _item.Child = selection; _stage.Children.Add(_item);
        _verticalGuide.Y1 = 0; _verticalGuide.Y2 = _stage.Height; _horizontalGuide.X1 = 0; _horizontalGuide.X2 = _stage.Width;
        Panel.SetZIndex(_verticalGuide, 100); Panel.SetZIndex(_horizontalGuide, 100); _stage.Children.Add(_verticalGuide); _stage.Children.Add(_horizontalGuide);
        _text.TextChanged += (_, _) => QueueRefresh();
        _font.SelectionChanged += (_, _) => { if (_font.SelectedItem is ComboBoxItem { Tag: string name }) { _font.Text = name; _font.FontFamily = new FontFamily(name); } QueueRefresh(); };
        _font.LostKeyboardFocus += (_, _) => { UpdateSelectedFontPreview(); QueueRefresh(); };
        _fontSize.ValueChanged += (_, _) => QueueRefresh(); _foreground.SelectionChanged += (_, _) => QueueRefresh(); _background.SelectionChanged += (_, _) => QueueRefresh(); _opacity.ValueChanged += (_, _) => QueueRefresh();
        _foreground.LostKeyboardFocus += (_, _) => QueueRefresh(); _background.LostKeyboardFocus += (_, _) => QueueRefresh(); _aspect.Checked += (_, _) => QueueRefresh(); _aspect.Unchecked += (_, _) => QueueRefresh();
        _font.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { UpdateSelectedFontPreview(); QueueRefresh(); }));
        _foreground.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueRefresh()));
        _background.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueRefresh()));
        _fill1.SelectionChanged += (_, _) => QueueRefresh(); _fill2.SelectionChanged += (_, _) => QueueRefresh();
        _fill1.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueRefresh())); _fill2.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueRefresh()));
        _useSecondFill.Checked += (_, _) => { _fill2.IsEnabled = true; QueueRefresh(); }; _useSecondFill.Unchecked += (_, _) => { _fill2.IsEnabled = false; QueueRefresh(); }; _fill2.IsEnabled = _useSecondFill.IsChecked == true;
        _gradientKind.SelectionChanged += (_, _) => QueueRefresh(); _gradientAngle.ValueChanged += (_, _) => { _gradientAngleLabel.Text = $"Angle: {_gradientAngle.Value:0}°"; QueueRefresh(); };
    }

    private static ComboBox ColorBox()
    {
        var box = new ComboBox { IsEditable = true };
        foreach (var color in new[] { "Transparent", "White", "Black", "Red", "Orange", "Yellow", "Lime", "Cyan", "DeepSkyBlue", "Purple", "#FFFFFFFF", "#AA000000" }) box.Items.Add(color);
        return box;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
    private void UpdateSelectedFontPreview()
    {
        if (!string.IsNullOrWhiteSpace(_font.Text)) _font.FontFamily = new FontFamily(_font.Text);
    }
    private static double SnapCenter(double value, double extent, out double? guide)
    {
        const double threshold = 8;
        guide = null; var closest = value; var distance = threshold + 1;
        foreach (var fraction in new[] { .25, .5, .75 })
        {
            var candidate = extent * fraction; var current = Math.Abs(value - candidate);
            if (current <= threshold && current < distance) { closest = candidate; guide = candidate; distance = current; }
        }
        return closest;
    }
    private void ShowGuides(double? vertical, double? horizontal)
    {
        if (vertical is { } x) { _verticalGuide.X1 = x; _verticalGuide.X2 = x; _verticalGuide.Visibility = Visibility.Visible; } else _verticalGuide.Visibility = Visibility.Collapsed;
        if (horizontal is { } y) { _horizontalGuide.Y1 = y; _horizontalGuide.Y2 = y; _horizontalGuide.Visibility = Visibility.Visible; } else _horizontalGuide.Visibility = Visibility.Collapsed;
    }
    private void HideGuides() { _verticalGuide.Visibility = Visibility.Collapsed; _horizontalGuide.Visibility = Visibility.Collapsed; }
    private FrameworkElement CreateContent()
    {
        if (_target.Kind == GraphicsOverlayKind.Image && _target.ImagePath is { } path)
            return new Image { Source = new BitmapImage(new Uri(path)), Stretch = _aspect.IsChecked == true ? Stretch.Uniform : Stretch.Fill };
        if (_target.Kind is GraphicsOverlayKind.SolidColor or GraphicsOverlayKind.Gradient)
            return new Border { Background = FillBrush(_fill1.Text, _fill2.Text, _target.Kind == GraphicsOverlayKind.Gradient && _useSecondFill.IsChecked == true, _gradientKind.SelectedItem is GraphicGradientKind kind ? kind : GraphicGradientKind.Linear, _gradientAngle.Value) };
        return new Border { Background = BrushOf(_background.Text, Brushes.Transparent), Child = new TextBlock { Text = _text.Text, FontFamily = new FontFamily(string.IsNullOrWhiteSpace(_font.Text) ? "Segoe UI" : _font.Text), FontSize = _fontSize.Value * 360 / 1080, Foreground = BrushOf(_foreground.Text, Brushes.White), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    }
    private void RefreshContent()
    {
        if (_item.Child is not Grid grid || grid.Children.Count < 2) return;
        grid.Children.RemoveAt(0); grid.Children.Insert(0, CreateContent()); _item.Opacity = _opacity.Value;
    }
    private void QueueRefresh()
    {
        if (_refreshQueued) return; _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; RefreshContent(); }, System.Windows.Threading.DispatcherPriority.Background);
    }
    private static bool IsInsideThumb(DependencyObject? item)
    {
        while (item is not null) { if (item is Thumb) return true; item = VisualTreeHelper.GetParent(item); }
        return false;
    }
    private static Brush BrushOf(string? value, Brush fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(value) ? "Transparent" : value)!); } catch { return fallback; }
    }
    private static Brush FillBrush(string? first, string? second, bool useSecond, GraphicGradientKind kind, double angle)
    {
        var firstColor = ColorOf(first, Colors.White); if (!useSecond) return new SolidColorBrush(firstColor);
        var secondColor = ColorOf(second, Colors.Black); var radians = angle * Math.PI / 180; var dx = Math.Cos(radians); var dy = Math.Sin(radians);
        if (kind == GraphicGradientKind.Radial)
        {
            var origin = new Point(Math.Clamp(.5 - dx * .15, 0, 1), Math.Clamp(.5 - dy * .15, 0, 1));
            return new RadialGradientBrush(firstColor, secondColor) { Center = new Point(.5, .5), GradientOrigin = origin, RadiusX = .7, RadiusY = .7 };
        }
        return new LinearGradientBrush(firstColor, secondColor, new Point(.5 - dx / 2, .5 - dy / 2), new Point(.5 + dx / 2, .5 + dy / 2));
    }
    private static Color ColorOf(string? value, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(value) ? "Transparent" : value)!; } catch { return fallback; } }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        _target.Text = string.IsNullOrWhiteSpace(_text.Text) ? "Text" : _text.Text; _target.FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? "Segoe UI" : _font.Text;
        _target.FontSize = _fontSize.Value; _target.Foreground = string.IsNullOrWhiteSpace(_foreground.Text) ? "White" : _foreground.Text; _target.Background = string.IsNullOrWhiteSpace(_background.Text) ? "Transparent" : _background.Text;
        _target.FillColor1 = string.IsNullOrWhiteSpace(_fill1.Text) ? "White" : _fill1.Text; _target.FillColor2 = string.IsNullOrWhiteSpace(_fill2.Text) ? "Black" : _fill2.Text;
        _target.UseSecondFillColor = _useSecondFill.IsChecked == true; _target.GradientKind = _gradientKind.SelectedItem is GraphicGradientKind kind ? kind : GraphicGradientKind.Linear; _target.GradientAngle = _gradientAngle.Value;
        _target.Opacity = _opacity.Value; _target.PreserveAspectRatio = _aspect.IsChecked == true; _target.X = Math.Clamp(Canvas.GetLeft(_item) / _stage.Width, 0, 1); _target.Y = Math.Clamp(Canvas.GetTop(_item) / _stage.Height, 0, 1); _target.Width = Math.Clamp(_item.Width / _stage.Width, .01, 1); _target.Height = Math.Clamp(_item.Height / _stage.Height, .01, 1); DialogResult = true;
    }
}
