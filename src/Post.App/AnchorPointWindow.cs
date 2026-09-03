using Post.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Post.App;

/// <summary>
/// Sets the point everything on a layer turns and scales about. The stage stands for an
/// item's own box rather than the frame, because the anchor is a fraction of whatever it
/// is applied to: the middle of a caption and the middle of a full-frame clip are both
/// 0.5, 0.5.
/// </summary>
internal sealed class AnchorPointWindow : Window
{
    private const double StageWidth = 560, StageHeight = 315, DotSize = 14;

    private readonly Canvas _stage = new()
    {
        Width = StageWidth, Height = StageHeight, ClipToBounds = true,
        Background = new SolidColorBrush(Color.FromRgb(5, 13, 27)),
    };
    private readonly Ellipse _dot = new()
    {
        Width = DotSize, Height = DotSize, Fill = Brushes.White,
        Stroke = new SolidColorBrush(Color.FromRgb(20, 40, 70)), StrokeThickness = 2, Cursor = Cursors.Hand,
    };
    private readonly TextBlock _readout = new() { FontFamily = new FontFamily("Consolas"), Foreground = Brushes.White, FontWeight = FontWeights.Bold };
    private readonly TimelineLayer _layer;
    private bool _dragging;

    public AnchorPointWindow(TimelineLayer layer, Window owner)
    {
        _layer = layer;
        Title = $"Anchor Point — {layer.Name}";
        Width = 640; Height = 560; MinWidth = 620; MinHeight = 520; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Drag the white dot to the point this layer should turn and scale about.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The square stands for whatever is on the layer, not the video frame, so the middle is the middle of each clip or overlay. The dot snaps to the quarter, middle and three-quarter guides, and to the corners.",
            Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        AddGuides();
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 122, 154)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = _stage, HorizontalAlignment = HorizontalAlignment.Center,
        });

        _readout.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(_readout);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var centre = new Button { Content = "Centre", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Set Anchor Point", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        centre.Click += (_, _) => MoveDot(.5, .5);
        save.Click += (_, _) => { _layer.AnchorX = AnchorX; _layer.AnchorY = AnchorY; DialogResult = true; };
        buttons.Children.Add(centre); buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;

        _stage.Children.Add(_dot);
        Panel.SetZIndex(_dot, 50);
        _dot.PreviewMouseLeftButtonDown += (_, e) => { _dragging = true; _dot.CaptureMouse(); e.Handled = true; };
        _dot.PreviewMouseLeftButtonUp += (_, _) => { _dragging = false; _dot.ReleaseMouseCapture(); };
        _dot.LostMouseCapture += (_, _) => _dragging = false;
        _dot.PreviewMouseMove += Dot_PreviewMouseMove;
        // Clicking anywhere on the stage drops the anchor there, which is quicker than
        // dragging when the point wanted is across the square.
        _stage.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _dot) return;
            var point = e.GetPosition(_stage);
            MoveDot(point.X / StageWidth, point.Y / StageHeight);
        };

        MoveDot(layer.AnchorX, layer.AnchorY);
    }

    private double AnchorX => (Canvas.GetLeft(_dot) + DotSize / 2) / StageWidth;
    private double AnchorY => (Canvas.GetTop(_dot) + DotSize / 2) / StageHeight;

    private void Dot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(_stage);
        MoveDot(point.X / StageWidth, point.Y / StageHeight);
    }

    private void MoveDot(double x, double y)
    {
        x = Snap(Math.Clamp(x, 0, 1)); y = Snap(Math.Clamp(y, 0, 1));
        Canvas.SetLeft(_dot, x * StageWidth - DotSize / 2);
        Canvas.SetTop(_dot, y * StageHeight - DotSize / 2);
        _readout.Text = $"ANCHOR  X {x.ToString("0.###", CultureInfo.InvariantCulture)}   Y {y.ToString("0.###", CultureInfo.InvariantCulture)}"
            + (Math.Abs(x - .5) < .0005 && Math.Abs(y - .5) < .0005 ? "   (centre)" : "");
    }

    /// <summary>Pulls the dot onto the guides when it is close, so exact points are easy.</summary>
    private static double Snap(double value)
    {
        foreach (var stop in new[] { 0, .25, .5, .75, 1d })
            if (Math.Abs(value - stop) < .022) return stop;
        return value;
    }

    private void AddGuides()
    {
        foreach (var fraction in new[] { .25, .5, .75 })
        {
            var thick = Math.Abs(fraction - .5) < .0001;
            var brush = new SolidColorBrush(thick ? Color.FromRgb(69, 210, 235) : Color.FromRgb(38, 74, 104));
            _stage.Children.Add(new Line
            {
                X1 = fraction * StageWidth, X2 = fraction * StageWidth, Y1 = 0, Y2 = StageHeight,
                Stroke = brush, StrokeThickness = thick ? 1.25 : 1,
                StrokeDashArray = new DoubleCollection([5, 3]), IsHitTestVisible = false,
            });
            _stage.Children.Add(new Line
            {
                X1 = 0, X2 = StageWidth, Y1 = fraction * StageHeight, Y2 = fraction * StageHeight,
                Stroke = brush, StrokeThickness = thick ? 1.25 : 1,
                StrokeDashArray = new DoubleCollection([5, 3]), IsHitTestVisible = false,
            });
        }
    }
}
