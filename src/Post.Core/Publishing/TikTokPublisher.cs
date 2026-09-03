using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Post.Core.Publishing;

/// <summary>
/// TikTok Content Posting API, Direct Post. PKCE is mandatory for desktop clients and
/// TikTok expects the challenge hex encoded rather than base64url. Posting is a three
/// step dance: ask what the creator is allowed to do, initialise the post, then send
/// the file to the address it hands back.
/// </summary>
public sealed class TikTokPublisher(HttpClient? client = null) : IPublishTarget
{
    private const string AuthorizeEndpoint = "https://www.tiktok.com/v2/auth/authorize/";
    private const string TokenEndpoint = "https://open.tiktokapis.com/v2/oauth/token/";
    private const string UserEndpoint = "https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name";
    private const string CreatorEndpoint = "https://open.tiktokapis.com/v2/post/publish/creator_info/query/";
    private const string InitEndpoint = "https://open.tiktokapis.com/v2/post/publish/video/init/";
    private const string StatusEndpoint = "https://open.tiktokapis.com/v2/post/publish/status/fetch/";
    private const string Scopes = "user.info.basic,video.upload,video.publish";

    // TikTok accepts one chunk up to 64 MB; past that every chunk must be at least
    // 5 MB, so 32 MB chunks keep the final one comfortably inside the limits.
    private const long SingleChunkLimit = 64L * 1024 * 1024;
    private const long ChunkSize = 32L * 1024 * 1024;

    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Overridable so tests can point the flow at a local stub.</summary>
    public string AuthorizeUrl { get; init; } = AuthorizeEndpoint;
    public string TokenUrl { get; init; } = TokenEndpoint;
    public string UserUrl { get; init; } = UserEndpoint;
    public string CreatorUrl { get; init; } = CreatorEndpoint;
    public string InitUrl { get; init; } = InitEndpoint;
    public string StatusUrl { get; init; } = StatusEndpoint;

    public PublishPlatform Platform => PublishPlatform.TikTok;

    public string BuildAuthorizeUrl(PublishAccount account, string redirectUri, PkcePair pkce, string state)
        => $"{AuthorizeUrl}?client_key={OAuthBroker.Escape(account.ClientId ?? "")}" +
           $"&scope={OAuthBroker.Escape(Scopes)}" +
           "&response_type=code" +
           $"&redirect_uri={OAuthBroker.Escape(redirectUri)}" +
           $"&state={state}" +
           $"&code_challenge={pkce.ChallengeHex}&code_challenge_method=S256";

