using Post.Core;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Post.App;

/// <summary>
/// Shows Post's effects on the moving picture. Colour corrections and LUTs are baked
/// on the CPU into one lookup table (see <see cref="PreviewLut"/>) that the shader
/// samples, so a LUT previews as cheaply as a brightness tweak. The exported render is
/// still produced by ffmpeg, and the paused frame is rendered through the real filters.
/// </summary>
internal sealed class PreviewShaderEffect : ShaderEffect
{
    // Two builds: the plain one stays cheap, the blur one adds ring taps. A single
    // shader with a branch does not fit in ps_2_0's register budget.
    private static readonly PixelShader ColorShader = new() { UriSource = new Uri("pack://application:,,,/Post;component/Assets/PreviewEffect.ps") };
    private static readonly PixelShader BlurShader = new() { UriSource = new Uri("pack://application:,,,/Post;component/Assets/PreviewEffectBlur.ps") };
    private static ImageBrush? _identityTable;

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(PreviewShaderEffect), 0);
    public static readonly DependencyProperty ColorTableProperty = RegisterPixelShaderSamplerProperty(nameof(ColorTable), typeof(PreviewShaderEffect), 1);
    public static readonly DependencyProperty LutSizeProperty = DependencyProperty.Register(nameof(LutSize), typeof(double), typeof(PreviewShaderEffect), new UIPropertyMetadata((double)PreviewLut.DefaultSize, PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty VignetteProperty = DependencyProperty.Register(nameof(Vignette), typeof(double), typeof(PreviewShaderEffect), new UIPropertyMetadata(0d, PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty BlurRadiusProperty = DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(PreviewShaderEffect), new UIPropertyMetadata(0d, PixelShaderConstantCallback(2)));

    public PreviewShaderEffect(bool withBlur = false)
    {
        PixelShader = withBlur ? BlurShader : ColorShader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(ColorTableProperty);
        UpdateShaderValue(LutSizeProperty);
        UpdateShaderValue(VignetteProperty);
        if (withBlur) UpdateShaderValue(BlurRadiusProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush ColorTable { get => (Brush)GetValue(ColorTableProperty); set => SetValue(ColorTableProperty, value); }
    public double LutSize { get => (double)GetValue(LutSizeProperty); set => SetValue(LutSizeProperty, value); }
    public double Vignette { get => (double)GetValue(VignetteProperty); set => SetValue(VignetteProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }

    /// <summary>
    /// Folds an effect stack into a shader, or returns null when nothing in it can be
    /// shown live (sharpen only appears in the render).
    /// </summary>
    public static PreviewShaderEffect? For(IReadOnlyList<VideoEffect> effects, ColorGrade? working = null)
    {
        double vignette = 0, blur = 0;
        var used = false;
        foreach (var effect in effects)
        {
            if (!effect.IsEnabled) continue;
            switch (effect.Kind)
            {
                case VideoEffectKind.Vignette:
                    // The export widens the lens angle by 1.2; match that so the preview
                    // darkens by roughly the same amount rather than noticeably less.
                    vignette = Math.Clamp(vignette + Math.Clamp(effect.Amount, 0, 1) * 1.2, 0, 1); used = true;
                    break;
                case VideoEffectKind.Blur:
                    // The export blurs with sigma = amount * 25 px; scale that to the
                    // texture coordinates of a nominally 1920-wide frame.
                    blur = Math.Clamp(blur + Math.Clamp(effect.Amount, 0, 1) * 25 / 1920, 0, .05); used = true;
                    break;
            }
        }

        var strip = PreviewLut.BuildStrip(effects, working);
        if (strip is null && !used) return null;

        return new PreviewShaderEffect(blur > 0)
        {
            ColorTable = strip is null ? IdentityTable() : TableBrush(strip),
            LutSize = PreviewLut.DefaultSize,
            Vignette = vignette,
            BlurRadius = blur,
        };
    }

    /// <summary>Wraps a baked strip as a brush the shader can sample.</summary>
    private static ImageBrush TableBrush(byte[] strip)
    {
        var size = PreviewLut.DefaultSize;
        var bitmap = new WriteableBitmap(size * size, size, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size * size, size), strip, size * size * 4, 0);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.RelativeToBoundingBox };
        brush.Freeze();
        return brush;
    }

    /// <summary>A pass-through table, used when only a vignette or blur is applied.</summary>
    private static ImageBrush IdentityTable()
    {
        if (_identityTable is not null) return _identityTable;
        var size = PreviewLut.DefaultSize;
        var strip = new byte[size * size * size * 4];
        var step = 255d / (size - 1);
        for (var blue = 0; blue < size; blue++)
            for (var green = 0; green < size; green++)
                for (var red = 0; red < size; red++)
                {
                    var index = (green * size * size + blue * size + red) * 4;
                    strip[index + 0] = (byte)Math.Round(blue * step);
                    strip[index + 1] = (byte)Math.Round(green * step);
                    strip[index + 2] = (byte)Math.Round(red * step);
                    strip[index + 3] = 255;
                }
        return _identityTable = TableBrush(strip);
    }
}
