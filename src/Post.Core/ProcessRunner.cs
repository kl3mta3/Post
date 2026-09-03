using System.Diagnostics;
using System.Globalization;
using System.Text;
namespace Post.Core;
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string operation) { if (ExitCode != 0) throw new InvalidOperationException($"{operation} failed (exit {ExitCode}).\n{StandardError}"); }
}
/// <summary>A parsed line from ffmpeg's <c>-progress</c> stream.</summary>
public sealed record FfmpegProgress(TimeSpan OutTime, double Speed);
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
        => RunAsync(executable, arguments, null, cancellationToken);
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken = default);
}
public sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
        => RunAsync(executable, arguments, null, cancellationToken);

    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; process.Start();
        // Cancelling must actually stop the encoder; abandoning the wait would leave
        // ffmpeg running in the background and still writing to the output file.
        using var kill = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        var output = progress is null ? process.StandardOutput.ReadToEndAsync(CancellationToken.None) : ReadProgressAsync(process.StandardOutput, progress);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { try { await process.WaitForExitAsync(CancellationToken.None); } catch { } throw; }
        return new(process.ExitCode, await output, await error);
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, IProgress<FfmpegProgress> progress)
    {
        var buffer = new StringBuilder(); var time = TimeSpan.Zero; var speed = 0d;
        while (await reader.ReadLineAsync() is { } line)
        {
            // A long export emits thousands of progress blocks; keep only enough for diagnostics.
            if (buffer.Length < 64 * 1024) buffer.AppendLine(line);
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            var key = line[..split].Trim(); var value = line[(split + 1)..].Trim();
            switch (key)
            {
                // out_time is HH:MM:SS.microseconds; out_time_us is the fallback for builds that omit it.
                case "out_time" when TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed): time = parsed; break;
                case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds > 0: time = TimeSpan.FromTicks(microseconds * 10); break;
                case "speed" when double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed): speed = parsedSpeed; break;
                case "progress": progress.Report(new(time, speed)); break;
            }
        }
        return buffer.ToString();
    }
}
