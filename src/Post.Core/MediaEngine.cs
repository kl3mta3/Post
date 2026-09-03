using System.Globalization;
using System.Collections.Concurrent;

namespace Post.Core;

public sealed class MediaEngine(FfmpegTools tools, IProcessRunner runner)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ThumbnailLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly VideoEncoderCatalog _encoders = new(tools, runner);

    /// <summary>Which H.264 encoder to use: "auto", "cpu", or an encoder name.</summary>
    public string EncoderPreference { get; set; } = "auto";

    /// <summary>Every encoder this machine can actually run, fastest first.</summary>
    public Task<IReadOnlyList<VideoEncoder>> AvailableEncodersAsync(CancellationToken token = default) => _encoders.AvailableAsync(token);

    private Task<VideoEncoder> EncoderAsync(CancellationToken token) => _encoders.ResolveAsync(EncoderPreference, token);
    /// <summary>
    /// A cap on the canvas rotation pads out to. An anchor in a corner of a 1080p frame
    /// would otherwise ask for a 4400 pixel square, which costs far more than the turn
    /// is worth; past this the far corners clip instead.
    /// </summary>
    private const int MaximumRotationSide = 2600;

    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Ts(TimeSpan value) => S(value.TotalSeconds);

    /// <summary>
    /// Runs ffmpeg while translating its own -progress stream into a fraction inside
    /// from..to. Without a known total duration this behaves like a plain run.
    /// </summary>
    private async Task RunFfmpegAsync(List<string> args, string operation, string stage, TimeSpan total, double from, double to, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        if (progress is null || total <= TimeSpan.Zero)
        {
            progress?.Report(new(from, stage));
            var plain = await runner.RunAsync(tools.Ffmpeg, args, token);
            if (plain.ExitCode != 0 && !token.IsCancellationRequested && VideoEncoderCatalog.TryFallbackToCpu(args))
                plain = await runner.RunAsync(tools.Ffmpeg, args, token);
            plain.EnsureSuccess(operation);
            progress?.Report(new(to, stage)); return;
        }
        progress.Report(new(from, stage));
        var reporter = new Progress<FfmpegProgress>(value =>
        {
            var done = Math.Clamp(value.OutTime.TotalSeconds / total.TotalSeconds, 0, 1);
            progress.Report(new(from + (to - from) * done, stage));
        });
        var full = new List<string>(args.Count + 3) { "-nostats", "-progress", "pipe:1" }; full.AddRange(args);
        var result = await runner.RunAsync(tools.Ffmpeg, full, reporter, token);
        if (result.ExitCode != 0 && !token.IsCancellationRequested && VideoEncoderCatalog.TryFallbackToCpu(args))
        {
            progress.Report(new(from, stage));
            full = ["-nostats", "-progress", "pipe:1", .. args];
            result = await runner.RunAsync(tools.Ffmpeg, full, reporter, token);
        }
        result.EnsureSuccess(operation);
        progress.Report(new(to, stage));
    }

    public async Task<string> CreatePreviewProxyAsync(MediaInfo media, string cacheDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        if (media.IsStillImage) return media.Path;
        if (Path.GetExtension(media.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && media.VideoCodec is "h264" or "hevc") return media.Path;
        var extension = Path.GetExtension(media.Path).ToLowerInvariant();
        if (!media.HasVideo && extension is ".mp3" or ".wav" or ".wma") return media.Path;
        var output = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(media.Path)}-{Math.Abs(media.Path.GetHashCode())}.preview{(media.HasVideo ? ".mp4" : ".m4a")}");
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(media.Path)) return output;
        var proxyEncoder = media.HasVideo ? await EncoderAsync(token) : VideoEncoder.Cpu;
        var args = new List<string> { "-y", "-i", media.Path };
        if (media.HasVideo)
            args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-vf", "scale='min(1920,iw)':-2", "-c:v", proxyEncoder.Name,
                .. proxyEncoder.SpeedArgs(EncodeSpeed.Fast), .. proxyEncoder.QualityArgs(23), "-c:a", "aac", "-b:a", "160k"]);
        else args.AddRange(["-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        var result = await runner.RunAsync(tools.Ffmpeg, args, token);
        if (result.ExitCode != 0 && !token.IsCancellationRequested && VideoEncoderCatalog.TryFallbackToCpu(args))
            result = await runner.RunAsync(tools.Ffmpeg, args, token);
        result.EnsureSuccess("Preview preparation"); return output;
    }

    /// <summary>
    /// Builds a preview file whose audio has been run through the equalizer, so the
    /// live player can hear the EQ. Video is stream-copied, so only the audio is
    /// re-encoded and the result is cached per equalizer setting.
    /// </summary>
    public async Task<string> CreateEqualizedPreviewAsync(string input, bool hasVideo, AudioEqualizer equalizer, string cacheDirectory, CancellationToken token = default)
    {
        var filters = equalizer.BuildFilters();
        if (filters.Count == 0) return input;
        Directory.CreateDirectory(cacheDirectory);
        var chain = string.Join(',', filters);
        var output = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(input)}-{Math.Abs(input.GetHashCode())}.eq-{Signature(chain)}{(hasVideo ? ".mp4" : ".m4a")}");
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(input)) return output;
        var args = new List<string> { "-y", "-i", input };
        if (hasVideo) args.AddRange(["-map", "0:v:0", "-map", "0:a:0", "-c:v", "copy"]); else args.AddRange(["-map", "0:a:0", "-vn"]);
        args.AddRange(["-af", chain, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output]);
        var result = await runner.RunAsync(tools.Ffmpeg, args, token);
        result.EnsureSuccess("Equalized preview"); return output;
    }

    private static string Signature(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    public async Task<string?> CreateWaveformAsync(MediaInfo media, string cacheDirectory, int width = 1800, int height = 120, CancellationToken token = default)
    {
        if (!media.HasAudio) return null;
        Directory.CreateDirectory(cacheDirectory);
        width = Math.Clamp(width, 600, 24000); height = Math.Clamp(height, 40, 240);
        var output = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(media.Path)}-{Math.Abs(media.Path.GetHashCode())}.wave-{width}x{height}.png");
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(media.Path)) return output;
        var filter = $"aformat=channel_layouts=stereo,compand=attacks=0:decays=0.25:points=-80/-80|-35/-12|-12/-4|0/0,showwavespic=s={width}x{height}:split_channels=1:colors=55d9e8|36b4c6";
        var result = await runner.RunAsync(tools.Ffmpeg, ["-y", "-i", media.Path, "-filter_complex", filter, "-frames:v", "1", output], token);
        result.EnsureSuccess("Waveform generation"); return output;
    }

    public async Task<string> CreateThumbnailStripAsync(MediaInfo media, string cacheDirectory, int frameCount = 12, int frameWidth = 160, int frameHeight = 90, CancellationToken token = default)
    {
        Directory.CreateDirectory(cacheDirectory); frameCount = Math.Clamp(frameCount, 4, 120);
        var output = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(media.Path)}-{Math.Abs(media.Path.GetHashCode())}.filmstrip-{frameCount}.jpg");
        var gate = ThumbnailLocks.GetOrAdd(output, _ => new SemaphoreSlim(1, 1)); await gate.WaitAsync(token);
        try
        {
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(media.Path)) return output;
        var fps = frameCount / Math.Max(.1, media.Duration.TotalSeconds);
        var filter = $"fps={S(fps)},scale={frameWidth}:{frameHeight}:force_original_aspect_ratio=decrease,pad={frameWidth}:{frameHeight}:(ow-iw)/2:(oh-ih)/2:color=black,tile={frameCount}x1";
        var temporary = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(output)}-{Guid.NewGuid():N}.jpg");
        try
        {
            var result = await runner.RunAsync(tools.Ffmpeg, ["-y", "-i", media.Path, "-vf", filter, "-frames:v", "1", "-update", "1", "-q:v", "3", temporary], token);
            result.EnsureSuccess("Thumbnail strip generation"); File.Move(temporary, output, true); return output;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    public async Task CaptureFrameAsync(string input, TimeSpan position, string output, CancellationToken token = default, IEnumerable<VideoEffect>? effects = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var args = new List<string> { "-y", "-ss", Ts(position), "-i", input };
        var filters = VideoEffects.Build(effects);
        if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
        args.AddRange(["-frames:v", "1", "-q:v", "1", output]);
        var result = await runner.RunAsync(tools.Ffmpeg, args, token);
        result.EnsureSuccess("Frame capture");
    }

    public async Task ExportAsync(ClipItem clip, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        progress?.Report(new(0.02, "Preparing export"));
        var extension = Path.GetExtension(output);
        // Effects require re-encoding, so they rule out the stream-copy fast path.
        var lossless = !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) && options.Mode == ExportMode.Lossless && options.Speed == 1 && options.Volume == 1 && options.Aspect == AspectPreset.Original && options.CropZoom == 1 && !VideoEffects.HasAny(options.Effects) && (options.Equalizer?.IsFlat ?? true);
        if (lossless)
        {
            if (clip.Segments.Count == 1) await StreamCopyAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, output, token, "Copying selection", .02, 1, progress);
            else await ExportWithSegmentsAsync(clip, output, true, token, .02, 1, progress);
        }
        else if (options.Mode == ExportMode.Gif) await ExportGifAsync(clip, output, options, token, progress);
        else if (clip.Segments.Count == 1)
        {
            await EncodeAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, clip.Media, output, options, token, .04, 1, progress);
        }
        else
        {
            var temp = Path.Combine(Path.GetTempPath(), $"post-pre-{Guid.NewGuid():N}.mkv");
            try { await ExportWithSegmentsAsync(clip, temp, true, token, .02, .3, progress); await EncodeAsync(temp, TimeSpan.Zero, clip.SelectedDuration, clip.Media, output, options, token, .3, 1, progress); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        progress?.Report(new(1, "Export complete"));
    }

    public async Task ExportCompositionAsync(TimelineComposition composition, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        if (!composition.HasVisibleMedia && !composition.HasVisibleGraphics) throw new InvalidOperationException("No visible timeline layers contain media.");
        if (composition.OutputDuration <= TimeSpan.Zero) throw new InvalidOperationException("The layered timeline has no exportable duration.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (options.Mode == ExportMode.Gif)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"post-composite-{Guid.NewGuid():N}.mp4");
            try
            {
                await ExportCompositionVideoAsync(composition, temp, options with { Mode = ExportMode.Lossless }, false, progress, token);
                var first = composition.Layers.Where(layer => layer.IsVisible).SelectMany(layer => layer.Placements).First().Clip.Media;
                var (width, height) = CompositionSize(first, options, false);
                var duration = TimeSpan.FromSeconds(composition.OutputDuration.TotalSeconds / Math.Max(.01, options.Speed));
                var rendered = new MediaInfo(temp, duration, width, height, 30, "h264", null, 0);
                await ExportGifFileAsync(temp, TimeSpan.Zero, duration, rendered, output, options with { Aspect = AspectPreset.Original, CropZoom = 1, PanX = 0, PanY = 0, Speed = 1 }, token);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return;
        }
        await ExportCompositionVideoAsync(composition, output, options, false, progress, token);
    }

    public Task CreateCompositionPreviewAsync(TimelineComposition composition, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
        => ExportCompositionVideoAsync(composition, output, options with { Mode = ExportMode.Lossless, Speed = 1, Volume = 1 }, true, progress, token);

    public async Task ExportCompositionAudioAsync(TimelineComposition composition, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        var placements = composition.Layers.Where(layer => layer.IsVisible && !LayerAudioFullyMuted(layer)).SelectMany(layer => layer.Placements.Select(placement => (Layer: layer, Placement: placement))).Where(item => item.Placement.Clip.Media.HasAudio).ToArray();
        if (placements.Length == 0) throw new InvalidOperationException("No visible, unmuted timeline layers contain audio.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = Path.Combine(Path.GetTempPath(), $"post-audio-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            var rendered = new Dictionary<ClipItem, string>();
            foreach (var clip in placements.Select(item => item.Placement.Clip).Distinct())
            {
                token.ThrowIfCancellationRequested(); var part = Path.Combine(temp, $"source-{rendered.Count:000}.wav");
                var sourceCount = placements.Select(item => item.Placement.Clip).Distinct().Count();
                var low = .1 + .3 * rendered.Count / sourceCount; var high = .1 + .3 * (rendered.Count + 1) / sourceCount;
                await RenderCompositionAudioSourceAsync(clip, part, token, $"Preparing audio {rendered.Count + 1} of {sourceCount}", low, high, progress);
                rendered[clip] = part;
            }
            var args = new List<string> { "-y" }; foreach (var item in placements) args.AddRange(["-i", rendered[item.Placement.Clip]]);
            var filters = new List<string>(); var inputs = new List<string>();
            for (var i = 0; i < placements.Length; i++)
            {
                var item = placements[i]; var placement = item.Placement; var name = $"a{i}"; var delay = Math.Max(0, (long)placement.Start.TotalMilliseconds);
                var volume = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Volume, 1);
                filters.Add($"[{i}:a]atrim=start={Ts(placement.InPoint)}:end={Ts(placement.InPoint + placement.Duration)},asetpts=PTS-STARTPTS,volume='({volume})*{S(LayerVolume(item.Layer))}':eval=frame,adelay={delay}:all=1{AudioChannelFilter(item.Layer)}[{name}]"); inputs.Add($"[{name}]");
            }
            var mixed = "mixed"; if (inputs.Count == 1) filters.Add($"{inputs[0]}anull[{mixed}]"); else filters.Add($"{string.Concat(inputs)}amix=inputs={inputs.Count}:duration=longest:dropout_transition=0[{mixed}]");
            var finalFilters = new List<string>(); if (options.Speed != 1) finalFilters.AddRange(Atempo(options.Speed)); if (options.Volume != 1) finalFilters.Add($"volume={S(options.Volume)}");
            finalFilters.AddRange(composition.Equalizer.BuildFilters());
            if (finalFilters.Count > 0) filters.Add($"[{mixed}]{string.Join(',', finalFilters)}[outa]"); else filters.Add($"[{mixed}]anull[outa]");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[outa]"]); AddAudioCodec(args, output, options.AudioBitrateKbps); args.Add(output);
            var mixDuration = TimeSpan.FromSeconds(composition.OutputDuration.TotalSeconds / Math.Max(.01, options.Speed));
            await RunFfmpegAsync(args, "Audio export", "Mixing audio layers", mixDuration, .45, 1, progress, token); progress?.Report(new(1, "Audio export complete"));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private async Task RenderCompositionAudioSourceAsync(ClipItem clip, string output, CancellationToken token, string stage = "Preparing audio", double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var filters = new List<string>();
        for (var i = 0; i < clip.Segments.Count; i++) { var segment = clip.Segments[i]; filters.Add($"[0:a]atrim=start={Ts(segment.SourceStart)}:end={Ts(segment.SourceEnd)},asetpts=PTS-STARTPTS[a{i}]"); }
        var inputs = string.Concat(Enumerable.Range(0, clip.Segments.Count).Select(i => $"[a{i}]")); filters.Add($"{inputs}concat=n={clip.Segments.Count}:v=0:a=1[outa]");
        var args = new List<string> { "-y", "-i", clip.SourcePath, "-filter_complex", string.Join(';', filters), "-map", "[outa]", "-c:a", "pcm_s16le", output };
        await RunFfmpegAsync(args, "Audio source preparation", stage, clip.SelectedDuration, from, to, progress, token);
    }

    private static void AddAudioCodec(List<string> args, string output, int bitrateKbps)
    {
        var bitrate = Math.Clamp(bitrateKbps, 64, 512); var extension = Path.GetExtension(output).ToLowerInvariant();
        switch (extension)
        {
            case ".wav": args.AddRange(["-c:a", "pcm_s16le"]); break;
            case ".flac": args.AddRange(["-c:a", "flac"]); break;
            case ".m4a": args.AddRange(["-c:a", "aac", "-b:a", $"{bitrate}k"]); break;
            case ".ogg": args.AddRange(["-c:a", "libvorbis", "-b:a", $"{bitrate}k"]); break;
            default: args.AddRange(["-c:a", "libmp3lame", "-b:a", $"{bitrate}k"]); break;
        }
    }

    private async Task ExportCompositionVideoAsync(TimelineComposition composition, string output, ExportOptions options, bool preview, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        var layers = composition.Layers.Where(layer => layer.IsVisible).ToArray();
        // Layer 1 is the top visual layer in the editor. Build the compositor
        // bottom-to-top so earlier rows overlay later rows, matching live preview.
        var placements = layers.Reverse().Where(layer => layer.Kind != TimelineLayerKind.Graphics).SelectMany((layer, layerIndex) => layer.Placements.Select(placement => (Layer: layer, LayerIndex: layerIndex, Placement: placement))).ToArray();
        var visualPlacements = placements.Select((item, inputIndex) => (Item: item, InputIndex: inputIndex)).Where(value => value.Item.Layer.Kind == TimelineLayerKind.Video).ToArray();
        var graphics = layers.Reverse().SelectMany(layer => layer.Graphics).Where(graphic => !string.IsNullOrWhiteSpace(graphic.RenderedImagePath) && GraphicExists(graphic.RenderedImagePath)).ToArray();
        if (placements.Length == 0 && graphics.Length == 0) throw new InvalidOperationException("No visible timeline layers contain media.");
        var duration = composition.OutputDuration;
        var sizingMedia = visualPlacements.Length > 0 ? visualPlacements[0].Item.Placement.Clip.Media : placements.Length > 0 && placements[0].Placement.Clip.Media.HasVideo ? placements[0].Placement.Clip.Media : new MediaInfo("", duration, 1920, 1080, 30, "generated", null, 0);
        var (width, height) = CompositionSize(sizingMedia, options, preview);
        var temp = Path.Combine(Path.GetTempPath(), $"post-layers-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            progress?.Report(new(.04, "Preparing timeline layers"));
            var renderedClips = new Dictionary<ClipItem, string>();
            var sources = placements.Select(item => item.Placement.Clip).Distinct().ToArray();
            foreach (var clip in sources)
            {
                token.ThrowIfCancellationRequested();
                // Composition inputs must share a broadly supported intermediate format.
                // Stream-copying raw BGR AVI (and several other codecs) into Matroska is
                // invalid, so normalize every source to H.264/AAC before overlaying it.
                // An uncut source already in that format needs no pass at all: re-encoding
                // it whole is the slowest thing an export can do, and it costs a generation
                // of quality on the way.
                if (NeedsNoPreparation(clip)) { renderedClips[clip] = clip.SourcePath; continue; }
                var part = Path.Combine(temp, $"source-{renderedClips.Count:000}.mp4");
                var low = .05 + .25 * renderedClips.Count / sources.Length; var high = .05 + .25 * (renderedClips.Count + 1) / sources.Length;
                await RenderCompositionSourceAsync(clip, part, token, $"Preparing {clip.DisplayName} ({renderedClips.Count + 1} of {sources.Length})", low, high, progress);
                renderedClips[clip] = part;
            }

            var args = new List<string> { "-y" };
            foreach (var item in placements) args.AddRange(["-i", renderedClips[item.Placement.Clip]]);
            // A still image is looped by the demuxer; an animated source (GIF/APNG/WebP)
            // is looped as a whole stream so it keeps playing for the overlay's duration.
            foreach (var graphic in graphics)
            {
                var path = graphic.RenderedImagePath!;
                if (IsImageSequence(path)) args.AddRange(["-framerate", "30", "-start_number", "0", "-i", path]);
                else if (IsAnimatedImage(path)) args.AddRange(["-stream_loop", "-1", "-i", path]);
                else args.AddRange(["-loop", "1", "-i", path]);
            }
            var filters = new List<string> { $"color=c=black:s={width}x{height}:r=30:d={Ts(duration)}[base]" };
            var placementPositions = new Dictionary<int, (string X, string Y)>();
            foreach (var visual in visualPlacements)
            {
                var placement = visual.Item.Placement; var input = visual.InputIndex;
                var source = $"v{input}";
                filters.Add($"[{input}:v]trim=start={Ts(placement.InPoint)}:end={Ts(placement.InPoint + placement.Duration)},setpts=PTS-STARTPTS+{Ts(placement.Start)}/TB,scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[vp{input}]");
                // Animated clips are resized by scale and then placed by the overlay filter.
                // Both accept per-frame expressions, which is what reproduces the editor's
                // transform: scaled about the centre, then offset by (position - 0.5).
                // geq evaluates an expression per pixel per frame and dominates render time,
                // so it is only worth adding when the opacity is genuinely animated.
                var turns = Rotation.Turns(placement.Keyframes, placement.SpinDegreesPerSecond);
                var moves = turns || placement.Keyframes.Any(item => item.Property is KeyframeProperty.PositionX or KeyframeProperty.PositionY or KeyframeProperty.Scale);
                var fades = placement.Keyframes.Any(item => item.Property is KeyframeProperty.Opacity);
                var clipEffects = VideoEffects.Build(placement.Effects);
                if (moves || fades)
                {
                    var offsetSeconds = placement.Start.TotalSeconds;
                    var chain = new List<string>(clipEffects) { "fps=30", "format=rgba" };
                    if (moves)
                    {
                        var scale = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Scale, 1, "t", offsetSeconds);
                        var x = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.PositionX, .5, "t", offsetSeconds);
                        var y = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.PositionY, .5, "t", offsetSeconds);
                        chain.Add($"scale=w='max(2,trunc({width}*({scale})/2)*2)':h='max(2,trunc({height}*({scale})/2)*2)':eval=frame");
                        placementPositions[input] = ($"(W-w)/2+(({x})-0.5)*W", $"(H-h)/2+(({y})-0.5)*H");
                    }
                    if (turns)
                    {
                        // Rotating about the layer's anchor rather than the frame centre.
                        var angle = Rotation.AngleExpression(placement.Keyframes, placement.SpinDegreesPerSecond, offsetSeconds);
                        var plan = Rotation.Build(width, height, visual.Item.Layer.AnchorX, visual.Item.Layer.AnchorY, angle, MaximumRotationSide);
                        chain.AddRange(plan.Filters);
                        var (positionX, positionY) = placementPositions.TryGetValue(input, out var placed)
                            ? placed : ("(W-w)/2", "(H-h)/2");
                        placementPositions[input] = ($"({positionX})-{plan.OffsetX}", $"({positionY})-{plan.OffsetY}");
                    }
                    if (fades)
                    {
                        var ramps = OpacityFades.TryBuild(placement.Keyframes, offsetSeconds);
                        if (ramps is not null) chain.AddRange(ramps);
                        else
                        {
                            var opacity = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Opacity, 1, "T", offsetSeconds);
                            chain.Add($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({opacity})':enable='between(t,{Ts(placement.Start)},{Ts(placement.Start + placement.Duration)})'");
                        }
                    }
                    filters.Add($"[vp{input}]{string.Join(',', chain)}[{source}]");
                }
                else filters.Add($"[vp{input}]{(clipEffects.Count > 0 ? string.Join(',', clipEffects) : "null")}[{source}]");
            }
            var video = "base";
            for (var i = 0; i < visualPlacements.Length; i++)
            {
                var visual = visualPlacements[i]; var input = visual.InputIndex; var next = $"overlay{i}";
                var position = placementPositions.TryGetValue(input, out var value) ? $"x='{value.X}':y='{value.Y}':eval=frame" : "0:0";
                filters.Add($"[{video}][v{input}]overlay={position}:eof_action=pass:shortest=0[{next}]"); video = next;
            }
            for (var i = 0; i < graphics.Length; i++)
            {
                var graphic = graphics[i]; var input = placements.Length + i; var start = graphic.Start.TotalSeconds;
                // The overlay is rendered at its own size and positioned by the overlay
                // filter, whose x/y take per-frame expressions. This matches the editor:
                // a box of Width x Height (times Scale), top-left corner at X*W / Y*H.
                var scaleAnimated = graphic.Keyframes.Any(item => item.Property == KeyframeProperty.Scale);
                var fixedScale = scaleAnimated ? 1 : Math.Clamp(KeyframeEvaluator.Evaluate(graphic.Keyframes, KeyframeProperty.Scale, TimeSpan.Zero, 1), .01, 8);
                var graphicWidth = Math.Clamp((int)Math.Round(width * graphic.Width * fixedScale), 2, width); var graphicHeight = Math.Clamp((int)Math.Round(height * graphic.Height * fixedScale), 2, height);
                var fit = graphic.PreserveAspectRatio ? $"scale={graphicWidth}:{graphicHeight}:force_original_aspect_ratio=decrease,pad={graphicWidth}:{graphicHeight}:(ow-iw)/2:(oh-ih)/2:color=0x00000000" : $"scale={graphicWidth}:{graphicHeight}";
                var chain = new List<string> { "fps=30", fit, "format=rgba" };
                if (scaleAnimated)
                {
                    var scale = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.Scale, 1, "t", start);
                    chain.Add($"scale=w='max(2,trunc({graphicWidth}*({scale})/2)*2)':h='max(2,trunc({graphicHeight}*({scale})/2)*2)':eval=frame");
                }
                // A constant opacity is a cheap alpha multiply, and a plain fade in or out is
                // a cheap ramp. Only an arbitrary opacity curve is worth geq, which costs an
                // expression evaluation per pixel per frame and can dominate a whole export.
                var constantOpacity = Math.Clamp(graphic.Opacity, 0, 1);
                var opacityRamps = graphic.Keyframes.Any(item => item.Property == KeyframeProperty.Opacity)
                    ? OpacityFades.TryBuild(graphic.Keyframes, start) : null;
                if (constantOpacity < .999) chain.Add($"colorchannelmixer=aa={S(constantOpacity)}");
                if (opacityRamps is not null) chain.AddRange(opacityRamps);
                else if (graphic.Keyframes.Any(item => item.Property == KeyframeProperty.Opacity))
                    chain.Add($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.Opacity, constantOpacity, "T", start)})':enable='between(t,{Ts(graphic.Start)},{Ts(graphic.End)})'");
                // Rotation goes on last, about the layer's anchor, and shifts the overlay
                // back by the padding it added so the anchor stays put.
                var turnOffsetX = 0; var turnOffsetY = 0;
                if (Rotation.Turns(graphic.Keyframes, graphic.SpinDegreesPerSecond))
                {
                    var angle = Rotation.AngleExpression(graphic.Keyframes, graphic.SpinDegreesPerSecond, start);
                    var layer = composition.Layers.FirstOrDefault(item => item.Graphics.Contains(graphic));
                    var plan = Rotation.Build(graphicWidth, graphicHeight, layer?.AnchorX ?? .5, layer?.AnchorY ?? .5, angle, MaximumRotationSide);
                    chain.AddRange(plan.Filters);
                    turnOffsetX = plan.OffsetX; turnOffsetY = plan.OffsetY;
                }
                filters.Add($"[{input}:v]{string.Join(',', chain)}[g{i}]");
                var positionX = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.PositionX, graphic.X, "t", start);
                var positionY = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.PositionY, graphic.Y, "t", start);
                var next = $"graphic{i}";
                filters.Add($"[{video}][g{i}]overlay=x='W*({positionX})-{turnOffsetX}':y='H*({positionY})-{turnOffsetY}':eval=frame:enable='between(t,{Ts(graphic.Start)},{Ts(graphic.End)})':eof_action=pass:shortest=0[{next}]"); video = next;
            }
            var videoSpeed = Math.Max(.01, options.Speed);
            // Output effects run on the finished frame, after every layer is composited.
            var outputChain = new List<string>(VideoEffects.Build(composition.OutputEffects)) { $"trim=duration={Ts(duration)}", $"setpts=(PTS-STARTPTS)/{S(videoSpeed)}" };
            filters.Add($"[{video}]{string.Join(',', outputChain)}[outv]");

            var audioInputs = new List<string>();
            for (var i = 0; i < placements.Length; i++)
            {
                var item = placements[i];
                if (LayerAudioFullyMuted(item.Layer) || !item.Placement.Clip.Media.HasAudio) continue;
                var name = $"a{i}"; var delay = Math.Max(0, (long)item.Placement.Start.TotalMilliseconds);
                var volume = KeyframeEvaluator.BuildFfmpegExpression(item.Placement.Keyframes, KeyframeProperty.Volume, 1);
                filters.Add($"[{i}:a]atrim=start={Ts(item.Placement.InPoint)}:end={Ts(item.Placement.InPoint + item.Placement.Duration)},asetpts=PTS-STARTPTS,volume='({volume})*{S(LayerVolume(item.Layer))}':eval=frame,adelay={delay}:all=1{AudioChannelFilter(item.Layer)}[{name}]"); audioInputs.Add($"[{name}]");
            }
            var audio = "mixed";
            if (audioInputs.Count == 0) filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={Ts(duration)}[{audio}]");
            else if (audioInputs.Count == 1) filters.Add($"{audioInputs[0]}apad=pad_dur={Ts(duration)},atrim=duration={Ts(duration)}[{audio}]");
            else filters.Add($"{string.Concat(audioInputs)}amix=inputs={audioInputs.Count}:duration=longest:dropout_transition=0,apad=pad_dur={Ts(duration)},atrim=duration={Ts(duration)}[{audio}]");
            var audioFilters = new List<string>(); if (options.Speed != 1) audioFilters.AddRange(Atempo(options.Speed)); if (options.Volume != 1) audioFilters.Add($"volume={S(options.Volume)}");
            audioFilters.AddRange(composition.Equalizer.BuildFilters());
            if (audioFilters.Count > 0) filters.Add($"[{audio}]{string.Join(',', audioFilters)}[outa]"); else filters.Add($"[{audio}]anull[outa]");

            var webm = !preview && Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = !preview && Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
            var encoder = webm || avi ? VideoEncoder.Cpu : await EncoderAsync(token);
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[outv]", "-map", "[outa]", "-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : encoder.Name]); if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2"]); else if (!avi) args.AddRange(encoder.SpeedArgs(preview ? EncodeSpeed.Fast : EncodeSpeed.Balanced)); args.AddRange(["-pix_fmt", "yuv420p"]);
            var finalDuration = duration.TotalSeconds / videoSpeed;
            var targetMb = options.Mode switch { ExportMode.Discord20Mb => 19.5, ExportMode.Discord10Mb => 9.7, ExportMode.CustomSize => options.CustomSizeMb * .97, _ => 0 };
            if (preview) args.AddRange([.. encoder.QualityArgs(27), "-c:a", "aac", "-b:a", "96k"]);
            else if (targetMb > 0)
            {
                var videoRate = Math.Max(150, (int)(targetMb * 8192 / Math.Max(.2, finalDuration) - 128));
                args.AddRange([.. encoder.BitrateArgs(videoRate), "-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", "128k"]);
            }
            else { if (avi) args.AddRange(["-q:v", Math.Clamp(options.VideoQualityCrf / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else { args.AddRange(encoder.QualityArgs(options.VideoQualityCrf)); if (webm) args.AddRange(["-b:v", "0"]); } args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{Math.Clamp(options.AudioBitrateKbps, 64, 512)}k"]); }
            args.AddRange(["-t", S(finalDuration)]); if (!webm && !avi) args.AddRange(["-movflags", "+faststart"]); args.Add(output);
            await RunFfmpegAsync(args, preview ? "Layer preview" : "Layered export", preview ? "Building layered preview" : "Compositing timeline layers", TimeSpan.FromSeconds(finalDuration), .3, 1, progress, token);
            progress?.Report(new(1, preview ? "Layer preview ready" : "Layered export complete"));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static readonly string[] AnimatedImageExtensions = [".gif", ".webp", ".apng"];
    public static bool IsAnimatedImage(string path) => AnimatedImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for a printf-style pattern such as frame_%05d.png. Rasterized animations
    /// (Lottie) reach the compositor as a numbered PNG sequence rather than one file.
    /// </summary>
    public static bool IsImageSequence(string path) => path.Contains("%0", StringComparison.Ordinal);

    public static bool GraphicExists(string path)
        => IsImageSequence(path) ? Directory.Exists(Path.GetDirectoryName(path)) : File.Exists(path);

    /// <summary>The layer's own level, clamped to what the volume filter accepts.</summary>
    private static double LayerVolume(TimelineLayer layer) => Math.Clamp(layer.Volume, 0, 2);

    private static bool LayerAudioFullyMuted(TimelineLayer layer) => layer.IsMuted || (layer.Kind == TimelineLayerKind.Audio && layer.MuteLeftChannel && layer.MuteRightChannel);
    private static string AudioChannelFilter(TimelineLayer layer)
    {
        if (layer.Kind != TimelineLayerKind.Audio) return "";
        if (layer.MuteLeftChannel && !layer.MuteRightChannel) return ",aformat=channel_layouts=stereo,pan=stereo|FL=0*FL|FR=FR";
        if (layer.MuteRightChannel && !layer.MuteLeftChannel) return ",aformat=channel_layouts=stereo,pan=stereo|FL=FL|FR=0*FR";
        return "";
    }

    /// <summary>
    /// True when a source can go straight into the composite. The preparation pass exists
    /// to apply the clip's cuts and to normalize odd codecs into something that survives
    /// being muxed and overlaid; a whole, uncut H.264/AAC MP4 needs neither.
    /// </summary>
    private static bool NeedsNoPreparation(ClipItem clip)
    {
        if (clip.Segments.Count != 1) return false;
        var segment = clip.Segments[0];
        if (segment.SourceStart > TimeSpan.Zero || segment.SourceEnd < clip.Media.Duration - TimeSpan.FromMilliseconds(50)) return false;
        var extension = Path.GetExtension(clip.SourcePath).ToLowerInvariant();
        if (clip.Media.AudioCodec is not (null or "aac")) return false;
        if (clip.Media.HasVideo) return extension == ".mp4" && clip.Media.VideoCodec == "h264";
        return clip.Media.IsAudioOnly && extension is ".m4a" or ".mp4" or ".aac";
    }

    private async Task RenderCompositionSourceAsync(ClipItem clip, string output, CancellationToken token, string stage = "Preparing source", double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        if (clip.Segments.Count == 0) throw new InvalidOperationException("No clip pieces are available for the layered composition.");
        var filters = new List<string>();
        if (!clip.Media.HasVideo)
        {
            for (var i = 0; i < clip.Segments.Count; i++)
            {
                var segment = clip.Segments[i]; filters.Add($"[0:a]atrim=start={Ts(segment.SourceStart)}:end={Ts(segment.SourceEnd)},asetpts=PTS-STARTPTS[a{i}]");
            }
            var audioInputs = string.Concat(Enumerable.Range(0, clip.Segments.Count).Select(i => $"[a{i}]"));
            filters.Add($"{audioInputs}concat=n={clip.Segments.Count}:v=0:a=1[outa]"); filters.Add($"color=c=black:s=1280x720:r=30:d={Ts(clip.SelectedDuration)}[outv]");
            var audioArgs = new List<string> { "-y", "-i", clip.SourcePath, "-filter_complex", string.Join(';', filters), "-map", "[outv]", "-map", "[outa]", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart", output };
            await RunFfmpegAsync(audioArgs, "Audio layer source preparation", stage, clip.SelectedDuration, from, to, progress, token); return;
        }
        for (var i = 0; i < clip.Segments.Count; i++)
        {
            var segment = clip.Segments[i];
            filters.Add($"[0:v]trim=start={Ts(segment.SourceStart)}:end={Ts(segment.SourceEnd)},setpts=PTS-STARTPTS[v{i}]");
            if (clip.Media.HasAudio) filters.Add($"[0:a]atrim=start={Ts(segment.SourceStart)}:end={Ts(segment.SourceEnd)},asetpts=PTS-STARTPTS[a{i}]");
        }

        var concatInputs = string.Concat(Enumerable.Range(0, clip.Segments.Count).Select(i => clip.Media.HasAudio ? $"[v{i}][a{i}]" : $"[v{i}]"));
        filters.Add($"{concatInputs}concat=n={clip.Segments.Count}:v=1:a={(clip.Media.HasAudio ? 1 : 0)}[outv]{(clip.Media.HasAudio ? "[outa]" : "")}");
        var args = new List<string> { "-y", "-i", clip.SourcePath, "-filter_complex", string.Join(';', filters), "-map", "[outv]" };
        if (clip.Media.HasAudio) args.AddRange(["-map", "[outa]"]);
        var prepEncoder = await EncoderAsync(token);
        args.AddRange(["-c:v", prepEncoder.Name, .. prepEncoder.SpeedArgs(EncodeSpeed.Fast), .. prepEncoder.QualityArgs(18), "-pix_fmt", "yuv420p"]);
        if (clip.Media.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        await RunFfmpegAsync(args, "Layer source preparation", stage, clip.SelectedDuration, from, to, progress, token);
    }

    private static (int Width, int Height) CompositionSize(MediaInfo media, ExportOptions options, bool preview)
    {
        int width, height;
        switch (options.Aspect)
        {
            case AspectPreset.Vertical9x16: width = preview ? 540 : 1080; height = preview ? 960 : 1920; break;
            case AspectPreset.Square1x1: width = preview ? 720 : 1080; height = width; break;
            case AspectPreset.Portrait4x5: width = preview ? 576 : 1080; height = preview ? 720 : 1350; break;
            case AspectPreset.Landscape16x9: width = preview ? 1280 : 1920; height = preview ? 720 : 1080; break;
            case AspectPreset.Standard4x3: width = preview ? 960 : 1440; height = preview ? 720 : 1080; break;
            case AspectPreset.Cinema21x9: width = preview ? 1260 : 2520; height = preview ? 540 : 1080; break;
            default:
                if (!media.HasVideo) return (preview ? 1280 : 1920, preview ? 720 : 1080);
                var scale = preview && (media.Width > 1280 || media.Height > 720) ? Math.Min(1280d / media.Width, 720d / media.Height) : 1;
                width = Math.Max(2, (int)(media.Width * scale) / 2 * 2); height = Math.Max(2, (int)(media.Height * scale) / 2 * 2); break;
        }
        return (width, height);
    }

    private Task StreamCopyAsync(string input, TimeSpan start, TimeSpan end, string output, CancellationToken token, string stage = "Copying selection", double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var args = new List<string> { "-y", "-ss", Ts(start), "-to", Ts(end), "-i", input, "-c", "copy", "-map", "0", "-map_metadata", "0", "-movflags", "+faststart", output };
        return RunFfmpegAsync(args, "Lossless trim", stage, end - start, from, to, progress, token);
    }

    private async Task ExportWithSegmentsAsync(ClipItem clip, string output, bool lossless, CancellationToken token, double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"post-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            var parts = new List<string>(); var index = 0;
            var copyShare = (to - from) * (lossless ? .75 : .5);
            foreach (var segment in clip.Segments)
            {
                var part = Path.Combine(temp, $"part-{index:000}.mkv");
                var low = from + copyShare * index / clip.Segments.Count; var high = from + copyShare * (index + 1) / clip.Segments.Count; index++;
                await StreamCopyAsync(clip.SourcePath, segment.SourceStart, segment.SourceEnd, part, token, $"Copying piece {index} of {clip.Segments.Count}", low, high, progress);
                parts.Add(part);
            }
            if (parts.Count == 0) throw new InvalidOperationException("No segments to export.");
            await ConcatFilesAsync(parts, output, lossless, null, token, clip.SelectedDuration, from + copyShare, to, progress);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    public async Task StitchAsync(IReadOnlyList<ClipItem> clips, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        if (clips.Count == 0) throw new InvalidOperationException("No clips are loaded.");
        var temp = Path.Combine(Path.GetTempPath(), $"post-video-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < clips.Count; i++)
            {
                var part = Path.Combine(temp, $"clip-{i:000}.mkv"); var index = i;
                var relay = progress is null ? null : new Progress<ExportProgress>(value => progress.Report(new(.5 * (index + value.Fraction) / clips.Count, $"Preparing clip {index + 1} of {clips.Count}")));
                await ExportAsync(clips[i], part, options with { Mode = ExportMode.Lossless }, relay, token); parts.Add(part);
            }
            var total = TimeSpan.FromTicks(clips.Sum(clip => clip.SelectedDuration.Ticks));
            var webm = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
            try { await ConcatFilesAsync(parts, output, !webm && !avi, options, token, total, .5, 1, progress); }
            catch { await ConcatFilesAsync(parts, output, false, options, token, total, .5, 1, progress); }
            progress?.Report(new(1, "Montage complete"));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private async Task ConcatFilesAsync(IEnumerable<string> inputs, string output, bool lossless, ExportOptions? options, CancellationToken token, TimeSpan total = default, double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var list = Path.Combine(Path.GetTempPath(), $"post-list-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, inputs.Select(p => $"file '{p.Replace("'", "'\\''")}'"), token);
        try
        {
            var args = new List<string> { "-y", "-f", "concat", "-safe", "0", "-i", list };
            if (lossless) args.AddRange(["-c", "copy"]);
            else
            {
                var webm = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
                var quality = Math.Clamp(options?.VideoQualityCrf ?? 18, 10, 40); var bitrate = Math.Clamp(options?.AudioBitrateKbps ?? 192, 64, 512);
                var joinEncoder = webm || avi ? VideoEncoder.Cpu : await EncoderAsync(token);
                args.AddRange(["-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : joinEncoder.Name]);
                if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2", "-b:v", "0", "-crf", quality.ToString(CultureInfo.InvariantCulture)]); else if (avi) args.AddRange(["-q:v", Math.Clamp(quality / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else args.AddRange([.. joinEncoder.SpeedArgs(EncodeSpeed.Balanced), .. joinEncoder.QualityArgs(quality)]);
                args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{bitrate}k"]);
            }
            args.Add(output);
            await RunFfmpegAsync(args, "Montage stitching", lossless ? "Joining pieces" : "Encoding joined video", total, from, to, progress, token);
        }
        finally { if (File.Exists(list)) File.Delete(list); }
    }

    private async Task EncodeAsync(string input, TimeSpan start, TimeSpan end, MediaInfo media, string output, ExportOptions options, CancellationToken token, double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var duration = (end - start).TotalSeconds / options.Speed; var args = new List<string> { "-y", "-ss", Ts(start), "-to", Ts(end), "-i", input };
        var vf = BuildVideoFilter(media, options); if (options.Speed != 1) vf.Add($"setpts=PTS/{S(options.Speed)}"); if (vf.Count > 0) args.AddRange(["-vf", string.Join(',', vf)]);
        var af = new List<string>(); if (options.Speed != 1) af.AddRange(Atempo(options.Speed)); if (options.Volume != 1) af.Add($"volume={S(options.Volume)}");
        if (options.Equalizer is { } equalizer) af.AddRange(equalizer.BuildFilters());
        if (af.Count > 0 && media.HasAudio) args.AddRange(["-af", string.Join(',', af)]);
        var webm = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
        var encoder = webm || avi ? VideoEncoder.Cpu : await EncoderAsync(token);
        args.AddRange(["-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : encoder.Name]); if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2"]); else if (!avi) args.AddRange(encoder.SpeedArgs(EncodeSpeed.Balanced)); args.AddRange(["-pix_fmt", "yuv420p"]);
        var targetMb = options.Mode switch { ExportMode.Discord20Mb => 19.5, ExportMode.Discord10Mb => 9.7, ExportMode.CustomSize => options.CustomSizeMb * .97, _ => 0 };
        if (targetMb > 0)
        {
            var audioRate = media.HasAudio ? 128 : 0; var videoRate = Math.Max(150, (int)(targetMb * 8192 / Math.Max(0.2, duration) - audioRate));
            args.AddRange(encoder.BitrateArgs(videoRate));
        }
        else { if (avi) args.AddRange(["-q:v", Math.Clamp(options.VideoQualityCrf / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else { args.AddRange(encoder.QualityArgs(options.VideoQualityCrf)); if (webm) args.AddRange(["-b:v", "0"]); } }
        if (media.HasAudio) args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{Math.Clamp(options.AudioBitrateKbps, 64, 512)}k"]); if (!webm && !avi) args.AddRange(["-movflags", "+faststart"]); args.Add(output);
        await RunFfmpegAsync(args, "Video export", "Encoding video", TimeSpan.FromSeconds(duration), from, to, progress, token);
    }

    private static List<string> Atempo(double speed)
    {
        var filters = new List<string>(); var remaining = speed;
        while (remaining > 2) { filters.Add("atempo=2"); remaining /= 2; }
        while (remaining < .5) { filters.Add("atempo=0.5"); remaining /= .5; }
        filters.Add($"atempo={S(remaining)}"); return filters;
    }

    private static List<string> BuildVideoFilter(MediaInfo media, ExportOptions options)
    {
        var effects = VideoEffects.Build(options.Effects);
        if (options.Aspect == AspectPreset.Original && options.CropZoom == 1) return effects;
        var ratio = options.Aspect switch { AspectPreset.Vertical9x16 => 9d / 16, AspectPreset.Square1x1 => 1, AspectPreset.Portrait4x5 => 4d / 5, AspectPreset.Standard4x3 => 4d / 3, AspectPreset.Cinema21x9 => 21d / 9, _ => 16d / 9 };
        var inputRatio = media.Width / (double)media.Height; int baseW, baseH;
        if (inputRatio > ratio) { baseH = media.Height; baseW = (int)(baseH * ratio); } else { baseW = media.Width; baseH = (int)(baseW / ratio); }
        var zoom = Math.Clamp(options.CropZoom, 1, 4); var w = Math.Max(2, (int)(baseW / zoom) / 2 * 2); var h = Math.Max(2, (int)(baseH / zoom) / 2 * 2);
        var x = (int)((media.Width - w) * Math.Clamp((options.PanX + 1) / 2, 0, 1)); var y = (int)((media.Height - h) * Math.Clamp((options.PanY + 1) / 2, 0, 1));
        var target = options.Aspect switch { AspectPreset.Vertical9x16 => "1080:1920", AspectPreset.Square1x1 => "1080:1080", AspectPreset.Portrait4x5 => "1080:1350", AspectPreset.Standard4x3 => "1440:1080", AspectPreset.Cinema21x9 => "2520:1080", _ => "1920:1080" };
        return [$"crop={w}:{h}:{x}:{y}", $"scale={target}:flags=lanczos", .. effects];
    }

    private async Task ExportGifAsync(ClipItem clip, string output, ExportOptions options, CancellationToken token, IProgress<ExportProgress>? progress = null)
    {
        if (clip.Segments.Count == 0) throw new InvalidOperationException("No timeline pieces to export.");
        if (clip.Segments.Count == 1)
        {
            await ExportGifFileAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, clip.Media, output, options, token, .04, 1, progress);
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"post-gif-{Guid.NewGuid():N}.mkv");
        try
        {
            await ExportWithSegmentsAsync(clip, temp, true, token, .02, .3, progress);
            await ExportGifFileAsync(temp, TimeSpan.Zero, clip.SelectedDuration, clip.Media, output, options, token, .3, 1, progress);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private Task ExportGifFileAsync(string input, TimeSpan start, TimeSpan end, MediaInfo media, string output, ExportOptions options, CancellationToken token, double from = 0, double to = 1, IProgress<ExportProgress>? progress = null)
    {
        var vf = BuildVideoFilter(media, options); vf.Add("fps=20"); vf.Add("scale='min(960,iw)':-2:flags=lanczos"); var chain = string.Join(',', vf);
        var filter = $"[0:v]{chain},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a";
        var args = new List<string> { "-y", "-ss", Ts(start), "-to", Ts(end), "-i", input, "-filter_complex", filter, "-loop", "0", output };
        return RunFfmpegAsync(args, "GIF export", "Rendering GIF", end - start, from, to, progress, token);
    }
}
