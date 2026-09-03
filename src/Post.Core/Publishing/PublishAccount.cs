namespace Post.Core.Publishing;

// Keep the numeric values stable because the stored account file records them.
public enum PublishPlatform { YouTube = 0, TikTok = 1 }

public enum PublishPrivacy { Private = 0, Unlisted = 1, Public = 2 }

/// <summary>
/// One connected destination. Tokens are only ever held here in memory; the store
/// encrypts them before they touch the disk.
/// </summary>
public sealed class PublishAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PublishPlatform Platform { get; init; }

    /// <summary>The signed-in account's name, filled in after authorizing.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Ticked when this account should receive the next publish.</summary>
    public bool IsSelected { get; set; } = true;

    // ---- credentials --------------------------------------------------------
    // The app identifies itself with a client id; the user authorizes it with OAuth.
    // Both are optional here so an account can exist before it has been signed in.
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(AccessToken);
    /// <summary>True when the stored token is past its life and needs a refresh or a new sign-in.</summary>
    public bool IsExpired => ExpiresAtUtc is { } expiry && DateTime.UtcNow >= expiry.AddMinutes(-2);

    // ---- what gets posted ---------------------------------------------------
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Comma-separated on the way in; the provider splits them.</summary>
    public string Tags { get; set; } = "";
    public PublishPrivacy Privacy { get; set; } = PublishPrivacy.Private;
    public string? ThumbnailPath { get; set; }

    // YouTube
    public string CategoryId { get; set; } = "22";
    public bool MadeForKids { get; set; }
    public bool NotifySubscribers { get; set; } = true;

    // TikTok
    public bool AllowComments { get; set; } = true;
    public bool AllowDuet { get; set; } = true;
    public bool AllowStitch { get; set; } = true;
    /// <summary>Required by TikTok when the post promotes a brand or product.</summary>
    public bool DisclosesCommercialContent { get; set; }
    public bool BrandedContent { get; set; }

    public PublishAccount Clone() => (PublishAccount)MemberwiseClone();

    public static string PlatformName(PublishPlatform platform)
        => platform == PublishPlatform.YouTube ? "YouTube" : "TikTok";

    /// <summary>What the tab header shows: the account when known, the platform otherwise.</summary>
    public string TabHeader => string.IsNullOrWhiteSpace(DisplayName) ? PlatformName(Platform) : $"{PlatformName(Platform)} · {DisplayName}";
}
