using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Post.App;

/// <summary>
/// The face of a colour wheel, drawn a pixel at a time.
///
/// It was built from pie wedges with gradient fills, and the hairline seams between them
/// read as white spokes across the wheel — a highlight that is not a colour and does not
/// mean anything. A bitmap has no seams to leave.
/// </summary>
internal static class ColorDisc
{
    public static ImageSource Render(int size, Func<double, double, Color> pick)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new byte[size * size * 4];
        var centre = (size - 1) / 2d;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - centre) / centre;
                var dy = (centre - y) / centre;
                var radius = Math.Sqrt(dx * dx + dy * dy);
                if (radius > 1.02) continue;   // outside the disc, left transparent

                var colour = pick(Math.Atan2(dy, dx) * 180 / Math.PI, Math.Min(1, radius));

                // One pixel of feather at the rim, or the edge is a staircase.
                var edge = Math.Clamp((1 - radius) * centre, 0, 1);
                var alpha = colour.A / 255d * edge;

                var at = (y * size + x) * 4;
                // Pbgra32 is premultiplied: the channels carry the alpha already.
                pixels[at] = (byte)(colour.B * alpha);
                pixels[at + 1] = (byte)(colour.G * alpha);
                pixels[at + 2] = (byte)(colour.R * alpha);
                pixels[at + 3] = (byte)(alpha * 255);
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// One grading wheel: a hue ring with a puck dragged out from the middle, and a slider
/// beside it for how bright that range should be.
///
/// The puck sits where the tint is being asked for: its angle is which colour, its distance
/// from the middle is how much. That reads as a direction rather than three numbers, which
/// is the whole point of a wheel — the numbers are underneath for when they matter.
/// </summary>
internal sealed class ColorWheel : StackPanel
{
    private const double Face = 132;
    private const double PuckSize = 13;

