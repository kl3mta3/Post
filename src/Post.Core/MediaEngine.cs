using System.Globalization;
using System.Collections.Concurrent;

namespace Post.Core;

public sealed class MediaEngine(FfmpegTools tools, IProcessRunner runner)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ThumbnailLocks = new(StringComparer.OrdinalIgnoreCase);
    private static string S(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Ts(TimeSpan value) => S(value.TotalSeconds);

    public async Task<string> CreatePreviewProxyAsync(MediaInfo media, string cacheDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(cacheDirectory);
        if (media.IsStillImage) return media.Path;
        if (Path.GetExtension(media.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) && media.VideoCodec is "h264" or "hevc") return media.Path;
        var extension = Path.GetExtension(media.Path).ToLowerInvariant();
        if (!media.HasVideo && extension is ".mp3" or ".wav" or ".wma") return media.Path;
        var output = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(media.Path)}-{Math.Abs(media.Path.GetHashCode())}.preview{(media.HasVideo ? ".mp4" : ".m4a")}");
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(media.Path)) return output;
        var args = media.HasVideo
            ? new List<string> { "-y", "-i", media.Path, "-map", "0:v:0", "-map", "0:a?", "-vf", "scale='min(1920,iw)':-2", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output }
            : new List<string> { "-y", "-i", media.Path, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output };
        var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess("Preview preparation"); return output;
    }

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

    public async Task CaptureFrameAsync(string input, TimeSpan position, string output, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await runner.RunAsync(tools.Ffmpeg, ["-y", "-ss", Ts(position), "-i", input, "-frames:v", "1", "-q:v", "1", output], token);
        result.EnsureSuccess("Frame capture");
    }

    public async Task ExportAsync(ClipItem clip, string output, ExportOptions options, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        progress?.Report(new(0.03, "Preparing export"));
        var extension = Path.GetExtension(output);
        var lossless = !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".avi", StringComparison.OrdinalIgnoreCase) && options.Mode == ExportMode.Lossless && options.Speed == 1 && options.Volume == 1 && options.Aspect == AspectPreset.Original && options.CropZoom == 1;
        if (lossless)
        {
            if (clip.Segments.Count == 1) await StreamCopyAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, output, token);
            else await ExportWithSegmentsAsync(clip, output, true, token);
        }
        else if (options.Mode == ExportMode.Gif) await ExportGifAsync(clip, output, options, token);
        else if (clip.Segments.Count == 1)
        {
            await EncodeAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, clip.Media, output, options, token);
        }
        else
        {
            var temp = Path.Combine(Path.GetTempPath(), $"post-pre-{Guid.NewGuid():N}.mkv");
            try { await ExportWithSegmentsAsync(clip, temp, true, token); await EncodeAsync(temp, TimeSpan.Zero, clip.SelectedDuration, clip.Media, output, options, token); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
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
                await RenderCompositionAudioSourceAsync(clip, part, token); rendered[clip] = part;
                progress?.Report(new(.1 + .3 * rendered.Count / placements.Select(item => item.Placement.Clip).Distinct().Count(), $"Preparing audio {rendered.Count}"));
            }
            var args = new List<string> { "-y" }; foreach (var item in placements) args.AddRange(["-i", rendered[item.Placement.Clip]]);
            var filters = new List<string>(); var inputs = new List<string>();
            for (var i = 0; i < placements.Length; i++)
            {
                var item = placements[i]; var placement = item.Placement; var name = $"a{i}"; var delay = Math.Max(0, (long)placement.Start.TotalMilliseconds);
                var volume = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Volume, 1);
                filters.Add($"[{i}:a]atrim=start={Ts(placement.InPoint)}:end={Ts(placement.InPoint + placement.Duration)},asetpts=PTS-STARTPTS,volume='{volume}':eval=frame,adelay={delay}:all=1{AudioChannelFilter(item.Layer)}[{name}]"); inputs.Add($"[{name}]");
            }
            var mixed = "mixed"; if (inputs.Count == 1) filters.Add($"{inputs[0]}anull[{mixed}]"); else filters.Add($"{string.Concat(inputs)}amix=inputs={inputs.Count}:duration=longest:dropout_transition=0[{mixed}]");
            var finalFilters = new List<string>(); if (options.Speed != 1) finalFilters.AddRange(Atempo(options.Speed)); if (options.Volume != 1) finalFilters.Add($"volume={S(options.Volume)}");
            if (finalFilters.Count > 0) filters.Add($"[{mixed}]{string.Join(',', finalFilters)}[outa]"); else filters.Add($"[{mixed}]anull[outa]");
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[outa]"]); AddAudioCodec(args, output, options.AudioBitrateKbps); args.Add(output);
            progress?.Report(new(.5, "Mixing audio layers")); var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess("Audio export"); progress?.Report(new(1, "Audio export complete"));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private async Task RenderCompositionAudioSourceAsync(ClipItem clip, string output, CancellationToken token)
    {
        var filters = new List<string>();
        for (var i = 0; i < clip.Segments.Count; i++) { var segment = clip.Segments[i]; filters.Add($"[0:a]atrim=start={Ts(segment.SourceStart)}:end={Ts(segment.SourceEnd)},asetpts=PTS-STARTPTS[a{i}]"); }
        var inputs = string.Concat(Enumerable.Range(0, clip.Segments.Count).Select(i => $"[a{i}]")); filters.Add($"{inputs}concat=n={clip.Segments.Count}:v=0:a=1[outa]");
        var result = await runner.RunAsync(tools.Ffmpeg, ["-y", "-i", clip.SourcePath, "-filter_complex", string.Join(';', filters), "-map", "[outa]", "-c:a", "pcm_s16le", output], token); result.EnsureSuccess("Audio source preparation");
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
        var graphics = layers.Reverse().SelectMany(layer => layer.Graphics).Where(graphic => !string.IsNullOrWhiteSpace(graphic.RenderedImagePath) && File.Exists(graphic.RenderedImagePath)).ToArray();
        if (placements.Length == 0 && graphics.Length == 0) throw new InvalidOperationException("No visible timeline layers contain media.");
        var duration = composition.OutputDuration;
        var sizingMedia = visualPlacements.Length > 0 ? visualPlacements[0].Item.Placement.Clip.Media : placements.Length > 0 && placements[0].Placement.Clip.Media.HasVideo ? placements[0].Placement.Clip.Media : new MediaInfo("", duration, 1920, 1080, 30, "generated", null, 0);
        var (width, height) = CompositionSize(sizingMedia, options, preview);
        var temp = Path.Combine(Path.GetTempPath(), $"post-layers-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            progress?.Report(new(.04, "Preparing timeline layers"));
            var renderedClips = new Dictionary<ClipItem, string>();
            foreach (var clip in placements.Select(item => item.Placement.Clip).Distinct())
            {
                token.ThrowIfCancellationRequested();
                // Composition inputs must share a broadly supported intermediate format.
                // Stream-copying raw BGR AVI (and several other codecs) into Matroska is
                // invalid, so normalize every source to H.264/AAC before overlaying it.
                var part = Path.Combine(temp, $"source-{renderedClips.Count:000}.mp4");
                await RenderCompositionSourceAsync(clip, part, token);
                renderedClips[clip] = part;
                progress?.Report(new(.08 + .32 * renderedClips.Count / placements.Select(item => item.Placement.Clip).Distinct().Count(), $"Preparing {clip.DisplayName}"));
            }

            var args = new List<string> { "-y" };
            foreach (var item in placements) args.AddRange(["-i", renderedClips[item.Placement.Clip]]);
            foreach (var graphic in graphics) args.AddRange(["-loop", "1", "-i", graphic.RenderedImagePath!]);
            var filters = new List<string> { $"color=c=black:s={width}x{height}:r=30:d={Ts(duration)}[base]" };
            foreach (var visual in visualPlacements)
            {
                var placement = visual.Item.Placement; var input = visual.InputIndex;
                var source = $"v{input}";
                filters.Add($"[{input}:v]trim=start={Ts(placement.InPoint)}:end={Ts(placement.InPoint + placement.Duration)},setpts=PTS-STARTPTS+{Ts(placement.Start)}/TB,scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[vp{input}]");
                if (placement.Keyframes.Any(item => item.Property is KeyframeProperty.PositionX or KeyframeProperty.PositionY or KeyframeProperty.Scale or KeyframeProperty.Opacity))
                {
                    var scale = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Scale, 1, "on/30");
                    var x = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.PositionX, .5, "on/30");
                    var y = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.PositionY, .5, "on/30");
                    var opacity = KeyframeEvaluator.BuildFfmpegExpression(placement.Keyframes, KeyframeProperty.Opacity, 1, "N/30");
                    var left = $"(W-W*({scale}))/2+(({x})-0.5)*W"; var top = $"(H-H*({scale}))/2+(({y})-0.5)*H";
                    filters.Add($"[vp{input}]fps=30,format=rgba,perspective=x0='{left}':y0='{top}':x1='({left})+W*({scale})':y1='{top}':x2='{left}':y2='({top})+H*({scale})':x3='({left})+W*({scale})':y3='({top})+H*({scale})':sense=source:eval=frame,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({opacity})'[{source}]");
                }
                else filters.Add($"[vp{input}]null[{source}]");
            }
            var video = "base";
            for (var i = 0; i < visualPlacements.Length; i++)
            {
                var visual = visualPlacements[i]; var input = visual.InputIndex; var next = $"overlay{i}";
                filters.Add($"[{video}][v{input}]overlay=0:0:eof_action=pass:shortest=0[{next}]"); video = next;
            }
            for (var i = 0; i < graphics.Length; i++)
            {
                var graphic = graphics[i]; var input = placements.Length + i;
                var graphicWidth = Math.Clamp((int)Math.Round(width * graphic.Width), 2, width); var graphicHeight = Math.Clamp((int)Math.Round(height * graphic.Height), 2, height);
                var fit = graphic.PreserveAspectRatio ? $"scale={graphicWidth}:{graphicHeight}:force_original_aspect_ratio=decrease,pad={graphicWidth}:{graphicHeight}:(ow-iw)/2:(oh-ih)/2:color=0x00000000" : $"scale={graphicWidth}:{graphicHeight}";
                var baseName = $"gb{i}";
                filters.Add($"[{input}:v]fps=30,{fit},format=rgba,pad={width}:{height}:0:0:color=0x00000000[{baseName}]");
                var scale = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.Scale, 1, "on/30", graphic.Start.TotalSeconds);
                var positionX = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.PositionX, graphic.X, "on/30", graphic.Start.TotalSeconds);
                var positionY = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.PositionY, graphic.Y, "on/30", graphic.Start.TotalSeconds);
                var opacity = KeyframeEvaluator.BuildFfmpegExpression(graphic.Keyframes, KeyframeProperty.Opacity, Math.Clamp(graphic.Opacity, 0, 1), "N/30", graphic.Start.TotalSeconds);
                var left = $"W*({positionX})"; var top = $"H*({positionY})";
                filters.Add($"[{baseName}]perspective=x0='{left}':y0='{top}':x1='({left})+W*({scale})':y1='{top}':x2='{left}':y2='({top})+H*({scale})':x3='({left})+W*({scale})':y3='({top})+H*({scale})':sense=source:eval=frame,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*({opacity})'[g{i}]");
                var next = $"graphic{i}"; filters.Add($"[{video}][g{i}]overlay=0:0:enable='between(t,{Ts(graphic.Start)},{Ts(graphic.End)})':eof_action=pass:shortest=0[{next}]"); video = next;
            }
            var videoSpeed = Math.Max(.01, options.Speed);
            filters.Add($"[{video}]trim=duration={Ts(duration)},setpts=(PTS-STARTPTS)/{S(videoSpeed)}[outv]");

            var audioInputs = new List<string>();
            for (var i = 0; i < placements.Length; i++)
            {
                var item = placements[i];
                if (LayerAudioFullyMuted(item.Layer) || !item.Placement.Clip.Media.HasAudio) continue;
                var name = $"a{i}"; var delay = Math.Max(0, (long)item.Placement.Start.TotalMilliseconds);
                var volume = KeyframeEvaluator.BuildFfmpegExpression(item.Placement.Keyframes, KeyframeProperty.Volume, 1);
                filters.Add($"[{i}:a]atrim=start={Ts(item.Placement.InPoint)}:end={Ts(item.Placement.InPoint + item.Placement.Duration)},asetpts=PTS-STARTPTS,volume='{volume}':eval=frame,adelay={delay}:all=1{AudioChannelFilter(item.Layer)}[{name}]"); audioInputs.Add($"[{name}]");
            }
            var audio = "mixed";
            if (audioInputs.Count == 0) filters.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={Ts(duration)}[{audio}]");
            else if (audioInputs.Count == 1) filters.Add($"{audioInputs[0]}apad=pad_dur={Ts(duration)},atrim=duration={Ts(duration)}[{audio}]");
            else filters.Add($"{string.Concat(audioInputs)}amix=inputs={audioInputs.Count}:duration=longest:dropout_transition=0,apad=pad_dur={Ts(duration)},atrim=duration={Ts(duration)}[{audio}]");
            var audioFilters = new List<string>(); if (options.Speed != 1) audioFilters.AddRange(Atempo(options.Speed)); if (options.Volume != 1) audioFilters.Add($"volume={S(options.Volume)}");
            if (audioFilters.Count > 0) filters.Add($"[{audio}]{string.Join(',', audioFilters)}[outa]"); else filters.Add($"[{audio}]anull[outa]");

            var webm = !preview && Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = !preview && Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[outv]", "-map", "[outa]", "-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : "libx264"]); if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2"]); else if (!avi) args.AddRange(["-preset", preview ? "ultrafast" : "medium"]); args.AddRange(["-pix_fmt", "yuv420p"]);
            var finalDuration = duration.TotalSeconds / videoSpeed;
            var targetMb = options.Mode switch { ExportMode.Discord20Mb => 19.5, ExportMode.Discord10Mb => 9.7, ExportMode.CustomSize => options.CustomSizeMb * .97, _ => 0 };
            if (preview) args.AddRange(["-crf", "27", "-c:a", "aac", "-b:a", "96k"]);
            else if (targetMb > 0)
            {
                var videoRate = Math.Max(150, (int)(targetMb * 8192 / Math.Max(.2, finalDuration) - 128));
                args.AddRange(["-b:v", $"{videoRate}k", "-maxrate", $"{videoRate}k", "-bufsize", $"{videoRate * 2}k", "-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", "128k"]);
            }
            else { if (avi) args.AddRange(["-q:v", Math.Clamp(options.VideoQualityCrf / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else { args.AddRange(["-crf", Math.Clamp(options.VideoQualityCrf, 10, 40).ToString(CultureInfo.InvariantCulture)]); if (webm) args.AddRange(["-b:v", "0"]); } args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{Math.Clamp(options.AudioBitrateKbps, 64, 512)}k"]); }
            args.AddRange(["-t", S(finalDuration)]); if (!webm && !avi) args.AddRange(["-movflags", "+faststart"]); args.Add(output);
            progress?.Report(new(.45, preview ? "Building layered preview" : "Compositing timeline layers"));
            var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess(preview ? "Layer preview" : "Layered export");
            progress?.Report(new(1, preview ? "Layer preview ready" : "Layered export complete"));
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static bool LayerAudioFullyMuted(TimelineLayer layer) => layer.IsMuted || (layer.Kind == TimelineLayerKind.Audio && layer.MuteLeftChannel && layer.MuteRightChannel);
    private static string AudioChannelFilter(TimelineLayer layer)
    {
        if (layer.Kind != TimelineLayerKind.Audio) return "";
        if (layer.MuteLeftChannel && !layer.MuteRightChannel) return ",aformat=channel_layouts=stereo,pan=stereo|FL=0*FL|FR=FR";
        if (layer.MuteRightChannel && !layer.MuteLeftChannel) return ",aformat=channel_layouts=stereo,pan=stereo|FL=FL|FR=0*FR";
        return "";
    }

    private async Task RenderCompositionSourceAsync(ClipItem clip, string output, CancellationToken token)
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
            var audioResult = await runner.RunAsync(tools.Ffmpeg, audioArgs, token); audioResult.EnsureSuccess("Audio layer source preparation"); return;
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
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p"]);
        if (clip.Media.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+faststart", output]);
        var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess("Layer source preparation");
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

    private async Task StreamCopyAsync(string input, TimeSpan start, TimeSpan end, string output, CancellationToken token)
    {
        var args = new[] { "-y", "-ss", Ts(start), "-to", Ts(end), "-i", input, "-c", "copy", "-map", "0", "-map_metadata", "0", "-movflags", "+faststart", output };
        var result = await runner.RunAsync(tools.Ffmpeg, args, token);
        result.EnsureSuccess("Lossless trim");
    }

    private async Task ExportWithSegmentsAsync(ClipItem clip, string output, bool lossless, CancellationToken token)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"post-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            var parts = new List<string>(); var index = 0;
            foreach (var segment in clip.Segments)
            {
                var part = Path.Combine(temp, $"part-{index++:000}.mkv"); await StreamCopyAsync(clip.SourcePath, segment.SourceStart, segment.SourceEnd, part, token); parts.Add(part);
            }
            if (parts.Count == 0) throw new InvalidOperationException("No segments to export.");
            await ConcatFilesAsync(parts, output, lossless, null, token);
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    public async Task StitchAsync(IReadOnlyList<ClipItem> clips, string output, ExportOptions options, CancellationToken token = default)
    {
        if (clips.Count == 0) throw new InvalidOperationException("No clips are loaded.");
        var temp = Path.Combine(Path.GetTempPath(), $"post-video-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        try
        {
            var parts = new List<string>();
            for (var i = 0; i < clips.Count; i++)
            {
                var part = Path.Combine(temp, $"clip-{i:000}.mkv");
                await ExportAsync(clips[i], part, options with { Mode = ExportMode.Lossless }, null, token); parts.Add(part);
            }
            var webm = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
            try { await ConcatFilesAsync(parts, output, !webm && !avi, options, token); }
            catch { await ConcatFilesAsync(parts, output, false, options, token); }
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private async Task ConcatFilesAsync(IEnumerable<string> inputs, string output, bool lossless, ExportOptions? options, CancellationToken token)
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
                args.AddRange(["-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : "libx264"]);
                if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2", "-b:v", "0", "-crf", quality.ToString(CultureInfo.InvariantCulture)]); else if (avi) args.AddRange(["-q:v", Math.Clamp(quality / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else args.AddRange(["-preset", "medium", "-crf", quality.ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{bitrate}k"]);
            }
            args.Add(output); var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess("Montage stitching");
        }
        finally { if (File.Exists(list)) File.Delete(list); }
    }

    private async Task EncodeAsync(string input, TimeSpan start, TimeSpan end, MediaInfo media, string output, ExportOptions options, CancellationToken token)
    {
        var duration = (end - start).TotalSeconds / options.Speed; var args = new List<string> { "-y", "-ss", Ts(start), "-to", Ts(end), "-i", input };
        var vf = BuildVideoFilter(media, options); if (options.Speed != 1) vf.Add($"setpts=PTS/{S(options.Speed)}"); if (vf.Count > 0) args.AddRange(["-vf", string.Join(',', vf)]);
        var af = new List<string>(); if (options.Speed != 1) af.AddRange(Atempo(options.Speed)); if (options.Volume != 1) af.Add($"volume={S(options.Volume)}"); if (af.Count > 0 && media.HasAudio) args.AddRange(["-af", string.Join(',', af)]);
        var webm = Path.GetExtension(output).Equals(".webm", StringComparison.OrdinalIgnoreCase); var avi = Path.GetExtension(output).Equals(".avi", StringComparison.OrdinalIgnoreCase);
        args.AddRange(["-c:v", webm ? "libvpx-vp9" : avi ? "mpeg4" : "libx264"]); if (webm) args.AddRange(["-deadline", "good", "-cpu-used", "2"]); else if (!avi) args.AddRange(["-preset", "medium"]); args.AddRange(["-pix_fmt", "yuv420p"]);
        var targetMb = options.Mode switch { ExportMode.Discord20Mb => 19.5, ExportMode.Discord10Mb => 9.7, ExportMode.CustomSize => options.CustomSizeMb * .97, _ => 0 };
        if (targetMb > 0)
        {
            var audioRate = media.HasAudio ? 128 : 0; var videoRate = Math.Max(150, (int)(targetMb * 8192 / Math.Max(0.2, duration) - audioRate));
            args.AddRange(["-b:v", $"{videoRate}k", "-maxrate", $"{videoRate}k", "-bufsize", $"{videoRate * 2}k"]);
        }
        else { if (avi) args.AddRange(["-q:v", Math.Clamp(options.VideoQualityCrf / 2, 2, 20).ToString(CultureInfo.InvariantCulture)]); else { args.AddRange(["-crf", Math.Clamp(options.VideoQualityCrf, 10, 40).ToString(CultureInfo.InvariantCulture)]); if (webm) args.AddRange(["-b:v", "0"]); } }
        if (media.HasAudio) args.AddRange(["-c:a", webm ? "libopus" : avi ? "libmp3lame" : "aac", "-b:a", $"{Math.Clamp(options.AudioBitrateKbps, 64, 512)}k"]); if (!webm && !avi) args.AddRange(["-movflags", "+faststart"]); args.Add(output);
        var result = await runner.RunAsync(tools.Ffmpeg, args, token); result.EnsureSuccess("Video export");
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
        if (options.Aspect == AspectPreset.Original && options.CropZoom == 1) return [];
        var ratio = options.Aspect switch { AspectPreset.Vertical9x16 => 9d / 16, AspectPreset.Square1x1 => 1, AspectPreset.Portrait4x5 => 4d / 5, AspectPreset.Standard4x3 => 4d / 3, AspectPreset.Cinema21x9 => 21d / 9, _ => 16d / 9 };
        var inputRatio = media.Width / (double)media.Height; int baseW, baseH;
        if (inputRatio > ratio) { baseH = media.Height; baseW = (int)(baseH * ratio); } else { baseW = media.Width; baseH = (int)(baseW / ratio); }
        var zoom = Math.Clamp(options.CropZoom, 1, 4); var w = Math.Max(2, (int)(baseW / zoom) / 2 * 2); var h = Math.Max(2, (int)(baseH / zoom) / 2 * 2);
        var x = (int)((media.Width - w) * Math.Clamp((options.PanX + 1) / 2, 0, 1)); var y = (int)((media.Height - h) * Math.Clamp((options.PanY + 1) / 2, 0, 1));
        var target = options.Aspect switch { AspectPreset.Vertical9x16 => "1080:1920", AspectPreset.Square1x1 => "1080:1080", AspectPreset.Portrait4x5 => "1080:1350", AspectPreset.Standard4x3 => "1440:1080", AspectPreset.Cinema21x9 => "2520:1080", _ => "1920:1080" };
        return [$"crop={w}:{h}:{x}:{y}", $"scale={target}:flags=lanczos"];
    }

    private async Task ExportGifAsync(ClipItem clip, string output, ExportOptions options, CancellationToken token)
    {
        if (clip.Segments.Count == 0) throw new InvalidOperationException("No timeline pieces to export.");
        if (clip.Segments.Count == 1)
        {
            await ExportGifFileAsync(clip.SourcePath, clip.Segments[0].SourceStart, clip.Segments[0].SourceEnd, clip.Media, output, options, token);
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"post-gif-{Guid.NewGuid():N}.mkv");
        try
        {
            await ExportWithSegmentsAsync(clip, temp, true, token);
            await ExportGifFileAsync(temp, TimeSpan.Zero, clip.SelectedDuration, clip.Media, output, options, token);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private async Task ExportGifFileAsync(string input, TimeSpan start, TimeSpan end, MediaInfo media, string output, ExportOptions options, CancellationToken token)
    {
        var vf = BuildVideoFilter(media, options); vf.Add("fps=20"); vf.Add("scale='min(960,iw)':-2:flags=lanczos"); var chain = string.Join(',', vf);
        var filter = $"[0:v]{chain},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a";
        var result = await runner.RunAsync(tools.Ffmpeg, ["-y", "-ss", Ts(start), "-to", Ts(end), "-i", input, "-filter_complex", filter, "-loop", "0", output], token);
        result.EnsureSuccess("GIF export");
    }
}
