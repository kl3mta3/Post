using Post.Core;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Post.App;

/// <summary>
/// The colour grading and equalizer windows, plus the pending-effect preview that lets
/// sliders be judged on the paused frame before anything is added to a project.
/// </summary>
public partial class MainWindow
{
    private ColorGradingPanel? _gradingPanel;
    private EqualizerPanel? _equalizerPanel;
    private VideoEffect? _pendingPreviewEffect;
    private Guid? _suppressedPreviewEffectId;
    private string? _lastGradePreview;

    /// <summary>
    /// The effects used to render the paused frame: everything applied to the clip and
    /// the timeline, minus the one being edited, plus whatever is being auditioned.
    /// </summary>
    private List<VideoEffect> PreviewEffectsFor(TimelinePlacement placement, TimeSpan? projectTime = null)
    {
        var effects = new List<VideoEffect>();
        effects.AddRange(placement.Effects.Where(effect => effect.Id != _suppressedPreviewEffectId));

        // Whichever LUT the clip's keyframes are holding at this moment, as an effect for
        // the length of this frame. Export switches lut3d on and off by time; here there is
        // only ever one frame on screen, so the one in force is the whole story.
        var offset = (projectTime ?? _editPosition) - placement.Start;
        if (KeyframeEvaluator.EvaluateText(placement.Keyframes, KeyframeProperty.Lut, offset) is { } lut && File.Exists(lut))
            effects.Add(new VideoEffect { Kind = VideoEffectKind.Lut, FilePath = lut });

        effects.AddRange(_composition.OutputEffects.Where(effect => effect.Id != _suppressedPreviewEffectId));
        if (_pendingPreviewEffect is { } pending) effects.Add(pending);
        return effects;
    }

    /// <summary>
    /// The grade being worked on in the Color Grading panel, shown on the moving picture.
    ///
    /// It used to bake a .cube and have ffmpeg render one still frame, which is why that
    /// panel had a preview screen of its own. The shader samples a baked table either way,
    /// so this is the same maths on live video instead of a photograph of it.
    /// </summary>
    private ColorGrade? _previewGrade;

    private void SetPreviewGrade(ColorGrade? grade)
    {
        _previewGrade = grade is { IsNeutral: false } ? grade : null;
        RefreshLivePreviewShaders();
        UpdatePreviewShaders();
        if (!_playing) _ = RenderCurrentScrubFrameAsync();
    }

    private void SetPreviewEffect(VideoEffect? pending, Guid? suppressed)
    {
        _pendingPreviewEffect = pending; _suppressedPreviewEffectId = suppressed;
        RefreshLivePreviewShaders();
        UpdatePreviewShaders();
        if (!_playing) _ = RenderCurrentScrubFrameAsync();
    }

    /// <summary>
    /// Re-shades whichever players are on screen, so an effect being auditioned shows on
    /// the moving picture rather than only on a paused frame.
    /// </summary>
    private void RefreshLivePreviewShaders()
    {
        foreach (var layer in _composition.Layers)
            foreach (var placement in layer.Placements)
                if (_livePlayers.TryGetValue(placement.Id, out var player))
                    ApplyPreviewShader(player, placement.Id, PreviewEffectsFor(placement));
    }

    // ---- live effect preview ------------------------------------------------
    // The paused frame is rendered through the real ffmpeg filters. During playback
    // the players carry a shader that approximates the colour and vignette effects,
    // so an effect is visible on the moving picture and not just on one frame.

    private readonly Dictionary<Guid, (string Signature, System.Windows.Media.Effects.Effect? Effect)> _previewShaders = [];

    private void ApplyPreviewShader(UIElement element, Guid key, IReadOnlyList<VideoEffect> effects)
    {
        var live = effects.Where(effect => effect.IsEnabled).ToArray();
        var signature = string.Join('|', live.Select(effect => $"{effect.Kind}:{effect.Amount}:{effect.Brightness}:{effect.Contrast}:{effect.Saturation}:{effect.Gamma}:{effect.Hue}:{effect.FilePath}"))
            + "|grade:" + (_previewGrade?.ToString() ?? "none");
        if (_previewShaders.TryGetValue(key, out var cached) && cached.Signature == signature)
        {
            if (!ReferenceEquals(element.Effect, cached.Effect)) element.Effect = cached.Effect;
            return;
        }
        var shader = PreviewShaderEffect.For(live, _previewGrade);
        _previewShaders[key] = (signature, shader);
        element.Effect = shader;
    }