    private readonly Canvas _face = new() { Width = Face, Height = Face };
    private readonly Ellipse _puck = new()
    {
        // Hollow, with a dark ring: a filled puck puts a colour on the wheel that is not
        // part of the wheel, and a white one reads as a highlight.
        Width = PuckSize, Height = PuckSize, Fill = Brushes.Transparent,
        Stroke = new SolidColorBrush(Color.FromRgb(12, 20, 34)), StrokeThickness = 3,
        // The puck sat on top of the surface that takes the mouse, so pressing on the puck —
        // the obvious way to drag it — hit the puck and started nothing.
        IsHitTestVisible = false,
    };
    private readonly Ellipse _halo = new()
    {
        Width = PuckSize - 4, Height = PuckSize - 4, Fill = Brushes.Transparent,
        Stroke = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), StrokeThickness = 1.5,
        IsHitTestVisible = false,
    };
    private readonly Slider _luma;
    private readonly TextBlock _readout = new()
    {
        Foreground = Brushes.LightGray, FontFamily = new FontFamily("Consolas"), FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        // Fixed, or the readout growing by a minus sign resizes the wheel, which reflows the
        // row, which slides all four wheels sideways while one of them is being dragged.
        Width = Face, TextAlignment = TextAlignment.Center,
    };

    private double _x, _y;      // −1 to 1 across the face
    private bool _dragging;
    private bool _quiet;

    /// <summary>Fired whenever the puck or the slider moves, but not while being reset.</summary>
    public event Action? Changed;

    /// <summary>
    /// How much of each channel this wheel is asking for. Red sits at 0°, green at 120° and
    /// blue at 240°, so pulling the puck towards a colour adds that colour.
    /// </summary>
    public (double R, double G, double B) Offsets
    {
        get
        {
            var angle = Math.Atan2(-_y, _x);
            var amount = Math.Min(1, Math.Sqrt(_x * _x + _y * _y));
            if (amount < .001) return (0, 0, 0);

            // Each channel peaks at its own third of the circle and falls off from there.
            double Channel(double at) => Math.Max(0, Math.Cos(angle - at));
            var r = Channel(0);
            var g = Channel(2 * Math.PI / 3);
            var b = Channel(4 * Math.PI / 3);

            // Pulling towards a colour adds it and takes a little from the other two, so a
            // wheel at full stretch tints rather than just brightening.
            var mean = (r + g + b) / 3;
            return ((r - mean) * amount, (g - mean) * amount, (b - mean) * amount);
        }
    }

    /// <summary>The slider beside the wheel: how bright this range is, neutral in the middle.</summary>
    public double Luma => _luma.Value;

    public ColorWheel(string title, double lumaMinimum, double lumaMaximum, double lumaNeutral)
    {
        Margin = new Thickness(6, 0, 6, 0);
        // A fixed column, for the same reason the readout is fixed: nothing in here may
        // change size while a puck is being dragged.
        Width = Face + 4;

        Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6),
        });

        _luma = new Slider
        {
            Minimum = lumaMinimum, Maximum = lumaMaximum, Value = lumaNeutral,
            Width = Face, Margin = new Thickness(0, 6, 0, 0),
        };
        _luma.ValueChanged += (_, _) => Report();

        BuildFace();

        var row = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(_face);
        Children.Add(row);
        Children.Add(_luma);
        Children.Add(_readout);

        var reset = new Button
        {
            Content = "reset", Padding = new Thickness(8, 1, 8, 1), FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
        };
        reset.Click += (_, _) => Reset(lumaNeutral);
        Children.Add(reset);

        _neutral = lumaNeutral;
        Report();
    }

    private readonly double _neutral;

    private void BuildFace()
    {
        // Hue around, and how much of it out from the middle — which fades to nothing at
        // the centre, so the panel shows through where the wheel is asking for no tint.
        _face.Children.Add(new Image
        {
            Width = Face, Height = Face, IsHitTestVisible = false,
            Source = ColorDisc.Render((int)Face, (angle, radius) =>
            {
                var hue = FromAngle(angle * Math.PI / 180);
                return Color.FromArgb((byte)(radius * 255), hue.R, hue.G, hue.B);
            }),
        });

        _face.Children.Add(new Ellipse
        {
            Width = Face, Height = Face,
            Stroke = new SolidColorBrush(Color.FromRgb(70, 96, 128)), StrokeThickness = 1,
            IsHitTestVisible = false,
        });

        // A transparent disc over the top so the whole face takes the mouse: the wedges are
        // hit-test invisible, and a Canvas with no background is not hit-testable at all.
        var surface = new Ellipse { Width = Face, Height = Face, Fill = Brushes.Transparent, Cursor = Cursors.Cross };
        surface.MouseLeftButtonDown += (_, e) => { _dragging = true; surface.CaptureMouse(); Track(e.GetPosition(_face)); };
        surface.MouseMove += (_, e) => { if (_dragging) Track(e.GetPosition(_face)); };
        surface.MouseLeftButtonUp += (_, _) => { _dragging = false; surface.ReleaseMouseCapture(); };
        surface.MouseRightButtonUp += (_, _) => Reset(_neutral);
        _face.Children.Add(surface);

        // A pale ring just inside the dark one, so the puck is visible against both the
        // dark middle of the wheel and the bright rim.
        _face.Children.Add(_halo);
        _face.Children.Add(_puck);
        Place();
    }

    /// <summary>A fully saturated colour at this angle, red at 0 and going anticlockwise.</summary>
    private static Color FromAngle(double angle)
    {
        var degrees = (angle * 180 / Math.PI + 360) % 360;
        var sector = degrees / 60;
        var fade = (byte)(255 * (1 - Math.Abs(sector % 2 - 1)));
        return (int)sector switch
        {
            0 => Color.FromRgb(255, fade, 0),
            1 => Color.FromRgb(fade, 255, 0),
            2 => Color.FromRgb(0, 255, fade),
            3 => Color.FromRgb(0, fade, 255),
            4 => Color.FromRgb(fade, 0, 255),
            _ => Color.FromRgb(255, 0, fade),
        };
    }

    private void Track(Point point)
    {
        var centre = Face / 2;
        var x = (point.X - centre) / centre;
        var y = (point.Y - centre) / centre;

        // Inside the circle, always: dragging past the rim pins the puck to the edge rather
        // than letting go of it.
        var distance = Math.Sqrt(x * x + y * y);
        if (distance > 1) { x /= distance; y /= distance; }

        _x = x; _y = y;
        Place();
        Report();
    }

    private void Place()
    {
        var centre = Face / 2;
        Canvas.SetLeft(_puck, centre + _x * centre - PuckSize / 2);
        Canvas.SetTop(_puck, centre + _y * centre - PuckSize / 2);
        Canvas.SetLeft(_halo, centre + _x * centre - (PuckSize - 4) / 2);
        Canvas.SetTop(_halo, centre + _y * centre - (PuckSize - 4) / 2);
    }

    public void Reset(double lumaNeutral)
    {
        _quiet = true;
        _x = 0; _y = 0;
        _luma.Value = lumaNeutral;
        _quiet = false;
        Place();
        Report();
    }

    /// <summary>Puts the puck where these offsets say, without reporting back.</summary>
    public void Set(double r, double g, double b, double luma)
    {
        _quiet = true;
        var x = r - (g + b) / 2;
        var y = (g - b) * Math.Sqrt(3) / 2;
        var distance = Math.Sqrt(x * x + y * y);
        if (distance > 1) { x /= distance; y /= distance; }
        _x = x; _y = -y;
        _luma.Value = luma;
        _quiet = false;
        Place();
        Report();
    }

    private void Report()
    {
        var (r, g, b) = Offsets;
        _readout.Text = $"Y {N(_luma.Value)}  R {N(r)}  G {N(g)}  B {N(b)}";
        if (!_quiet) Changed?.Invoke();
    }

    /// <summary>Always the same width, sign included, so the readout never resizes.</summary>
    private static string N(double value) => value.ToString("+0.00;-0.00; 0.00", CultureInfo.InvariantCulture);
}