    public async Task SignInAsync(PublishAccount account, OAuthBroker broker, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.ClientId)) throw new PublishAuthException("Add the TikTok client key first.");
        var pkce = PkcePair.Create();
        var state = OAuthBroker.NewState();
        string redirectUri = "";
        var code = await broker.AuthorizeAsync(redirect => { redirectUri = redirect; return BuildAuthorizeUrl(account, redirect, pkce, state); }, state, token);

        var form = new Dictionary<string, string>
        {
            ["client_key"] = account.ClientId!,
            ["client_secret"] = account.ClientSecret ?? "",
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = pkce.Verifier,
        };
        using var response = await _client.PostAsync(TokenUrl, new FormUrlEncodedContent(form), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new PublishAuthException($"TikTok refused the sign-in ({(int)response.StatusCode}). {Trim(body)}");
        ApplyTokens(account, JsonDocument.Parse(body).RootElement);
        account.DisplayName = await FetchDisplayNameAsync(account, token) ?? account.DisplayName;
    }

    public async Task RefreshAsync(PublishAccount account, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) throw new PublishAuthException("This TikTok account needs to be signed in again.");
        var form = new Dictionary<string, string>
        {
            ["client_key"] = account.ClientId ?? "",
            ["client_secret"] = account.ClientSecret ?? "",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken!,
        };
        using var response = await _client.PostAsync(TokenUrl, new FormUrlEncodedContent(form), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new PublishAuthException("The saved TikTok sign-in has expired. Sign in again.");
        ApplyTokens(account, JsonDocument.Parse(body).RootElement);
    }

    public async Task<PublishResult> UploadAsync(PublishAccount account, string filePath, IProgress<PublishProgress>? progress, CancellationToken token = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new PublishException($"The rendered file is missing: {filePath}");

        progress?.Report(new(0, "Checking what TikTok allows for this account"));
        var allowed = await FetchPrivacyOptionsAsync(account, token);
        var privacy = ChoosePrivacy(account.Privacy, allowed);

        var chunkSize = file.Length <= SingleChunkLimit ? file.Length : ChunkSize;
        var chunkCount = file.Length <= SingleChunkLimit ? 1 : (int)(file.Length / chunkSize);

        var payload = new
        {
            post_info = new
            {
                title = string.IsNullOrWhiteSpace(account.Description) ? account.Title ?? "" : account.Description,
                privacy_level = privacy,
                disable_comment = !account.AllowComments,
                disable_duet = !account.AllowDuet,
                disable_stitch = !account.AllowStitch,
                brand_content_toggle = account.BrandedContent,
                brand_organic_toggle = account.DisclosesCommercialContent && !account.BrandedContent,
            },
            source_info = new
            {
                source = "FILE_UPLOAD",
                video_size = file.Length,
                chunk_size = chunkSize,
                total_chunk_count = chunkCount,
            },
        };

        progress?.Report(new(.05, "Starting the TikTok post"));
        using var request = new HttpRequestMessage(HttpMethod.Post, InitUrl) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var response = await _client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (response.StatusCode is HttpStatusCode.Unauthorized) throw new PublishAuthException("TikTok rejected the saved sign-in.");
        if (!response.IsSuccessStatusCode) throw new PublishException($"TikTok would not start the post ({(int)response.StatusCode}). {Trim(body)}");

        var json = JsonDocument.Parse(body).RootElement;
        ThrowOnError(json, "TikTok would not start the post");
        var data = json.GetProperty("data");
        var publishId = data.TryGetProperty("publish_id", out var id) ? id.GetString() ?? "" : "";
        var uploadUrl = data.TryGetProperty("upload_url", out var url) ? url.GetString() : null;
        if (string.IsNullOrWhiteSpace(uploadUrl)) throw new PublishException("TikTok did not return an upload address.");

        await SendChunksAsync(uploadUrl!, file, chunkSize, chunkCount, progress, token);

        progress?.Report(new(.97, "TikTok is processing the video"));
        var status = await FetchStatusAsync(account, publishId, token);
        progress?.Report(new(1, "Sent to TikTok"));
        return new PublishResult(publishId, null,
            $"Uploaded as {privacy.Replace('_', ' ').ToLowerInvariant()}{(status is null ? "" : $"; TikTok reports {status.ToLowerInvariant()}")}");
    }

    /// <summary>Sends each chunk as a byte range; the last one absorbs any remainder.</summary>
    private async Task SendChunksAsync(string uploadUrl, FileInfo file, long chunkSize, int chunkCount, IProgress<PublishProgress>? progress, CancellationToken token)
    {
        await using var stream = file.OpenRead();
        for (var index = 0; index < chunkCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var start = index * chunkSize;
            var length = index == chunkCount - 1 ? file.Length - start : chunkSize;
            var buffer = new byte[length];
            stream.Position = start;
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token);

            using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, read) };
            chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            chunk.Content.Headers.ContentLength = read;
            chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + read - 1, file.Length);

            using var response = await _client.SendAsync(chunk, token);
            if (!response.IsSuccessStatusCode)
                throw new PublishException($"TikTok rejected a chunk ({(int)response.StatusCode}). {Trim(await response.Content.ReadAsStringAsync(token))}");
            progress?.Report(new(Math.Clamp(.05 + (start + read) / (double)file.Length * .9, 0, .95), "Uploading to TikTok"));
        }
    }

    /// <summary>
    /// TikTok only accepts a privacy level the creator is actually allowed to use, and
    /// an unaudited client is limited to SELF_ONLY, so the request is clamped to what
    /// the account reports rather than failing.
    /// </summary>
    private static string ChoosePrivacy(PublishPrivacy privacy, IReadOnlyList<string> allowed)
    {
        var wanted = privacy switch
        {
            PublishPrivacy.Public => "PUBLIC_TO_EVERYONE",
            PublishPrivacy.Unlisted => "FOLLOWER_OF_CREATOR",
            _ => "SELF_ONLY",
        };
        if (allowed.Count == 0 || allowed.Contains(wanted)) return wanted;
        foreach (var fallback in new[] { "SELF_ONLY", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "PUBLIC_TO_EVERYONE" })
            if (allowed.Contains(fallback)) return fallback;
        return "SELF_ONLY";
    }

    private async Task<IReadOnlyList<string>> FetchPrivacyOptionsAsync(PublishAccount account, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CreatorUrl) { Content = JsonContent.Create(new { }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var response = await _client.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized) throw new PublishAuthException("TikTok rejected the saved sign-in.");
            if (!response.IsSuccessStatusCode) return [];
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
            if (!json.TryGetProperty("data", out var data) || !data.TryGetProperty("privacy_level_options", out var options)) return [];
            return options.EnumerateArray().Select(item => item.GetString() ?? "").Where(value => value.Length > 0).ToArray();
        }
        catch (PublishAuthException) { throw; }
        catch { return []; }
    }

    private async Task<string?> FetchStatusAsync(PublishAccount account, string publishId, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, StatusUrl) { Content = JsonContent.Create(new { publish_id = publishId }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var response = await _client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
            return json.TryGetProperty("data", out var data) && data.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch { return null; }
    }

    private async Task<string?> FetchDisplayNameAsync(PublishAccount account, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var response = await _client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
            return json.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user)
                && user.TryGetProperty("display_name", out var name) ? name.GetString() : null;
        }
        catch { return null; }
    }

    private static void ThrowOnError(JsonElement json, string prefix)
    {
        if (!json.TryGetProperty("error", out var error)) return;
        var code = error.TryGetProperty("code", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(code) || code == "ok") return;
        var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
        if (code.Contains("token", StringComparison.OrdinalIgnoreCase)) throw new PublishAuthException($"{prefix}: {message ?? code}");
        throw new PublishException($"{prefix}: {message ?? code}");
    }

    private static void ApplyTokens(PublishAccount account, JsonElement json)
    {
        // Errors come back with a 200 and an error object, so check before reading.
        ThrowOnError(json, "TikTok refused the sign-in");
        account.AccessToken = json.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (json.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } value) account.RefreshToken = value;
        account.ExpiresAtUtc = json.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? DateTime.UtcNow.AddSeconds(seconds)
            : DateTime.UtcNow.AddHours(24);
        if (string.IsNullOrWhiteSpace(account.AccessToken)) throw new PublishAuthException("TikTok returned no access token.");
    }

    private static string Trim(string body)
    {
        body = (body ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return body.Length <= 300 ? body : body[..300] + "…";
    }
}
