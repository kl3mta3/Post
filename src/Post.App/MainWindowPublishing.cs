using Microsoft.Win32;
using Post.Core;
using Post.Core.Publishing;
using System.IO;
using System.Windows;

namespace Post.App;

/// <summary>
/// The publish flow: connected accounts, their encrypted store, signing in, and the
/// job that renders a video and uploads it to each chosen account.
/// </summary>
public partial class MainWindow
{
    private readonly PublishAccountStore _publishStore = new();
    private readonly PublishService _publishService = new(new OAuthBroker());
    private List<PublishAccount>? _publishAccounts;
    private PublishWindow? _publishWindow;

    private List<PublishAccount> PublishAccounts => _publishAccounts ??= _publishStore.Load();

    private void Publish_Click(object sender, RoutedEventArgs e) => ShowPublishWindow();

    private void ShowPublishWindow()
    {
        if (_publishWindow is not null) { _publishWindow.Activate(); return; }
        var host = new PublishHost(PublishTo, account => _ = SignInToAccountAsync(account), SavePublishAccounts);
        _publishWindow = new PublishWindow(PublishAccounts, host, this);
        _publishWindow.Closed += (_, _) => _publishWindow = null;
        _publishWindow.ShowDialog();
    }

    private void SavePublishAccounts()
    {
        try { _publishStore.Save(PublishAccounts); }
        catch (Exception exception) { MessageBox.Show(this, $"The accounts could not be saved.\n{exception.Message}", "Publish", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    /// <summary>Runs the browser sign-in, then stores the tokens encrypted.</summary>
    private async Task SignInToAccountAsync(PublishAccount account)
    {
        if (!_publishService.IsSupported(account.Platform))
        {
            MessageBox.Show(this, $"Signing in to {PublishAccount.PlatformName(account.Platform)} is not connected yet.", "Publish", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        using var work = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // A platform that refuses in the browser never redirects back, so the wait has
        // to be visible and cancellable rather than silent.
        var waiting = new SignInWaitWindow(account.Platform, work, _publishWindow ?? (Window)this);
        waiting.Show();
        try
        {
            await _publishService.SignInAsync(account, work.Token);
            waiting.Finish();
            SavePublishAccounts();
            _publishWindow?.Refresh();
            MessageBox.Show(this, $"Signed in to {PublishAccount.PlatformName(account.Platform)}{(string.IsNullOrWhiteSpace(account.DisplayName) ? "" : $" as {account.DisplayName}")}.",
                "Publish", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            waiting.Finish();
            MessageBox.Show(this,
                "The sign-in was stopped before the browser came back.\n\nIf the browser showed an error instead of a consent screen, fix it in the platform's console and try again.",
                "Publish", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            waiting.Finish();
            MessageBox.Show(this, exception.Message, "Publish", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Renders the video the way an export would, then uploads that same file to each
    /// chosen account. It runs as an ordinary background job, so the progress chip and
    /// its estimate cover the upload as well as the render.
    /// </summary>
    private void PublishTo(IReadOnlyList<PublishAccount> accounts)
    {
        if (accounts.Count == 0) return;
        var unsupported = accounts.Where(account => !_publishService.IsSupported(account.Platform)).ToArray();
        if (unsupported.Length > 0)
        {
            var names = string.Join(", ", unsupported.Select(account => PublishAccount.PlatformName(account.Platform)).Distinct());
            MessageBox.Show(this, $"Publishing to {names} is not connected yet, so those accounts will be skipped.", "Publish", MessageBoxButton.OK, MessageBoxImage.Information);
            accounts = accounts.Where(account => _publishService.IsSupported(account.Platform)).ToArray();
            if (accounts.Count == 0) return;
        }

        var extension = VideoExtension;
        var dialog = new SaveFileDialog
        {
            FileName = _current is null ? $"Post_Video_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}" : DefaultName(_current, false),
            Filter = VideoFilter(extension), DefaultExt = extension, AddExtension = true, InitialDirectory = _settings.DefaultOutputFolder,
        };
        if (dialog.ShowDialog(this) != true) return;

        var output = dialog.FileName;
        var options = HasCustomComposition ? GetOptions() : GetClipOptions();
        var copies = accounts.Select(account => account.Clone()).ToArray();
        string? scratch = null;
        TimelineComposition? composition = null;
        ClipItem? clip = null;
        if (HasCustomComposition) { PrepareGraphicsForExport(); composition = CloneComposition(_composition, out scratch); }
        else if (_current is not null) clip = CloneClip(_current);
        else return;

        var title = copies.Length == 1
            ? $"Publish to {PublishAccount.PlatformName(copies[0].Platform)}"
            : $"Publish to {copies.Length} accounts";

        StartExportJob(title, output, async (progress, token) =>
        {
            // The render takes the first half of the bar; the uploads share the rest.
            var render = new Progress<ExportProgress>(value => progress.Report(new(value.Fraction * .5, value.Stage)));
            if (composition is not null) await _engine.ExportCompositionAsync(composition, output, options, render, token);
            else await _engine.ExportAsync(clip!, output, options, render, token);

            var share = .5 / copies.Length;
            var results = new List<PublishSummaryLine>();
            for (var index = 0; index < copies.Length; index++)
            {
                var account = copies[index];
                var offset = .5 + share * index;
                var upload = new Progress<PublishProgress>(value => progress.Report(new(offset + value.Fraction * share, value.Stage)));
                PublishResult result;
                try { result = await _publishService.PublishAsync(account, output, upload, token); }
                catch (PublishAuthException)
                {
                    // Remember which account to re-authorize once the job reports back.
                    _publishNeedsSignIn = account.Id;
                    CarryTokensBack(account);
                    throw;
                }
                results.Add(new PublishSummaryLine(PublishAccount.PlatformName(account.Platform), result.Message, result.Url));
                // Keep any refreshed token from the copy that did the work.
                CarryTokensBack(account);
            }
            progress.Report(new(1, "Published"));
            _publishSummaries[output] = results;
        },
        job => ShowPublishSummary(job),
        scratch);
    }

    private readonly Dictionary<string, List<PublishSummaryLine>> _publishSummaries = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _publishNeedsSignIn;

    /// <summary>
    /// When a publish stops because the platform would not accept the stored tokens,
    /// offer a new sign-in rather than reporting it as a failed render.
    /// </summary>
    private bool HandlePublishAuthFailure(ExportJob job)
    {
        if (_publishNeedsSignIn is not { } id) return false;
        _publishNeedsSignIn = null;
        if (PublishAccounts.FirstOrDefault(account => account.Id == id) is not { } account) return false;
        var platform = PublishAccount.PlatformName(account.Platform);
        var answer = MessageBox.Show(this,
            $"{platform} would not accept the saved sign-in.\n\n{job.Error}\n\nSign in again now?",
            "Publish", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) _ = SignInToAccountAsync(account);
        return true;
    }

    private void CarryTokensBack(PublishAccount copy)
    {
        if (PublishAccounts.FirstOrDefault(account => account.Id == copy.Id) is not { } stored) return;
        stored.AccessToken = copy.AccessToken; stored.RefreshToken = copy.RefreshToken;
        stored.ExpiresAtUtc = copy.ExpiresAtUtc;
        if (!string.IsNullOrWhiteSpace(copy.DisplayName)) stored.DisplayName = copy.DisplayName;
        SavePublishAccounts();
    }

    private void ShowPublishSummary(ExportJob job)
    {
        if (job.OutputPath is null || !_publishSummaries.Remove(job.OutputPath, out var summary)) return;
        new PublishSummaryWindow(summary, job.OutputPath, this).ShowDialog();
    }
}
