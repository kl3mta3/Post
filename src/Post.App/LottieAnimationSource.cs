using SkiaSharp;
using SkiaSharp.Skottie;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Post.App;

/// <summary>
/// Renders a Lottie animation (.json, or the JSON inside a .lottie) with Skottie.
/// The same instance feeds the live preview, a frame at a time, and the exporter,
/// which writes a numbered PNG sequence for ffmpeg to composite.
/// </summary>
internal sealed class LottieAnimationSource : IDisposable
{
    private readonly Animation _animation;
    private readonly object _gate = new();
    private bool _disposed;

    private LottieAnimationSource(Animation animation, string path)
    {
        _animation = animation; Path = path;
        Duration = TimeSpan.FromSeconds(animation.Duration.TotalSeconds > 0 ? animation.Duration.TotalSeconds : 1);
    }

    public string Path { get; }
    public TimeSpan Duration { get; }
    public double FrameRate => _animation.Fps > 0 ? _animation.Fps : 30;
    public SKSize Size => _animation.Size;

    /// <summary>Loads an animation, or returns null when the file is not valid Lottie.</summary>
    public static LottieAnimationSource? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // TryParse takes the JSON itself; TryCreate's string overload takes a path.
            if (Animation.TryParse(File.ReadAllText(path), out var parsed) && parsed is not null) return new LottieAnimationSource(parsed, path);
            return Animation.TryCreate(path, out var created) && created is not null ? new LottieAnimationSource(created, path) : null;
        }
        catch { return null; }
    }

    /// <summary>Renders one frame into a fresh BGRA bitmap for the preview.</summary>
    public BitmapSource? RenderFrame(TimeSpan time, int width, int height)
    {
        width = Math.Clamp(width, 2, 4096); height = Math.Clamp(height, 2, 4096);
        var pixels = RenderPixels(time, width, height);
        if (pixels is null) return null;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Writes the frames covering <paramref name="duration"/> as frame_00000.png and on,
    /// which the compositor takes as an image sequence input.
    /// </summary>
    public int RenderSequence(string folder, int width, int height, double fps, TimeSpan duration, CancellationToken token = default)
    {
        Directory.CreateDirectory(folder);
        var frames = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * fps));
        for (var index = 0; index < frames; index++)
        {
            token.ThrowIfCancellationRequested();
            // Looping keeps a short animation covering a longer overlay.
            var time = TimeSpan.FromSeconds(index / fps % Math.Max(.001, Duration.TotalSeconds));
            var pixels = RenderPixels(time, width, height);
            if (pixels is null) continue;
            using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(System.IO.Path.Combine(folder, $"frame_{index:00000}.png"));
            data.SaveTo(stream);
        }
        return frames;
    }

    private byte[]? RenderPixels(TimeSpan time, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            _animation.SeekFrameTime(time.TotalSeconds);
            _animation.Render(surface.Canvas, new SKRect(0, 0, width, height));
            surface.Canvas.Flush();
            var pixels = new byte[info.BytesSize];
            unsafe
            {
                fixed (byte* buffer = pixels)
                    if (!surface.ReadPixels(info, (IntPtr)buffer, info.RowBytes, 0, 0)) return null;
            }
            return pixels;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _animation.Dispose();
        }
    }
}
