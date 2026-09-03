namespace Post.Core.Publishing;

/// <summary>
/// Routes an account to the right platform implementation, and keeps the access token
/// fresh so an upload is not started with a token that is about to lapse.
/// </summary>
public sealed class PublishService(OAuthBroker broker, HttpClient? client = null)
{
    private readonly Dictionary<PublishPlatform, IPublishTarget> _targets = new()
    {
        [PublishPlatform.YouTube] = new YouTubePublisher(client),
        [PublishPlatform.TikTok] = new TikTokPublisher(client),
    };

    public OAuthBroker Broker { get; } = broker;

    public bool IsSupported(PublishPlatform platform) => _targets.ContainsKey(platform);

    public IPublishTarget TargetFor(PublishAccount account)
        => _targets.TryGetValue(account.Platform, out var target)
            ? target
            : throw new PublishException($"Publishing to {PublishAccount.PlatformName(account.Platform)} is not connected yet.");

    public Task SignInAsync(PublishAccount account, CancellationToken token = default)
        => TargetFor(account).SignInAsync(account, Broker, token);

    /// <summary>
    /// Uploads one account's copy, renewing the token first when it has expired. A
    /// refresh that fails surfaces as <see cref="PublishAuthException"/> so the caller
    /// can ask for a new sign-in.
    /// </summary>
    public async Task<PublishResult> PublishAsync(PublishAccount account, string filePath, IProgress<PublishProgress>? progress, CancellationToken token = default)
    {
        var target = TargetFor(account);
        if (!account.IsSignedIn) throw new PublishAuthException($"Sign in to {PublishAccount.PlatformName(account.Platform)} first.");
        if (account.IsExpired)
        {
            progress?.Report(new(0, $"Renewing the {PublishAccount.PlatformName(account.Platform)} sign-in"));
            await target.RefreshAsync(account, token);
        }

        try { return await target.UploadAsync(account, filePath, progress, token); }
        catch (PublishAuthException) when (!string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            // A token can lapse between the check and the upload; try once more.
            await target.RefreshAsync(account, token);
            return await target.UploadAsync(account, filePath, progress, token);
        }
    }
}
