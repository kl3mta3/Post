using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Post.Core;

namespace Post.App;

/// <summary>
/// Plays the timeline's audio through our own mixer so effects can be applied live.
/// MediaElement offers no DSP hook, so during live preview its audio is muted and the
/// sound comes from here instead: each clip is decoded, gain-staged and mixed, then the
/// mix runs through an equalizer whose coefficients can change between buffers.
/// </summary>
internal sealed class PreviewAudioEngine : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Source> _sources = [];
    private readonly MixingSampleProvider _mixer;
    private readonly AudioEqualizerProcessor _equalizer = new(SampleRate, Channels);
    private IWavePlayer? _output;
    private float _masterVolume = 1;
    private bool _muted;
    private bool _disposed;

    public PreviewAudioEngine()
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels)) { ReadFully = true };
    }

    /// <summary>False when no output device could be opened; callers then fall back to WPF audio.</summary>
    public bool IsAvailable { get; private set; }

    public bool Start()
    {
        if (_output is not null) return IsAvailable;
        try
        {
            // Shared-mode WASAPI keeps latency low without taking over the device.
            var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 80);
            output.Init(new EqualizedSampleProvider(_mixer, _equalizer, () => _muted ? 0 : _masterVolume));
            output.Play();
            _output = output; IsAvailable = true;
        }
        catch
        {
            try
            {
                var fallback = new WaveOutEvent { DesiredLatency = 120 };
                fallback.Init(new EqualizedSampleProvider(_mixer, _equalizer, () => _muted ? 0 : _masterVolume));
                fallback.Play();
                _output = fallback; IsAvailable = true;
            }
            catch { IsAvailable = false; }
        }
        return IsAvailable;
    }

    /// <summary>Applies new equalizer settings immediately, without interrupting playback.</summary>
    public void SetEqualizer(AudioEqualizer? equalizer) => _equalizer.Update(equalizer);

    public void SetMasterVolume(double volume, bool muted)
    {
        _masterVolume = (float)Math.Clamp(volume, 0, 1); _muted = muted;
    }

    /// <summary>Adds a clip to the mix, or returns the one already playing for this placement.</summary>
    public void EnsureSource(Guid id, string path)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(id, out var existing))
            {
                if (string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)) return;
                Remove(id);
            }
            try
            {
                var reader = new MediaFoundationReader(path);
                ISampleProvider samples = reader.ToSampleProvider();
                if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
                if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);
                var gain = new SourceGainProvider(samples);
                var source = new Source(reader, gain, path);
                _sources[id] = source;
                _mixer.AddMixerInput(gain);
            }
            catch { /* an undecodable source simply stays silent */ }
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_sources.Remove(id, out var source)) return;
            try { _mixer.RemoveMixerInput(source.Gain); } catch { }
            source.Dispose();
        }
    }

    public void RemoveAllExcept(IReadOnlyCollection<Guid> keep)
    {
        Guid[] stale;
        lock (_gate) stale = _sources.Keys.Where(id => !keep.Contains(id)).ToArray();
        foreach (var id in stale) Remove(id);
    }

    public void SetGain(Guid id, double volume, bool muteLeft, bool muteRight)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source)) return;
            source.Gain.Volume = (float)Math.Clamp(volume, 0, 4);
            source.Gain.LeftMuted = muteLeft; source.Gain.RightMuted = muteRight;
        }
    }

    /// <summary>Seeks a source when it has drifted from where the timeline expects it.</summary>
    public void SyncPosition(Guid id, TimeSpan position, TimeSpan tolerance)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source)) return;
            try
            {
                if ((source.Reader.CurrentTime - position).Duration() <= tolerance) return;
                var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > source.Reader.TotalTime ? source.Reader.TotalTime : position;
                source.Reader.CurrentTime = clamped;
            }
            catch { }
        }
    }

    public void SetPlaying(bool playing)
    {
        lock (_gate) foreach (var source in _sources.Values) source.Gain.Running = playing;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var source in _sources.Values) { try { _mixer.RemoveMixerInput(source.Gain); } catch { } source.Dispose(); }
            _sources.Clear();
        }
        try { _output?.Stop(); _output?.Dispose(); } catch { }
        _output = null; IsAvailable = false;
    }

    private sealed record Source(MediaFoundationReader Reader, SourceGainProvider Gain, string Path) : IDisposable
    {
        public void Dispose() { try { Reader.Dispose(); } catch { } }
    }

    /// <summary>Per-clip volume, channel muting, and a pause switch that feeds silence.</summary>
    private sealed class SourceGainProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public float Volume { get; set; } = 1;
        public bool LeftMuted { get; set; }
        public bool RightMuted { get; set; }
        public bool Running { get; set; }

        public int Read(float[] buffer, int offset, int count)
        {
            // While paused the mixer still pulls, so hand back silence and hold position.
            if (!Running) { Array.Clear(buffer, offset, count); return count; }
            var read = source.Read(buffer, offset, count);
            for (var i = read; i < count; i++) buffer[offset + i] = 0;
            for (var i = 0; i < count; i++)
            {
                var channel = (offset + i) % WaveFormat.Channels;
                var muted = channel == 0 ? LeftMuted : RightMuted;
                buffer[offset + i] = muted ? 0 : buffer[offset + i] * Volume;
            }
            return count;
        }
    }

    /// <summary>The master chain: the mix, then the equalizer, then preview volume.</summary>
    private sealed class EqualizedSampleProvider(ISampleProvider source, AudioEqualizerProcessor equalizer, Func<float> volume) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read <= 0) return read;
            equalizer.Process(buffer, offset, read);
            var level = volume();
            if (Math.Abs(level - 1) > .0001) for (var i = 0; i < read; i++) buffer[offset + i] *= level;
            return read;
        }
    }
}
