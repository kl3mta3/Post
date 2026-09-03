using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Post.Core.Publishing;

/// <summary>
/// YouTube Data API v3. Sign-in is the installed-app OAuth flow with PKCE against a
/// loopback redirect; the upload is a resumable session, which is what lets a large
/// render report progress and survive a stalled chunk.
/// </summary>
public sealed class YouTubePublisher(HttpClient? client = null) : IPublishTarget
{
    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ChannelsEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos";
    private const string ThumbnailEndpoint = "https://www.googleapis.com/upload/youtube/v3/thumbnails/set";
    private const string Scopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>8 MB keeps progress moving without paying a round trip per megabyte.</summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Overridable so tests can point the flow at a local stub.</summary>
    public string AuthorizeUrl { get; init; } = AuthorizeEndpoint;
    public string TokenUrl { get; init; } = TokenEndpoint;
    public string ChannelsUrl { get; init; } = ChannelsEndpoint;
    public string UploadUrl { get; init; } = UploadEndpoint;
    public string ThumbnailUrl { get; init; } = ThumbnailEndpoint;

    public PublishPlatform Platform => PublishPlatform.YouTube;

    public string BuildAuthorizeUrl(PublishAccount account, string redirectUri, PkcePair pkce, string state)
        => $"{AuthorizeUrl}?client_id={OAuthBroker.Escape(account.ClientId ?? "")}" +
           $"&redirect_uri={OAuthBroker.Escape(redirectUri)}" +
           "&response_type=code" +
           $"&scope={OAuthBroker.Escape(Scopes)}" +
           "&access_type=offline&prompt=consent&include_granted_scopes=true" +
           $"&code_challenge={pkce.ChallengeBase64Url}&code_challenge_method=S256" +
           $"&state={state}";

