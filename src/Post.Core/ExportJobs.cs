using System.Diagnostics;

namespace Post.Core;

public enum ExportJobState { Running, Completed, Failed, Canceled }

/// <summary>A single export running in the background, with a smoothed time estimate.</summary>
public sealed class ExportJob
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cancellation = new();
    private double _estimateSeconds;

    internal ExportJob(string title, string? outputPath) { Title = title; OutputPath = outputPath; }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; }
    public string? OutputPath { get; }
    public string Stage { get; private set; } = "Starting…";
    public double Fraction { get; private set; }
    public ExportJobState State { get; private set; } = ExportJobState.Running;
    public string? Error { get; private set; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public TimeSpan Elapsed { get; private set; }
    public bool IsFinished => State != ExportJobState.Running;
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Estimated time left, or null until there is enough signal to be honest about it.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (IsFinished || _estimateSeconds <= 0) return null;
            return TimeSpan.FromSeconds(Math.Min(_estimateSeconds, TimeSpan.FromHours(12).TotalSeconds));
        }
    }

    public void Cancel() { if (!IsFinished) { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } } }

    internal void Report(ExportProgress progress)
    {
        // Progress must never walk backwards; stage boundaries and ffmpeg restarts can.
        Fraction = Math.Clamp(Math.Max(Fraction, progress.Fraction), 0, 1);
        Stage = progress.Stage;
        Tick();
    }

    internal void Tick()
    {
        if (IsFinished) return;
        Elapsed = _clock.Elapsed;
        if (Fraction < .02 || Elapsed < TimeSpan.FromSeconds(2)) return;
        var projected = Elapsed.TotalSeconds * (1 - Fraction) / Fraction;
        // Exponential smoothing keeps the countdown from jumping around between stages.
        _estimateSeconds = _estimateSeconds <= 0 ? projected : _estimateSeconds * .7 + projected * .3;
    }

    internal void Finish(ExportJobState state, string? error)
    {
        Elapsed = _clock.Elapsed; _clock.Stop(); State = state; Error = error;
        if (state == ExportJobState.Completed) { Fraction = 1; Stage = "Finished"; }
        else if (state == ExportJobState.Canceled) Stage = "Canceled";
        else Stage = "Failed";
        _estimateSeconds = 0;
        try { _cancellation.Dispose(); } catch { }
    }

    public static string Describe(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}h {value.Minutes}m"
        : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds:00}s" : $"{Math.Max(1, (int)Math.Ceiling(value.TotalSeconds))}s";
}

/// <summary>
/// Owns background exports so the editor stays usable while ffmpeg runs.
/// Every notification is posted back to the context that created the manager
/// (the UI thread), so subscribers can touch controls directly.
/// </summary>
public sealed class ExportJobManager
{
    private readonly List<ExportJob> _jobs = [];
    private readonly SynchronizationContext _context = SynchronizationContext.Current ?? new SynchronizationContext();

    public IReadOnlyList<ExportJob> Jobs => _jobs;
    public IReadOnlyList<ExportJob> Running => _jobs.Where(job => !job.IsFinished).ToArray();
    public bool HasRunning => _jobs.Any(job => !job.IsFinished);

    /// <summary>Raised whenever any job's progress or state changes.</summary>
    public event EventHandler? Changed;
    /// <summary>Raised once per job when it stops running, successfully or not.</summary>
    public event EventHandler<ExportJob>? Finished;

    public ExportJob Start(string title, string? outputPath, Func<IProgress<ExportProgress>, CancellationToken, Task> work)
    {
        var job = new ExportJob(title, outputPath);
        _jobs.Add(job);
        var progress = new Progress<ExportProgress>(value => { job.Report(value); Raise(); });
        _ = Task.Run(async () =>
        {
            var state = ExportJobState.Completed; string? error = null;
            try { await work(progress, job.Token); }
            catch (OperationCanceledException) { state = ExportJobState.Canceled; }
            catch (Exception exception) { state = ExportJobState.Failed; error = exception.Message; }
            _context.Post(_ => { job.Finish(state, error); Changed?.Invoke(this, EventArgs.Empty); Finished?.Invoke(this, job); }, null);
        });
        Raise();
        return job;
    }

    /// <summary>Refreshes elapsed time and estimates; call from a UI timer.</summary>
    public void Refresh() { foreach (var job in _jobs) job.Tick(); Raise(); }

    public void Remove(ExportJob job) { job.Cancel(); _jobs.Remove(job); Raise(); }
    public void ClearFinished() { _jobs.RemoveAll(job => job.IsFinished); Raise(); }
    public void CancelAll() { foreach (var job in _jobs) job.Cancel(); }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
