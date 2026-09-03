using System.Globalization;

namespace Post.Core;

/// <summary>How much time the encoder is allowed to spend per frame.</summary>
public enum EncodeSpeed { Fast, Balanced }

/// <summary>
/// One H.264 encoder and the arguments that drive it. The GPU encoders take the same
/// jobs as libx264 but spell quality and bitrate differently, so each knows its own
/// dialect and callers just ask for quality or a bitrate.
/// </summary>
public sealed record VideoEncoder(string Name, string Label, bool IsHardware)
{
    public static readonly VideoEncoder Cpu = new("libx264", "CPU (libx264)", false);

    public IEnumerable<string> SpeedArgs(EncodeSpeed speed) => Name switch
    {
        "h264_nvenc" => speed == EncodeSpeed.Fast ? ["-preset", "p1"] : new[] { "-preset", "p5", "-tune", "hq" },
        "h264_qsv" => ["-preset", speed == EncodeSpeed.Fast ? "veryfast" : "medium"],
        "h264_amf" => ["-quality", speed == EncodeSpeed.Fast ? "speed" : "balanced"],
        _ => ["-preset", speed == EncodeSpeed.Fast ? "ultrafast" : "medium"],
    };

    /// <summary>Constant-quality arguments for a libx264-style CRF.</summary>
    public IEnumerable<string> QualityArgs(int crf)
    {
        var value = Math.Clamp(crf, 10, 40).ToString(CultureInfo.InvariantCulture);
        return Name switch
        {
            // The hardware rate controllers read the same 0-51 scale, so a CRF carries over.
            "h264_nvenc" => ["-rc", "vbr", "-cq", value, "-b:v", "0"],
            "h264_qsv" => ["-global_quality", value],
            "h264_amf" => ["-rc", "cqp", "-qp_i", value, "-qp_p", value],
            _ => ["-crf", value],
        };
    }

    public IEnumerable<string> BitrateArgs(int kilobits) =>
        ["-b:v", $"{kilobits}k", "-maxrate", $"{kilobits}k", "-bufsize", $"{kilobits * 2}k"];
}

/// <summary>
/// Finds the fastest H.264 encoder this machine can actually run. A build listing an
/// encoder is not proof it works — the GPU may be absent, busy, or driven by a driver
/// too old for it — so every candidate has to encode a frame before it is trusted.
/// </summary>
public sealed class VideoEncoderCatalog(FfmpegTools tools, IProcessRunner runner)
{
    // Fastest first. NVENC and QSV outrun AMF where a machine has more than one.
    private static readonly (string Name, string Label)[] Candidates =
    [
        ("h264_nvenc", "NVIDIA GPU (NVENC)"),
        ("h264_qsv", "Intel GPU (Quick Sync)"),
        ("h264_amf", "AMD GPU (AMF)"),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<VideoEncoder>? _available;

    /// <summary>Every usable encoder, fastest first, with the CPU always last.</summary>
    public async Task<IReadOnlyList<VideoEncoder>> AvailableAsync(CancellationToken token = default)
    {
        if (_available is not null) return _available;
        await _gate.WaitAsync(token);
        try
        {
            if (_available is not null) return _available;
            var found = new List<VideoEncoder>();
            foreach (var (name, label) in Candidates)
                if (await WorksAsync(name, token)) found.Add(new VideoEncoder(name, label, true));
            found.Add(VideoEncoder.Cpu);
            return _available = found;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The encoder to use for this preference: a named one when it is present, the
    /// fastest available when set to automatic, and the CPU when nothing else works.
    /// </summary>
    public async Task<VideoEncoder> ResolveAsync(string? preference, CancellationToken token = default)
    {
        if (string.Equals(preference, "cpu", StringComparison.OrdinalIgnoreCase)) return VideoEncoder.Cpu;
        var available = await AvailableAsync(token);
        if (!string.IsNullOrWhiteSpace(preference) && !preference.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return available.FirstOrDefault(item => item.Name.Equals(preference, StringComparison.OrdinalIgnoreCase)) ?? available[0];
        return available[0];
    }

    /// <summary>
    /// Rewrites a command that failed on a GPU encoder to run on the CPU instead,
    /// carrying the requested quality across. Returns false when there was no hardware
    /// encoder in it, which means the failure was something else.
    /// </summary>
    public static bool TryFallbackToCpu(List<string> args)
    {
        var index = args.FindIndex(item => item is "h264_nvenc" or "h264_qsv" or "h264_amf");
        if (index < 0) return false;
        args[index] = VideoEncoder.Cpu.Name;

        // Strip the hardware dialect, keeping the quality it asked for.
        string? quality = null;
        for (var i = args.Count - 2; i >= 0; i--)
        {
            var flag = args[i];
            var carriesQuality = flag is "-cq" or "-global_quality" or "-qp_i" or "-qp_p";
            if (carriesQuality) quality ??= args[i + 1];
            if (carriesQuality || flag is "-rc" or "-tune" or "-quality" || (flag is "-preset" && args[i + 1].StartsWith('p'))
                || (flag == "-b:v" && args[i + 1] == "0"))
                args.RemoveRange(i, 2);
        }
        args.InsertRange(index + 1, ["-preset", "medium", ..VideoEncoder.Cpu.QualityArgs(int.TryParse(quality, out var value) ? value : 18)]);
        return true;
    }

    private async Task<bool> WorksAsync(string encoder, CancellationToken token)
    {
        try
        {
            // A couple of frames of colour is enough to make the driver commit.
            var result = await runner.RunAsync(tools.Ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=0.1",
                 "-c:v", encoder, "-frames:v", "2", "-f", "null", "-"], token);
            return result.ExitCode == 0;
        }
        catch { return false; }
    }
}
