using Post.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Post.App;

/// <summary>
/// Background export plumbing: jobs run outside the editor, a chip in the project bar
/// shows overall progress plus an estimate, and the chip opens a job window.
/// </summary>
public partial class MainWindow
{
    private readonly ExportJobManager _exportJobs = new();
    private readonly DispatcherTimer _exportJobTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _exportChipHideTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<Guid, Action<ExportJob>> _exportJobCompletions = [];
    private readonly Dictionary<Guid, string> _exportJobScratch = [];
    private Window? _exportJobsWindow;
    private Action? _refreshExportJobsWindow;

    private void InitializeExportJobs()
    {
        _exportJobs.Changed += (_, _) => UpdateExportJobsChip();
        _exportJobs.Finished += ExportJobFinished;
        _exportJobTimer.Tick += (_, _) => { if (_exportJobs.HasRunning) _exportJobs.Refresh(); else _exportJobTimer.Stop(); };
        _exportChipHideTimer.Tick += (_, _) => { _exportChipHideTimer.Stop(); if (!_exportJobs.HasRunning && !_exportJobs.Jobs.Any(job => job.State == ExportJobState.Failed)) _exportJobs.ClearFinished(); };
        Closing += (_, e) =>
        {
            if (!_exportJobs.HasRunning) return;
            var answer = MessageBox.Show(this, $"{_exportJobs.Running.Count} export(s) are still running. Cancel them and close Post?", "Post", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) _exportJobs.CancelAll(); else e.Cancel = true;
        };
        UpdateExportJobsChip();
    }

    /// <summary>Queues an export to run in the background and returns immediately.</summary>
    private ExportJob StartExportJob(string title, string? outputPath, Func<IProgress<ExportProgress>, CancellationToken, Task> work, Action<ExportJob>? onCompleted = null, string? scratchFolder = null)
    {
        var job = _exportJobs.Start(title, outputPath, work);
        if (onCompleted is not null) _exportJobCompletions[job.Id] = onCompleted;
        if (scratchFolder is not null) _exportJobScratch[job.Id] = scratchFolder;
        if (!_exportJobTimer.IsEnabled) _exportJobTimer.Start();
        _exportChipHideTimer.Stop();
        return job;
    }

    private void ExportJobFinished(object? sender, ExportJob job)
    {
        if (_exportJobScratch.Remove(job.Id, out var scratch)) { try { Directory.Delete(scratch, true); } catch { } }
        if (_exportJobCompletions.Remove(job.Id, out var completion) && job.State == ExportJobState.Completed) completion(job);
        // A publish that failed on authorization offers a new sign-in instead of an error.
        if (job.State == ExportJobState.Failed && !HandlePublishAuthFailure(job))
            MessageBox.Show(this, job.Error ?? "The export failed.", job.Title, MessageBoxButton.OK, MessageBoxImage.Error);
        _refreshExportJobsWindow?.Invoke();
        UpdateExportJobsChip();
        if (!_exportJobs.HasRunning) _exportChipHideTimer.Start();
    }