    public async Task SignInAsync(PublishAccount account, OAuthBroker broker, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.ClientId)) throw new PublishAuthException("Add the Google client ID first.");
        var pkce = PkcePair.Create();
        var state = OAuthBroker.NewState();
        string redirectUri = "";
        var code = await broker.AuthorizeAsync(redirect => { redirectUri = redirect; return BuildAuthorizeUrl(account, redirect, pkce, state); }, state, token);

        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = account.ClientId!,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = pkce.Verifier,
        };
        // Installed apps still send the secret Google issues, though it is not confidential.
        if (!string.IsNullOrWhiteSpace(account.ClientSecret)) form["client_secret"] = account.ClientSecret!;

        using var response = await _client.PostAsync(TokenUrl, new FormUrlEncodedContent(form), token);
        var json = await ReadJsonAsync(response, "Google refused the sign-in", token);
        ApplyTokens(account, json);
        account.DisplayName = await FetchChannelNameAsync(account, token) ?? account.DisplayName;
    }

    public async Task RefreshAsync(PublishAccount account, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) throw new PublishAuthException("This YouTube account needs to be signed in again.");
        var form = new Dictionary<string, string>
        {
            ["refresh_token"] = account.RefreshToken!,
            ["client_id"] = account.ClientId ?? "",
            ["grant_type"] = "refresh_token",
        };
        if (!string.IsNullOrWhiteSpace(account.ClientSecret)) form["client_secret"] = account.ClientSecret!;

        using var response = await _client.PostAsync(TokenUrl, new FormUrlEncodedContent(form), token);
        if (!response.IsSuccessStatusCode) throw new PublishAuthException("The saved YouTube sign-in has expired. Sign in again.");
        ApplyTokens(account, JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement);
    }

    public async Task<PublishResult> UploadAsync(PublishAccount account, string filePath, IProgress<PublishProgress>? progress, CancellationToken token = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new PublishException($"The rendered file is missing: {filePath}");

        progress?.Report(new(0, "Starting the YouTube upload"));
        var metadata = new
        {
            snippet = new
            {
                title = string.IsNullOrWhiteSpace(account.Title) ? Path.GetFileNameWithoutExtension(filePath) : account.Title,
                description = account.Description ?? "",
                tags = SplitTags(account.Tags),
                categoryId = string.IsNullOrWhiteSpace(account.CategoryId) ? "22" : account.CategoryId,
            },
            status = new
            {
                privacyStatus = account.Privacy switch { PublishPrivacy.Public => "public", PublishPrivacy.Unlisted => "unlisted", _ => "private" },
                selfDeclaredMadeForKids = account.MadeForKids,
            },
        };

        var start = $"{UploadUrl}?uploadType=resumable&part=snippet,status&notifySubscribers={(account.NotifySubscribers ? "true" : "false")}";
        using var request = new HttpRequestMessage(HttpMethod.Post, start) { Content = JsonContent.Create(metadata) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Length.ToString());
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/*");

        using var session = await _client.SendAsync(request, token);
        if (session.StatusCode is HttpStatusCode.Unauthorized) throw new PublishAuthException("YouTube rejected the saved sign-in.");
        if (!session.IsSuccessStatusCode)
            throw new PublishException($"YouTube would not start the upload ({(int)session.StatusCode}). {await Detail(session, token)}");
        var uploadUri = session.Headers.Location?.ToString()
            ?? throw new PublishException("YouTube did not return an upload address.");

        var id = await SendChunksAsync(uploadUri, file, account.AccessToken!, progress, "Uploading to YouTube", token);

        string? thumbnail = null;
        if (!string.IsNullOrWhiteSpace(account.ThumbnailPath))
        {
            progress?.Report(new(.97, "Setting the thumbnail"));
            thumbnail = File.Exists(account.ThumbnailPath)
                ? await SetThumbnailAsync(account, id, token)
                : "the image is no longer at that path";
        }

        progress?.Report(new(1, "Published to YouTube"));
        var message = $"Uploaded as {metadata.snippet.title}"
            + (thumbnail is null ? "" : $"\n\nThe thumbnail was not set: {thumbnail}");
        return new PublishResult(id, $"https://www.youtube.com/watch?v={id}", message);
    }

    /// <summary>
    /// Sends the file in chunks. Google answers 308 while it wants more, and finishes
    /// with the video resource, so the loop is driven by the status code.
    /// </summary>
    private async Task<string> SendChunksAsync(string uploadUri, FileInfo file, string accessToken, IProgress<PublishProgress>? progress, string stage, CancellationToken token)
    {
        await using var stream = file.OpenRead();
        var buffer = new byte[ChunkSize];
        long position = 0;
        while (position < file.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, file.Length - position)), token);
            if (read <= 0) break;

            using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = new ByteArrayContent(buffer, 0, read) };
            chunk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            chunk.Content.Headers.ContentLength = read;
            chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(position, position + read - 1, file.Length);

            using var response = await _client.SendAsync(chunk, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized) throw new PublishAuthException("YouTube rejected the saved sign-in mid-upload.");
            if ((int)response.StatusCode == 308)
            {
                // Resume from wherever Google says it got to, which may trail our position.
                var range = response.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null;
                position = range is not null && range.StartsWith("bytes=0-") && long.TryParse(range[8..], out var last) ? last + 1 : position + read;
                stream.Position = position;
                progress?.Report(new(Math.Clamp(position / (double)file.Length * .95, 0, .95), stage));
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new PublishException($"YouTube rejected a chunk ({(int)response.StatusCode}). {await Detail(response, token)}");

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
            return body.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }
        throw new PublishException("The upload ended before YouTube confirmed the video.");
    }

    /// <summary>
    /// Sets the custom thumbnail, returning null when it worked and otherwise why it
    /// did not. A refused thumbnail must not fail a video that uploaded fine, but it
    /// must not disappear silently either.
    /// </summary>
    private async Task<string?> SetThumbnailAsync(PublishAccount account, string videoId, CancellationToken token)
    {
        var path = account.ThumbnailPath!;
        var file = new FileInfo(path);
        const long limit = 2 * 1024 * 1024;
        if (file.Length > limit) return $"the image is {file.Length / 1024d / 1024:0.#} MB and YouTube's limit is 2 MB";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) return $"{extension} is not a format YouTube accepts, so use a PNG or a JPEG";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ThumbnailUrl}?videoId={videoId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Content = new ByteArrayContent(await File.ReadAllBytesAsync(path, token));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(extension == ".png" ? "image/png" : "image/jpeg");

        using var response = await _client.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return null;
        var detail = Trim(await response.Content.ReadAsStringAsync(token));
        // A 403 here is almost always a channel that has not been verified for custom
        // thumbnails, which the response body does not say plainly.
        return (int)response.StatusCode == 403
            ? $"YouTube refused it. Custom thumbnails need a channel verified at youtube.com/verify. {detail}"
            : $"YouTube refused it ({(int)response.StatusCode}). {detail}";
    }

    private async Task<string?> FetchChannelNameAsync(PublishAccount account, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ChannelsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var response = await _client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement;
            return json.TryGetProperty("items", out var items) && items.GetArrayLength() > 0
                && items[0].TryGetProperty("snippet", out var snippet) && snippet.TryGetProperty("title", out var title)
                ? title.GetString()
                : null;
        }
        catch { return null; }
    }

    private static void ApplyTokens(PublishAccount account, JsonElement json)
    {
        account.AccessToken = json.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (json.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } value) account.RefreshToken = value;
        account.ExpiresAtUtc = json.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? DateTime.UtcNow.AddSeconds(seconds)
            : DateTime.UtcNow.AddHours(1);
        if (string.IsNullOrWhiteSpace(account.AccessToken)) throw new PublishAuthException("Google returned no access token.");
    }

    private static string[] SplitTags(string tags)
        => (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string failure, CancellationToken token)
    {
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new PublishAuthException($"{failure} ({(int)response.StatusCode}). {Trim(body)}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Detail(HttpResponseMessage response, CancellationToken token)
        => Trim(await response.Content.ReadAsStringAsync(token));

    private static string Trim(string body)
    {
        body = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return body.Length <= 300 ? body : body[..300] + "…";
    }
}
