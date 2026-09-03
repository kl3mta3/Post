namespace Post.Core.Publishing;

/// <summary>What a completed upload reports back.</summary>
public sealed record PublishResult(string Id, string? Url, string Message);

/// <summary>Progress of one upload, 0–1 with a line describing the stage.</summary>
public sealed record PublishProgress(double Fraction, string Stage);

/// <summary>
/// One platform's sign-in and upload. Implementations hold no state: the account
/// carries the credentials and tokens, and is updated in place after a sign-in or a
/// refresh so the caller can persist it.
/// </summary>
public interface IPublishTarget
{
    PublishPlatform Platform { get; }

    /// <summary>Runs the browser sign-in and writes the tokens onto the account.</summary>
    Task SignInAsync(PublishAccount account, OAuthBroker broker, CancellationToken token = default);

    /// <summary>
    /// Renews an expired access token from the stored refresh token. Throws
    /// <see cref="PublishAuthException"/> when the account must sign in again.
    /// </summary>
    Task RefreshAsync(PublishAccount account, CancellationToken token = default);

    /// <summary>Uploads a rendered file and returns where it landed.</summary>
    Task<PublishResult> UploadAsync(PublishAccount account, string filePath, IProgress<PublishProgress>? progress, CancellationToken token = default);
}
