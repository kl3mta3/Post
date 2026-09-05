using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Post.App;

/// <summary>
/// A colour, picked from a wheel rather than typed or chosen from a short list of names.
///
/// It stands in for the editable combo box of colour names that came before it, so it keeps
/// a <see cref="Text"/> of the same shape — a name, or #AARRGGBB. What it adds is the wheel,
/// and an opacity of its own: text and background are two colours, and each carries its own
/// alpha, so one can be solid over a half-transparent plate without touching the other.
/// </summary>
internal sealed class ColorField : Button
{
    private readonly Border _swatch = new()
    {
        Width = 34, Height = 16, CornerRadius = new CornerRadius(3),
        BorderBrush = new SolidColorBrush(Color.FromRgb(120, 148, 178)), BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 0, 8, 0),
    };
    private readonly TextBlock _caption = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly Popup _popup = new() { StaysOpen = false, Placement = PlacementMode.Bottom, AllowsTransparency = true };

    private Color _color = Colors.White;
    private bool _quiet;

    public event Action? Changed;

    public ColorField()
    {
        Padding = new Thickness(6, 3, 6, 3);
        HorizontalContentAlignment = HorizontalAlignment.Left;

        var halves = new Grid();
        halves.ColumnDefinitions.Add(new ColumnDefinition());
        halves.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_solid, 1);
        halves.Children.Add(_wash);
        halves.Children.Add(_solid);
        _swatch.Child = halves;

        var face = new StackPanel { Orientation = Orientation.Horizontal };
        face.Children.Add(_swatch);
        face.Children.Add(_caption);
        Content = face;

        _popup.PlacementTarget = this;
        Click += (_, _) => { BuildPopup(); _popup.IsOpen = !_popup.IsOpen; };
        Show();
    }

    /// <summary>The colour as text, in the form the overlay stores: #AARRGGBB.</summary>
    public string Text
    {
        get => $"#{_color.A:X2}{_color.R:X2}{_color.G:X2}{_color.B:X2}";
        set
        {
            _color = Parse(value, _color);
            Show();
        }
    }

    /// <summary>A colour name or #RRGGBB / #AARRGGBB, falling back rather than throwing.</summary>
    public static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            var converted = ColorConverter.ConvertFromString(text.Trim());
            return converted is Color color ? color : fallback;
        }
        catch { return fallback; }
    }

    private void Show()
    {
        // Two halves: the colour as it really is, over a chequer so a transparent one reads
        // as transparent rather than as nothing, and beside it the same colour at full
        // opacity so its hue is visible even at an alpha of zero. A single flat patch of
        // #00FF965A is invisible, which tells you nothing about the colour you picked.
        _swatch.Background = Chequer;
        _wash.Fill = new SolidColorBrush(_color);
        _solid.Fill = new SolidColorBrush(Color.FromRgb(_color.R, _color.G, _color.B));

        _caption.Text = Text;
        if (!_quiet) Changed?.Invoke();
    }

    private readonly Rectangle _wash = new();
    private readonly Rectangle _solid = new();

    /// <summary>The grey chequer that says "see through", drawn once and shared.</summary>
    private static readonly DrawingBrush Chequer = BuildChequer();

    private static DrawingBrush BuildChequer()
    {
        var light = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(210, 214, 220)), null, new RectangleGeometry(new Rect(0, 0, 8, 8)));
        var dark = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(160, 166, 176)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } });

        var brush = new DrawingBrush(new DrawingGroup { Children = { light, dark } })
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private void BuildPopup()
    {
        if (_popup.Child is not null) return;

        // Two different things that both end at nothing, so both are said out loud: one
        // takes the colour to black, the other takes it to see-through.
        var wheel = new HueWheel();
        var value = Bar("Brightness", 0, 1, "0 is black");
        var alpha = Bar("Opacity", 0, 1, "0 is see-through");
        var hex = new TextBox { FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 8, 0, 0) };

        void Push(bool fromWheel)
        {
            _quiet = true;
            var picked = HueWheel.FromHsv(wheel.Hue, wheel.Saturation, value.Value);
            _color = Color.FromArgb((byte)Math.Round(alpha.Value * 255), picked.R, picked.G, picked.B);
            _quiet = false;
            hex.Text = Text;
            wheel.ShowSelected(picked);
            Show();
        }

        wheel.Changed += () => Push(true);
        value.ValueChanged += (_, _) => Push(false);
        alpha.ValueChanged += (_, _) => Push(false);

        hex.LostKeyboardFocus += (_, _) =>
        {
            var parsed = Parse(hex.Text, _color);
            _color = parsed;
            var (h, s, v) = HueWheel.ToHsv(parsed);
            _quiet = true;
            wheel.Set(h, s); value.Value = v; alpha.Value = parsed.A / 255d;
            _quiet = false;
            Show();
        };

        // Opened on the colour it already has, rather than snapping to something else.
        var (hue, saturation, brightness) = HueWheel.ToHsv(_color);
        _quiet = true;
        wheel.Set(hue, saturation);

        wheel.ShowSelected(Color.FromRgb(_color.R, _color.G, _color.B));
        value.Value = brightness;
        alpha.Value = _color.A / 255d;
        _quiet = false;
        hex.Text = Text;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(wheel);
        panel.Children.Add(value.Host);
        panel.Children.Add(alpha.Host);
        panel.Children.Add(hex);

        var quick = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Width = 190 };
        foreach (var name in new[] { "White", "Black", "Transparent", "Red", "Orange", "Yellow", "Lime", "Cyan", "DeepSkyBlue", "Purple" })
        {
            var colour = Parse(name, Colors.White);
            var chip = new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(colour), Cursor = Cursors.Hand, ToolTip = name,
                BorderBrush = new SolidColorBrush(Color.FromRgb(120, 148, 178)), BorderThickness = new Thickness(1),
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _color = colour;
                var (h, s, v) = HueWheel.ToHsv(colour);
                _quiet = true;
                wheel.Set(h, s); value.Value = v; alpha.Value = colour.A / 255d;
                _quiet = false;
                hex.Text = Text;
                Show();
            };
            quick.Children.Add(chip);
        }
        panel.Children.Add(quick);

        _popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 23, 43)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 80, 111)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = panel,
        };
    }

    private sealed record Labelled(StackPanel Host, Slider Slider)
    {
        public double Value { get => Slider.Value; set => Slider.Value = value; }
        public event RoutedPropertyChangedEventHandler<double> ValueChanged
        {
            add => Slider.ValueChanged += value;
            remove => Slider.ValueChanged -= value;
        }
    }

    private static Labelled Bar(string label, double minimum, double maximum, string hint)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Width = 190 };
        var readout = new TextBlock { Foreground = Brushes.LightGray, FontSize = 10 };
        slider.ValueChanged += (_, e) => readout.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);

        var header = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(readout, Dock.Right); header.Children.Add(readout);
        header.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 11 });

        var host = new StackPanel();
        host.Children.Add(header);
        host.Children.Add(slider);
        host.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 10 });
        return new Labelled(host, slider);
    }
}