    private void UpdateExportJobsChip()
    {
        var jobs = _exportJobs.Jobs;
        if (jobs.Count == 0) { ExportJobsChip.Visibility = Visibility.Collapsed; _refreshExportJobsWindow?.Invoke(); return; }
        ExportJobsChip.Visibility = Visibility.Visible;
        var running = _exportJobs.Running;
        if (running.Count > 0)
        {
            var fraction = running.Average(job => job.Fraction);
            var remaining = running.Select(job => job.Remaining).Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty(TimeSpan.Zero).Max();
            var label = running.Count == 1 ? running[0].Title : $"{running.Count} exports";
            ExportJobsChipIcon.Text = "⏳";
            ExportJobsChipText.Text = remaining > TimeSpan.Zero
                ? $"{label} · {fraction * 100:0}% · ~{ExportJob.Describe(remaining)} left"
                : $"{label} · {fraction * 100:0}%";
            ExportJobsChipBar.Visibility = Visibility.Visible; ExportJobsChipBar.Value = fraction;
            ExportJobsChip.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 215, 208));
        }
        else
        {
            var failed = jobs.Count(job => job.State == ExportJobState.Failed);
            var canceled = jobs.Count(job => job.State == ExportJobState.Canceled);
            var done = jobs.Count(job => job.State == ExportJobState.Completed);
            ExportJobsChipIcon.Text = failed > 0 ? "⚠" : "✓";
            ExportJobsChipText.Text = failed > 0 ? $"{failed} export(s) failed" : canceled > 0 && done == 0 ? $"{canceled} export(s) canceled" : done == 1 ? "Export finished" : $"{done} exports finished";
            ExportJobsChipBar.Visibility = Visibility.Collapsed;
            ExportJobsChip.BorderBrush = new SolidColorBrush(failed > 0 ? Color.FromRgb(226, 106, 106) : Color.FromRgb(35, 52, 82));
        }
        ExportJobsChip.ToolTip = string.Join("\n", jobs.Select(DescribeJob));
        _refreshExportJobsWindow?.Invoke();
    }

    private static string DescribeJob(ExportJob job) => job.State switch
    {
        ExportJobState.Running => $"{job.Title} — {job.Fraction * 100:0}% · {job.Stage} · {ExportJob.Describe(job.Elapsed)} elapsed{(job.Remaining is { } left ? $" · ~{ExportJob.Describe(left)} left" : "")}",
        ExportJobState.Completed => $"{job.Title} — finished in {ExportJob.Describe(job.Elapsed)}",
        ExportJobState.Canceled => $"{job.Title} — canceled",
        _ => $"{job.Title} — failed: {job.Error}",
    };

    private void ExportJobs_Click(object sender, RoutedEventArgs e) => ShowExportJobsWindow();

    /// <summary>
    /// Opens the export monitor. It is intentionally modeless: blocking the editor
    /// would undo the point of running exports in the background.
    /// </summary>
    private void ShowExportJobsWindow()
    {
        if (_exportJobsWindow is not null) { _exportJobsWindow.Activate(); return; }
        var list = new StackPanel { Margin = new Thickness(16) };
        var clear = new Button { Content = "Clear finished", Padding = new Thickness(12, 6, 12, 6) };
        var close = new Button { Content = "Close", Padding = new Thickness(12, 6, 12, 6) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 14) };
        footer.Children.Add(clear); footer.Children.Add(close);
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var window = new Window
        {
            Title = "Exports", Width = 560, Height = 430, Content = root, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
        };
        _refreshExportJobsWindow = () => RenderExportJobRows(list);
        clear.Click += (_, _) => _exportJobs.ClearFinished();
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { _exportJobsWindow = null; _refreshExportJobsWindow = null; if (!_exportJobs.HasRunning && _exportJobs.Jobs.Count > 0) _exportChipHideTimer.Start(); };
        _exportJobsWindow = window;
        _exportChipHideTimer.Stop();
        RenderExportJobRows(list);
        window.Show();
    }

    private void RenderExportJobRows(StackPanel list)
    {
        list.Children.Clear();
        if (_exportJobs.Jobs.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No exports have been started yet.", Foreground = Brushes.LightGray, Margin = new Thickness(0, 12, 0, 0) });
            return;
        }
        foreach (var job in _exportJobs.Jobs)
        {
            var panel = new StackPanel();
            var header = new DockPanel { LastChildFill = true };
            var action = new Button { Content = job.IsFinished ? "Show file" : "Cancel", Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(8, 0, 0, 0) };
            action.IsEnabled = !job.IsFinished || (job.State == ExportJobState.Completed && job.OutputPath is not null && (File.Exists(job.OutputPath) || Directory.Exists(job.OutputPath)));
            if (job.IsFinished) action.Click += (_, _) => RevealInExplorer(job.OutputPath); else action.Click += (_, _) => { job.Cancel(); _exportJobs.Refresh(); };
            DockPanel.SetDock(action, Dock.Right); header.Children.Add(action);
            header.Children.Add(new TextBlock { Text = job.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock
            {
                Text = job.OutputPath is null ? job.Stage : $"{job.Stage} — {Path.GetFileName(job.OutputPath)}",
                Foreground = Brushes.LightGray, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
            });
            panel.Children.Add(new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Value = job.Fraction, Margin = new Thickness(0, 7, 0, 5), Foreground = new SolidColorBrush(job.State == ExportJobState.Failed ? Color.FromRgb(226, 106, 106) : Color.FromRgb(76, 215, 208)) });
            var detail = job.State switch
            {
                ExportJobState.Running => $"{job.Fraction * 100:0}% · {ExportJob.Describe(job.Elapsed)} elapsed" + (job.Remaining is { } left ? $" · about {ExportJob.Describe(left)} left" : " · estimating…"),
                ExportJobState.Completed => $"Finished in {ExportJob.Describe(job.Elapsed)} · started {job.StartedAt:t}",
                ExportJobState.Canceled => $"Canceled after {ExportJob.Describe(job.Elapsed)}",
                _ => job.Error ?? "Failed",
            };
            panel.Children.Add(new TextBlock { Text = detail, Foreground = job.State == ExportJobState.Failed ? Brushes.Salmon : Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            list.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(9, 20, 38)), BorderBrush = new SolidColorBrush(Color.FromRgb(32, 49, 76)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = panel,
            });
        }
    }

    private static void RevealInExplorer(string? path)
    {
        if (path is null) return;
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : Directory.Exists(path) ? $"\"{path}\"" : null;
        if (arguments is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); } catch { }
    }

    // ---- snapshots -------------------------------------------------------
    // A background export must render what the timeline looked like when it was
    // started, so nothing it reads may still be attached to the live editor.

    private static ClipItem CloneClip(ClipItem clip)
    {
        var copy = new ClipItem { SourcePath = clip.SourcePath, Media = clip.Media, PreviewPath = clip.PreviewPath, PendingCutStart = clip.PendingCutStart, PendingCutEnd = clip.PendingCutEnd };
        foreach (var segment in clip.Segments) copy.Segments.Add(new MediaSegment { Id = segment.Id, SourceStart = segment.SourceStart, SourceEnd = segment.SourceEnd });
        return copy;
    }

    /// <summary>
    /// Deep-copies a composition for export, including private copies of any rendered
    /// overlay images so later edits cannot overwrite a file ffmpeg is still reading.
    /// </summary>
    private static TimelineComposition CloneComposition(TimelineComposition composition, out string? scratchFolder)
    {
        string? scratch = null;
        var clips = new Dictionary<ClipItem, ClipItem>();
        var copy = new TimelineComposition { WorkspaceDuration = composition.WorkspaceDuration, RenderWorkspaceTailAsBlack = composition.RenderWorkspaceTailAsBlack };
        foreach (var effect in composition.OutputEffects) copy.OutputEffects.Add(effect.Clone());
        copy.Equalizer.CopyFrom(composition.Equalizer);
        foreach (var layer in composition.Layers)
        {
            var layerCopy = new TimelineLayer { Id = layer.Id, Name = layer.Name, IsVisible = layer.IsVisible, IsMuted = layer.IsMuted, MuteLeftChannel = layer.MuteLeftChannel, MuteRightChannel = layer.MuteRightChannel, Kind = layer.Kind };
            foreach (var placement in layer.Placements)
            {
                if (!clips.TryGetValue(placement.Clip, out var clip)) { clip = CloneClip(placement.Clip); clips[placement.Clip] = clip; }
                var placementCopy = new TimelinePlacement { Id = placement.Id, Clip = clip, Start = placement.Start, InPoint = placement.InPoint, Length = placement.Length };
                foreach (var keyframe in placement.Keyframes) placementCopy.Keyframes.Add(new AnimationKeyframe { Id = keyframe.Id, Property = keyframe.Property, Offset = keyframe.Offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation });
                foreach (var effect in placement.Effects) placementCopy.Effects.Add(effect.Clone());
                layerCopy.Placements.Add(placementCopy);
            }
            foreach (var graphic in layer.Graphics)
            {
                var graphicCopy = new GraphicsOverlay
                {
                    Id = graphic.Id, Kind = graphic.Kind, Text = graphic.Text, ImagePath = graphic.ImagePath, RenderedImagePath = graphic.RenderedImagePath,
                    FontFamily = graphic.FontFamily, FontSize = graphic.FontSize, Foreground = graphic.Foreground, Background = graphic.Background,
                    FillColor1 = graphic.FillColor1, FillColor2 = graphic.FillColor2, UseSecondFillColor = graphic.UseSecondFillColor,
                    GradientKind = graphic.GradientKind, GradientAngle = graphic.GradientAngle, Opacity = graphic.Opacity, PreserveAspectRatio = graphic.PreserveAspectRatio,
                    X = graphic.X, Y = graphic.Y, Width = graphic.Width, Height = graphic.Height, Start = graphic.Start, Duration = graphic.Duration,
                };
                foreach (var keyframe in graphic.Keyframes) graphicCopy.Keyframes.Add(new AnimationKeyframe { Id = keyframe.Id, Property = keyframe.Property, Offset = keyframe.Offset, Value = keyframe.Value, Interpolation = keyframe.Interpolation });
                if (graphic.RenderedImagePath is { } rendered && File.Exists(rendered))
                {
                    scratch ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"post-job-{Guid.NewGuid():N}")).FullName;
                    var frozen = Path.Combine(scratch, $"{graphic.Id:N}{Path.GetExtension(rendered)}");
                    try { File.Copy(rendered, frozen, true); graphicCopy.RenderedImagePath = frozen; } catch { }
                }
                layerCopy.Graphics.Add(graphicCopy);
            }
            copy.Layers.Add(layerCopy);
        }
        scratchFolder = scratch;
        return copy;
    }
}
