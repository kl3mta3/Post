namespace Post.Core;

/// <summary>
/// Real-time equalizer DSP: a cascade of peaking biquads per channel, matching the
/// RBJ cookbook filters that ffmpeg's <c>equalizer</c> filter uses, so what is heard
/// during playback matches what is rendered on export. Coefficients can be swapped
/// while audio is playing, which is what makes the preview live.
/// </summary>
public sealed class AudioEqualizerProcessor(int sampleRate, int channels)
{
    private readonly object _gate = new();
    private Biquad[][] _filters = [];
    private float _gain = 1;

    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public bool HasWork { get; private set; }

    /// <summary>Rebuilds the filter cascade. Safe to call while <see cref="Process"/> runs.</summary>
    public void Update(AudioEqualizer? equalizer)
    {
        var bands = equalizer is null || !equalizer.IsEnabled
            ? []
            : equalizer.Bands.Where(band => Math.Abs(band.GainDb) >= .05).ToArray();
        var filters = new Biquad[Channels][];
        for (var channel = 0; channel < Channels; channel++)
        {
            filters[channel] = new Biquad[bands.Length];
            for (var i = 0; i < bands.Length; i++)
                filters[channel][i] = Biquad.Peaking(SampleRate, Math.Clamp(bands[i].FrequencyHz, 20, SampleRate / 2.0 - 100), Math.Clamp(bands[i].Width, .1, 10), Math.Clamp(bands[i].GainDb, -24, 24));
        }
        var gain = equalizer is null || !equalizer.IsEnabled ? 1 : Math.Pow(10, Math.Clamp(equalizer.GainDb, -24, 24) / 20);
        lock (_gate)
        {
            _filters = filters; _gain = (float)gain;
            HasWork = bands.Length > 0 || Math.Abs(gain - 1) > .0001;
        }
    }

    /// <summary>Filters interleaved samples in place.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        Biquad[][] filters; float gain;
        lock (_gate) { filters = _filters; gain = _gain; }
        if (filters.Length == 0) return;
        for (var i = 0; i < count; i++)
        {
            var channel = (i + offset) % Channels;
            if (channel >= filters.Length) continue;
            var sample = buffer[offset + i];
            var cascade = filters[channel];
            for (var band = 0; band < cascade.Length; band++) sample = cascade[band].Process(sample);
            buffer[offset + i] = Math.Clamp(sample * gain, -1f, 1f);
        }
    }

    /// <summary>A direct-form-1 biquad. Held per channel, so it carries its own history.</summary>
    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Biquad Peaking(double sampleRate, double frequency, double q, double gainDb)
        {
            var filter = new Biquad();
            var a = Math.Pow(10, gainDb / 40);
            var w0 = 2 * Math.PI * frequency / sampleRate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha / a;
            filter._b0 = (1 + alpha * a) / a0;
            filter._b1 = -2 * cos / a0;
            filter._b2 = (1 - alpha * a) / a0;
            filter._a1 = -2 * cos / a0;
            filter._a2 = (1 - alpha / a) / a0;
            return filter;
        }

        public float Process(float input)
        {
            var output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = input; _y2 = _y1; _y1 = output;
            return (float)output;
        }
    }
}