    /// <summary>Shades the single-clip player; the layered players are handled per placement.</summary>
    private void UpdatePreviewShaders()
    {
        if (_livePreviewActive || Player is null) return;
        // A rendered composite already has the effects burned in.
        if (_compositionPreviewActive) { Player.Effect = null; return; }
        var effects = new List<VideoEffect>();
        if (_current is { } clip && _composition.Layers.SelectMany(layer => layer.Placements).FirstOrDefault(item => ReferenceEquals(item.Clip, clip)) is { } placement)
            effects.AddRange(PreviewEffectsFor(placement));
        else
        {
            effects.AddRange(_composition.OutputEffects.Where(effect => effect.Id != _suppressedPreviewEffectId));
            if (_pendingPreviewEffect is { } pending) effects.Add(pending);
        }
        ApplyPreviewShader(Player, Guid.Empty, effects);
    }

    /// <summary>Applies edited values to an effect that is already in a stack.</summary>
    private void UpdateVideoEffect(Guid id, bool wholeTimeline, VideoEffect values)
    {
        var target = wholeTimeline ? _composition.OutputEffects : SelectedPlacement()?.Effects;
        if (target?.FirstOrDefault(item => item.Id == id) is not { } existing) return;
        EnsureProjectHistory(); existing.CopyFrom(values); AfterEffectChange();
    }

    // ---- Lottie animations --------------------------------------------------
    // Skottie rasterizes the JSON: a frame at a time for the preview, and a numbered
    // PNG sequence for the exporter, which the compositor takes as an image sequence.

    private readonly Dictionary<string, LottieAnimationSource?> _lottieSources = new(StringComparer.OrdinalIgnoreCase);
    private bool _lottieDisposeHooked;

