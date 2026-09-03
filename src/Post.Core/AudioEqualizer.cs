using System.Collections.ObjectModel;
using System.Globalization;

namespace Post.Core;

public sealed class AudioEqualizerBand
{
    public double FrequencyHz { get; set; } = 1000;
    public double GainDb { get; set; }
    /// <summary>Bandwidth as a Q factor; lower is wider.</summary>
    public double Width { get; set; } = 1;
    public AudioEqualizerBand Clone() => new() { FrequencyHz = FrequencyHz, GainDb = GainDb, Width = Width };
}

/// <summary>A graphic equalizer applied to the mixed audio of a timeline or clip.</summary>
public sealed class AudioEqualizer
{
    public static readonly double[] DefaultFrequencies = [60, 150, 400, 1000, 2400, 6000, 12000, 16000];

    public bool IsEnabled { get; set; } = true;
    /// <summary>Overall make-up gain in decibels.</summary>
    public double GainDb { get; set; }
    public ObservableCollection<AudioEqualizerBand> Bands { get; } = [];

    public static AudioEqualizer Flat()
    {
        var equalizer = new AudioEqualizer();
        foreach (var frequency in DefaultFrequencies) equalizer.Bands.Add(new AudioEqualizerBand { FrequencyHz = frequency, GainDb = 0, Width = 1 });
        return equalizer;
    }

    public bool IsFlat => !IsEnabled || (Math.Abs(GainDb) < .05 && Bands.All(band => Math.Abs(band.GainDb) < .05));

    public AudioEqualizer Clone()
    {
        var copy = new AudioEqualizer { IsEnabled = IsEnabled, GainDb = GainDb };
        foreach (var band in Bands) copy.Bands.Add(band.Clone());
        return copy;
    }

    public void CopyFrom(AudioEqualizer other)
    {
        IsEnabled = other.IsEnabled; GainDb = other.GainDb;
        Bands.Clear(); foreach (var band in other.Bands) Bands.Add(band.Clone());
    }

    /// <summary>Named starting points offered in the equalizer window.</summary>
    public static IReadOnlyList<(string Name, double[] Gains)> Presets { get; } =
    [
        ("Flat", [0, 0, 0, 0, 0, 0, 0, 0]),
        ("Voice / podcast", [-4, -2, 1, 3, 3, 2, 0, -1]),
        ("Bass boost", [6, 4, 1, 0, 0, 0, 0, 0]),
        ("Bright", [-2, -1, 0, 1, 2, 4, 5, 4]),
        ("Warm", [3, 2, 1, 0, -1, -2, -2, -3]),
        ("Reduce rumble", [-10, -4, 0, 0, 0, 0, 0, 0]),
    ];

    /// <summary>Builds the ffmpeg audio filters for this equalizer, or an empty list when flat.</summary>
    public List<string> BuildFilters()
    {
        var filters = new List<string>();
        if (!IsEnabled) return filters;
        foreach (var band in Bands)
        {
            var gain = Math.Clamp(band.GainDb, -24, 24);
            if (Math.Abs(gain) < .05) continue;
            var frequency = Math.Clamp(band.FrequencyHz, 20, 20000);
            var width = Math.Clamp(band.Width, .1, 10);
            filters.Add($"equalizer=f={S(frequency)}:t=q:w={S(width)}:g={S(gain)}");
        }
        var makeUp = Math.Clamp(GainDb, -24, 24);
        if (Math.Abs(makeUp) >= .05) filters.Add($"volume={S(makeUp)}dB");
        return filters;
    }

    private static string S(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