/// <summary>
/// The disc a colour is picked off: hue around it, saturation out from the middle. Value
/// and opacity are sliders beside it, because neither is a direction.
/// </summary>
internal sealed class HueWheel : Canvas
{
    private const double Face = 190;
    private const double PuckSize = 12;

    private readonly Ellipse _puck = new()
    {
        Width = PuckSize, Height = PuckSize, Fill = Brushes.Transparent,
        Stroke = Brushes.White, StrokeThickness = 2,
        // Same as the grading wheels: on top of the surface, it would eat the press that
        // starts a drag.
        IsHitTestVisible = false,
    };
    private bool _dragging;

    public double Hue { get; private set; }
    public double Saturation { get; private set; }

    public event Action? Changed;

    public HueWheel()
    {
        Width = Face; Height = Face;

        // White in the middle out to full saturation at the rim, which is what saturation
        // means: how far the colour is from grey. Drawn a pixel at a time, because wedges
        // leave seams and the seams read as white spokes across the wheel.
        Children.Add(new Image
        {
            Width = Face, Height = Face, IsHitTestVisible = false,
            Source = ColorDisc.Render((int)Face, (angle, radius) => FromHsv(angle, radius, 1)),
        });

        var surface = new Ellipse { Width = Face, Height = Face, Fill = Brushes.Transparent, Cursor = Cursors.Cross };
        surface.MouseLeftButtonDown += (_, e) => { _dragging = true; surface.CaptureMouse(); Track(e.GetPosition(this)); };
        surface.MouseMove += (_, e) => { if (_dragging) Track(e.GetPosition(this)); };
        surface.MouseLeftButtonUp += (_, _) => { _dragging = false; surface.ReleaseMouseCapture(); };
        Children.Add(surface);
        Children.Add(_puck);
        Place();
    }

    private void Track(Point point)
    {
        var centre = Face / 2;
        var x = (point.X - centre) / centre;
        var y = (centre - point.Y) / centre;
        var distance = Math.Sqrt(x * x + y * y);
        if (distance > 1) { x /= distance; y /= distance; distance = 1; }

        Hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        Saturation = distance;
        Place();
        Changed?.Invoke();
    }

    public void Set(double hue, double saturation)
    {
        Hue = hue; Saturation = Math.Clamp(saturation, 0, 1);
        Place();
    }

    /// <summary>
    /// Fills the puck with the colour actually selected. A white dot over a colour wheel
    /// reads as part of the wheel and says nothing about what was picked, which is worse
    /// than useless when the brightness slider has taken the colour somewhere dark.
    /// </summary>
    public void ShowSelected(Color color) => _puck.Fill = new SolidColorBrush(color);

    private void Place()
    {
        var centre = Face / 2;
        var angle = Hue * Math.PI / 180;
        SetLeft(_puck, centre + Math.Cos(angle) * Saturation * centre - PuckSize / 2);
        SetTop(_puck, centre - Math.Sin(angle) * Saturation * centre - PuckSize / 2);
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var chroma = value * saturation;
        var second = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var match = value - chroma;

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };
        return Color.FromRgb(Byte(r + match), Byte(g + match), Byte(b + match));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;

        var hue = chroma < .0001 ? 0
            : max == r ? 60 * (((g - b) / chroma + 6) % 6)
            : max == g ? 60 * ((b - r) / chroma + 2)
            : 60 * ((r - g) / chroma + 4);

        return (hue, max < .0001 ? 0 : chroma / max, max);
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