    private LottieAnimationSource? LottieFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_lottieSources.TryGetValue(path, out var cached)) return cached;
        var source = LottieAnimationSource.TryLoad(path);
        _lottieSources[path] = source;
        if (!_lottieDisposeHooked)
        {
            _lottieDisposeHooked = true;
            Closed += (_, _) => { foreach (var item in _lottieSources.Values) item?.Dispose(); _lottieSources.Clear(); };
        }
        return source;
    }

    /// <summary>The animation's frame at an overlay-local time, sized for the preview.</summary>
    private System.Windows.Media.Imaging.BitmapSource? LottieFrameFor(GraphicsOverlay graphic, TimeSpan offset, double surfaceWidth, double surfaceHeight)
    {
        if (LottieFor(graphic.ImagePath) is not { } animation) return null;
        var width = (int)Math.Clamp(graphic.Width * surfaceWidth, 8, 2048);
        var height = (int)Math.Clamp(graphic.Height * surfaceHeight, 8, 2048);
        var seconds = animation.Duration.TotalSeconds <= 0 ? 0 : offset.TotalSeconds % animation.Duration.TotalSeconds;
        return animation.RenderFrame(TimeSpan.FromSeconds(Math.Max(0, seconds)), width, height);
    }

    /// <summary>
    /// Rasterizes an animation to a PNG sequence and returns the ffmpeg input pattern.
    /// The folder name carries the parameters, so an unchanged overlay is not re-rendered
    /// and a running export cannot have its frames rewritten underneath it.
    /// </summary>
    private string? RenderLottieSequence(GraphicsOverlay graphic)
    {
        if (LottieFor(graphic.ImagePath) is not { } animation) return null;
        const double fps = 30;
        var width = Math.Clamp((int)Math.Round(graphic.Width * 1920), 16, 1920);
        var height = Math.Clamp((int)Math.Round(graphic.Height * 1080), 16, 1080);
        var frames = Math.Max(1, (int)Math.Ceiling(graphic.Duration.TotalSeconds * fps));
        var folder = Path.Combine(_cache, $"lottie-{graphic.Id:N}-{width}x{height}-{frames}");
        var pattern = Path.Combine(folder, "frame_%05d.png");
        try
        {
            if (!Directory.Exists(folder) || Directory.GetFiles(folder, "frame_*.png").Length < frames)
                animation.RenderSequence(folder, width, height, fps, graphic.Duration);
            return pattern;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"The animation could not be rendered.\n{exception.Message}", "Animations", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    // ---- animations window --------------------------------------------------

    private AnimationsWindow? _animationsWindow;

    private void ToggleAnimationsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_animationsWindow is not null) { _animationsWindow.Close(); return; }
        ShowAnimationsWindow();
    }

    private void ShowAnimationsWindow()
    {
        if (_animationsWindow is not null) { _animationsWindow.Activate(); return; }
        _animationsWindow = new AnimationsWindow(new AnimationHost(
            () => _settings.AnimationPaths.Where(File.Exists).ToArray(),
            RememberAnimation, ForgetAnimation, AddAnimationToTimeline,
            path => LottieFor(path)?.RenderFrame(TimeSpan.Zero, 152, 112),
            DescribeAnimation), this);
        _animationsWindow.Closed += (_, _) => { _animationsWindow = null; if (AnimationsWindowMenuItem is not null) AnimationsWindowMenuItem.IsChecked = false; };
        AnimationsWindowMenuItem.IsChecked = true; _animationsWindow.Show();
    }

    private void RememberAnimation(string path)
    {
        if (LottieFor(path) is null) { MessageBox.Show(this, $"{Path.GetFileName(path)} is not a Lottie animation Post can read.", "Animations", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var paths = _settings.AnimationPaths.Where(item => !item.Equals(path, StringComparison.OrdinalIgnoreCase)).Prepend(path).Take(40).ToArray();
        _settings = _settings with { AnimationPaths = paths }; _settings.Save();
    }

    private void ForgetAnimation(string path)
    {
        _settings = _settings with { AnimationPaths = _settings.AnimationPaths.Where(item => !item.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray() };
        _settings.Save();
    }

    private string DescribeAnimation(string path)
        => LottieFor(path) is { } animation
            ? $"{animation.Duration.TotalSeconds:0.##}s · {animation.FrameRate:0}fps · {animation.Size.Width:0}×{animation.Size.Height:0}"
            : "could not be read";

    /// <summary>Drops an animation onto the timeline as its own graphics layer.</summary>
    private void AddAnimationToTimeline(string path, bool ownLayer)
    {
        if (LottieFor(path) is not { } animation) return;
        EnsureProjectHistory();
        var layer = ownLayer ? CreateGraphicsLayer(GraphicsOverlayKind.Lottie) : _composition.Layers.FirstOrDefault(item => item.Kind == TimelineLayerKind.Graphics) ?? CreateGraphicsLayer(GraphicsOverlayKind.Lottie);
        var aspect = animation.Size.Height > 0 ? animation.Size.Width / animation.Size.Height : 16 / 9d;
        var height = .35; var width = Math.Clamp(height * aspect * (1080 / 1920d), .05, 1);
        var graphic = new GraphicsOverlay
        {
            Kind = GraphicsOverlayKind.Lottie, ImagePath = path, Text = Path.GetFileNameWithoutExtension(path),
            Start = _editPosition, Duration = animation.Duration > TimeSpan.Zero ? animation.Duration : TimeSpan.FromSeconds(3),
            X = .5 - width / 2, Y = .5 - height / 2, Width = width, Height = height, PreserveAspectRatio = true,
        };
        layer.Graphics.Add(graphic);
        ExtendWorkspace(graphic.End);
        _activeLayerId = layer.Id; _selectedGraphicId = graphic.Id;
        InvalidateCompositionPreview(); CommitProjectEdit(); RefreshLayerStack(); UpdateLiveGraphics(_editPosition);
    }

    // ---- colour grading -----------------------------------------------------

    private void ToggleColorGradingWindow_Click(object sender, RoutedEventArgs e)
    {
        if (IsPaneVisible("grading")) { ClosePane("grading"); _gradingPanel?.StopPreview(); return; }
        ShowColorGradingWindow();
    }

    private void ShowColorGradingWindow()
    {
        OpenToolPane("grading", "Color Grading", 620, 560);
        if (ColorGradingWindowMenuItem is not null) ColorGradingWindowMenuItem.IsChecked = true;
    }

    /// <summary>
    /// The grading panel's contents, built the first time the pane is opened and kept, so
    /// a grade being worked on survives the pane being docked, floated or put away.
    /// </summary>
    private FrameworkElement BuildGradingPanel()
    {
        _gradingPanel = new ColorGradingPanel(SetPreviewGrade, AddGradeAsLut);
        return _gradingPanel;
    }

    /// <summary>
    /// Renders the frame under the caret through the grade. The grade is baked to a LUT
    /// and applied with lut3d, so the preview and the effect use identical maths.
    /// </summary>
    private async Task<string?> RenderGradePreviewAsync(ColorGrade grade)
    {
        var active = ResolveVideoFrameAt(_editPosition);
        string source; TimeSpan position;
        if (active is { } value) { source = value.Placement.Clip.SourcePath; position = value.SourcePosition.SourceTime; }
        else if (_current is { } clip && clip.Media.HasVideo) { source = clip.SourcePath; position = TimeSpan.Zero; }
        else return null;

        Directory.CreateDirectory(_cache);
        var token = Guid.NewGuid().ToString("N");
        var cube = Path.Combine(_cache, $"grade-{token}.cube");
        var output = Path.Combine(_cache, $"grade-{token}.png");
        try
        {
            // A coarse lattice is plenty for a preview and keeps the slider responsive.
            grade.SaveCube(cube, "Preview", 17);
            await _engine.CaptureFrameAsync(source, position, output, default, [new VideoEffect { Kind = VideoEffectKind.Lut, FilePath = cube }]);
        }
        finally { try { File.Delete(cube); } catch { } }
        if (_lastGradePreview is { } previous) { try { File.Delete(previous); } catch { } }
        _lastGradePreview = output;
        return output;
    }

    /// <summary>Applies a ready-made look by generating its LUT and adding it to the stack.</summary>
    private void AddLookStyle(string name, bool wholeTimeline)
    {
        if (LookStyles.Find(name) is not { } style) return;
        try
        {
            var path = LookStyles.EnsureCube(style, LutLibrary.Folder);
            AddVideoEffect(new VideoEffect { Kind = VideoEffectKind.Lut, FilePath = path }, wholeTimeline);
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Styles", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AddGradeAsLut(ColorGrade grade, bool wholeTimeline)
    {
        // The LUT outlives the session, so it goes next to the settings rather than in the cache.
        var path = Path.Combine(LutLibrary.Folder, $"grade-{DateTime.Now:yyyyMMdd_HHmmss}.cube");
        try { grade.SaveCube(path, "Post grade"); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Color Grading", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        AddVideoEffect(new VideoEffect { Kind = VideoEffectKind.Lut, FilePath = path }, wholeTimeline);
        _effectsPanel?.RefreshApplied();
    }

    // ---- equalizer ----------------------------------------------------------

    private void ToggleEqualizerWindow_Click(object sender, RoutedEventArgs e)
    {
        if (IsPaneVisible("equalizer")) { HideEqualizerPane(); return; }
        ShowEqualizerWindow();
    }

    /// <summary>Opens Audio EQ, floating on first use so it behaves like the window it was.</summary>
    private void ShowEqualizerWindow()
    {
        EnsureProjectHistory();
        OpenToolPane("equalizer", "Audio EQ", 720, 480);
        EqualizerWindowMenuItem.IsChecked = true;
    }

    private EqualizerPanel BuildEqualizerPanel()
    {
        if (_equalizerPanel is null)
        {
            _equalizerPanel = new EqualizerPanel(_composition.Equalizer, EqualizerChanged);
            _equalizerPanel.CloseRequested = HideEqualizerPane;
        }
        return _equalizerPanel;
    }

    /// <summary>
    /// Sliders move continuously, so the edit is recorded once when the pane is put away
    /// rather than on every nudge.
    /// </summary>
    private void HideEqualizerPane()
    {
        ClosePane("equalizer");
        CommitProjectEdit();
        if (EqualizerWindowMenuItem is not null) EqualizerWindowMenuItem.IsChecked = false;
    }

    // ---- equalized preview proxies -----------------------------------------
    // MediaElement cannot filter audio, so the live preview plays a proxy whose audio
    // has already been through the equalizer. Video is stream-copied into it, which
    // keeps the rebuild to an audio-only encode.

    private readonly Dictionary<string, string> _equalizedPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _equalizedPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private CancellationTokenSource? _equalizedPreviewWork;
    private string _equalizedSignature = "";
    private bool _equalizedPreviewTimerReady;

    /// <summary>The file the live preview should play for a clip, equalized when possible.</summary>
    private string? LivePreviewPathFor(ClipItem clip)
    {
        if (clip.PreviewPath is not { } path) return null;
        // Our own mixer equalizes in real time, so the proxy is only for the fallback path.
        if (PreviewAudioActive) return path;

        return _equalizedPreviews.TryGetValue(path, out var equalized) && File.Exists(equalized) ? equalized : path;
    }

    private void EqualizerChanged()
    {
        // InvalidateCompositionPreview() tears down the live preview, which is right for
        // a picture edit but not for this: an equalizer change only affects audio, and
        // the mixer applies it to the running preview on the next buffer.
        EnsurePreviewAudio();
        if (PreviewAudioActive)
        {
            _previewAudio!.SetEqualizer(_composition.Equalizer);
            UpdateProjectMediaPreviewAudio();
            UpdateCompositionPreviewAudio();
            // A rendered composite carries baked-in audio, so it is only stale once the
            // mixer stops covering it.
            if (_compositionPreviewActive && !PreviewAudioEngaged) InvalidateCompositionPreview();
            _equalizerPanel?.SetStatus(PreviewAudioEngaged
                ? "Live — the preview mixer is applying this as you drag."
                : "");
            return;
        }
        InvalidateCompositionPreview();
        QueueEqualizedPreview();
    }

    /// <summary>
    /// A rendered composition preview plays one flattened file, so its audio is baked in.
    /// While the equalizer is engaged that file is muted and the mixer plays the source
    /// clips instead, following the composite's own playback position.
    /// </summary>
    private void UpdateCompositionPreviewAudio()
    {
        if (!_compositionPreviewActive || Player is null) return;
        var owns = PreviewAudioEngaged;
        Player.IsMuted = _muted || owns;
        if (!owns) { _previewAudio?.RemoveAllExcept([]); return; }

        var engine = _previewAudio!;
        var time = Player.Position;
        var active = new HashSet<Guid>();
        foreach (var item in TimelineOperations.ActivePlacementsAt(_composition, time)
            .Where(entry => LayerIncludedInPreview(entry.Layer) && PlacementIncludedInPreview(entry.Placement)))
        {
            var placement = item.Placement;
            // Split out onto its own layer, so this copy of it is not heard.
            if (!placement.Clip.Media.HasAudio || placement.AudioMuted || placement.Clip.PreviewPath is not { } path) continue;
            active.Add(placement.Id);
            engine.EnsureSource(placement.Id, path);
            var volume = KeyframeEvaluator.Evaluate(placement.Keyframes, KeyframeProperty.Volume, time - placement.Start, 1);
            engine.SetGain(placement.Id, _muted || item.Layer.IsMuted ? 0 : volume,
                item.Layer.Kind == TimelineLayerKind.Audio && item.Layer.MuteLeftChannel,
                item.Layer.Kind == TimelineLayerKind.Audio && item.Layer.MuteRightChannel,
                item.Layer.ChannelSource);
            engine.SyncPosition(placement.Id, item.SourcePosition.SourceTime, _playing ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(20));
        }
        engine.SetMasterVolume(PreviewVolume.Value, _muted);
        engine.SetPlaying(_playing);
        engine.RemoveAllExcept(active);
    }

    // ---- live preview audio -------------------------------------------------

    private PreviewAudioEngine? _previewAudio;
    private readonly HashSet<Guid> _previewAudioSources = [];
    private readonly Guid _projectMediaAudioId = Guid.NewGuid();
    private bool _previewAudioDisposeHooked;

    /// <summary>True once an output device is open.</summary>
    private bool PreviewAudioActive => _previewAudio?.IsAvailable == true;

    /// <summary>
    /// True while our mixer should own preview audio. It only takes over when the
    /// equalizer is actually doing something, so an unused equalizer leaves the normal
    /// WPF playback path exactly as it was.
    /// </summary>
    // The engine is what can actually play one channel centred, so a split layer engages
    // it as surely as an equalizer setting does.
    private bool PreviewAudioEngaged => PreviewAudioActive
        && (!_composition.Equalizer.IsFlat || _composition.Layers.Any(layer => layer.ChannelSource != AudioChannelSource.Both));

    /// <summary>Opens the output device once; the engine then lives as long as the window.</summary>
    private void EnsurePreviewAudio()
    {
        if (_previewAudio is not null) return;
        var engine = new PreviewAudioEngine();
        if (!engine.Start())
        {
            engine.Dispose();
            _equalizerPanel?.SetStatus("No audio output device was available, so the preview falls back to a rendered copy.");
            return;
        }
        _previewAudio = engine;
        engine.SetEqualizer(_composition.Equalizer);
        engine.SetMasterVolume(PreviewVolume.Value, _muted);
        if (!_previewAudioDisposeHooked) { _previewAudioDisposeHooked = true; Closed += (_, _) => { _previewAudio?.Dispose(); _previewAudio = null; }; }
    }

    /// <summary>Drops the timeline sources when the live preview stops; the device stays open.</summary>
    private void ClearLivePreviewAudio()
    {
        _previewAudio?.RemoveAllExcept([]);
        _previewAudioSources.Clear();
    }

    /// <summary>
    /// Single-clip preview from the media panel plays through Player (or the audio-only
    /// MediaPlayer). When the equalizer is engaged, that player is muted and the sound
    /// comes from the mixer instead, synced to the player's own position.
    /// </summary>
    private void UpdateProjectMediaPreviewAudio()
    {
        if (_livePreviewActive || _compositionPreviewActive) return;
        var owns = PreviewAudioEngaged && _projectMediaPreviewActive && _current is { } clip && clip.Media.HasAudio && clip.PreviewPath is not null;
        if (Player is not null) Player.IsMuted = _muted || owns;
        _audioPreviewPlayer.IsMuted = _muted || owns;
        if (!owns) { _previewAudio?.Remove(_projectMediaAudioId); return; }

        var engine = _previewAudio!;
        engine.EnsureSource(_projectMediaAudioId, _current!.PreviewPath!);
        engine.SetGain(_projectMediaAudioId, 1, false, false);
        engine.SetMasterVolume(PreviewVolume.Value, _muted);
        var position = _projectMediaAudioPreviewActive || Player is null ? _audioPreviewPlayer.Position : Player.Position;
        engine.SyncPosition(_projectMediaAudioId, position, _playing ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(20));
        engine.SetPlaying(_playing);
    }

    /// <summary>Keeps one mixer source per active placement, gain-staged and in sync.</summary>
    private void UpdatePreviewAudioSource(TimelineLayer layer, TimelinePlacement placement, TimeSpan sourceTime, double animatedVolume, bool playing)
    {
        if (!PreviewAudioEngaged || !placement.Clip.Media.HasAudio) return;
        if (placement.Clip.PreviewPath is not { } path) return;
        var engine = _previewAudio!;
        engine.EnsureSource(placement.Id, path);
        _previewAudioSources.Add(placement.Id);
        var muted = _muted || layer.IsMuted;
        engine.SetGain(placement.Id, muted ? 0 : Math.Clamp(animatedVolume, 0, 4),
            layer.Kind == TimelineLayerKind.Audio && layer.MuteLeftChannel,
            layer.Kind == TimelineLayerKind.Audio && layer.MuteRightChannel,
            layer.ChannelSource);
        // Seek hard while paused or scrubbing; while playing, only correct real drift.
        engine.SyncPosition(placement.Id, sourceTime, playing ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(20));
    }

    /// <summary>Drops sources for placements that are no longer under the playhead.</summary>
    private void FinishPreviewAudioFrame(IReadOnlyCollection<Guid> activeIds, bool playing)
    {
        if (_previewAudio is null) return;
        if (!PreviewAudioEngaged) { ClearLivePreviewAudio(); return; }
        var engine = _previewAudio;
        engine.SetMasterVolume(PreviewVolume.Value, _muted);
        engine.SetPlaying(playing);
        engine.RemoveAllExcept(activeIds);
        _previewAudioSources.RemoveWhere(id => !activeIds.Contains(id));
    }

    /// <summary>Waits for the sliders to settle before spending an encode on them.</summary>
    private void QueueEqualizedPreview()
    {
        if (!_equalizedPreviewTimerReady)
        {
            _equalizedPreviewTimer.Tick += async (_, _) => { _equalizedPreviewTimer.Stop(); await RebuildEqualizedPreviewsAsync(); };
            _equalizedPreviewTimerReady = true;
        }
        _equalizedPreviewTimer.Stop(); _equalizedPreviewTimer.Start();
    }

    private async Task RebuildEqualizedPreviewsAsync()
    {
        if (PreviewAudioActive) return;

        var equalizer = _composition.Equalizer;
        var signature = string.Join(',', equalizer.BuildFilters());
        if (signature == _equalizedSignature) return;
        try { _equalizedPreviewWork?.Cancel(); } catch (ObjectDisposedException) { }
        var work = new CancellationTokenSource();
        _equalizedPreviewWork = work;
        try
        {
            if (signature.Length == 0)
            {
                _equalizedSignature = ""; if (_equalizedPreviews.Count == 0) return;
                _equalizedPreviews.Clear(); _equalizerPanel?.SetStatus("Equalizer is flat — the preview plays the original audio.");
                await RestartLivePreviewAsync(); return;
            }

            var clips = _composition.Layers.Where(LayerIncludedInPreview).SelectMany(layer => layer.Placements)
                .Select(placement => placement.Clip).Where(clip => clip.Media.HasAudio && clip.PreviewPath is not null)
                .Distinct().ToArray();
            if (clips.Length == 0) { _equalizedSignature = signature; return; }

            _equalizerPanel?.SetStatus($"Preparing equalized preview for {clips.Length} clip{(clips.Length == 1 ? "" : "s")}…");
            var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in clips)
            {
                var source = clip.PreviewPath!;
                built[source] = await _engine.CreateEqualizedPreviewAsync(source, clip.Media.HasVideo, equalizer, _cache, work.Token);
            }
            if (work.IsCancellationRequested) return;
            _equalizedPreviews.Clear(); foreach (var item in built) _equalizedPreviews[item.Key] = item.Value;
            _equalizedSignature = signature;
            _equalizerPanel?.SetStatus("Equalized preview ready — the player now uses it.");
            await RestartLivePreviewAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _equalizerPanel?.SetStatus($"Could not build the equalized preview: {exception.Message}"); }
        finally { if (ReferenceEquals(_equalizedPreviewWork, work)) _equalizedPreviewWork = null; work.Dispose(); }
    }

    /// <summary>Reopens the live players so they pick up the swapped files.</summary>
    private async Task RestartLivePreviewAsync()
    {
        if (!_livePreviewActive) return;
        var playing = _playing;
        StopLivePreview();
        await StartLivePreviewAsync(playing);
    }
}
