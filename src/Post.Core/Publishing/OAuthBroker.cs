using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Post.Core.Publishing;

/// <summary>A PKCE pair. TikTok wants the challenge hex encoded; Google wants base64url.</summary>
public sealed record PkcePair(string Verifier, string ChallengeBase64Url, string ChallengeHex)
{
    public static PkcePair Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        var verifier = Base64Url(bytes);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return new PkcePair(verifier, Base64Url(hash), Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Raised when a platform will not accept the stored tokens any more.</summary>
public sealed class PublishAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Raised when a platform rejects the upload itself.</summary>
public sealed class PublishException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs the browser half of an OAuth authorization code flow: opens the platform's
/// consent page in the user's browser and catches the redirect on a loopback listener.
/// A desktop app cannot keep a secret, so every flow here uses PKCE.
/// </summary>
public sealed class OAuthBroker(Action<string>? launcher = null)
{
    /// <summary>How the consent page is opened. Replaceable so tests need no browser.</summary>
    private readonly Action<string> _launcher = launcher ?? OpenBrowser;

    /// <summary>
    /// What to register with the platform. Google accepts any port on the loopback
    /// address and TikTok accepts a wildcard, which is what Post needs: Windows reserves
    /// large, machine-specific port ranges, so a fixed port cannot be relied on.
    /// </summary>
    public static string RedirectDisplay(PublishPlatform platform) => platform switch
    {
        PublishPlatform.TikTok => "http://127.0.0.1:*/",
        _ => "http://127.0.0.1",
    };

    /// <summary>
    /// Claims a free loopback port, hands the resulting redirect URI to
    /// <paramref name="buildAuthorizeUrl"/>, opens that page and waits for the code.
    /// </summary>
    public async Task<string> AuthorizeAsync(Func<string, string> buildAuthorizeUrl, string expectedState, CancellationToken token = default)
    {
        using var listener = StartListener(out var redirectUri);
        _launcher(buildAuthorizeUrl(redirectUri));

        try
        {
            // GetContextAsync has no cancellation, so race it against the token.
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, token));
            if (completed != contextTask) { token.ThrowIfCancellationRequested(); }
            var context = await contextTask;

            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var error = query["error"] ?? query["error_description"];

            await RespondAsync(context, error is null
                ? "Post is signed in. You can close this tab and go back to the app."
                : $"Sign-in failed: {WebUtility.HtmlEncode(error)}");

            if (error is not null) throw new PublishAuthException($"The platform refused the sign-in: {error}");
            if (string.IsNullOrWhiteSpace(code)) throw new PublishAuthException("The platform redirected back without an authorization code.");
            if (!string.IsNullOrEmpty(expectedState) && state != expectedState)
                throw new PublishAuthException("The sign-in response did not match the request, so it was discarded.");
            return code;
        }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>
    /// Binds a loopback port that http.sys will actually accept. Ports are requested
    /// from the OS rather than hardcoded, because Windows excludes whole ranges and the
    /// exclusions move between machines and reboots.
    /// </summary>
    private static HttpListener StartListener(out string redirectUri)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            int port;
            try { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { try { probe.Stop(); } catch { } }

            var listener = new HttpListener();
            var prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); redirectUri = prefix; return listener; }
            catch (HttpListenerException) { try { listener.Close(); } catch { } }
        }
        throw new PublishAuthException("Post could not open a local port to receive the sign-in. Check whether a firewall is blocking loopback connections.");
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var page = $"""
            <!doctype html><html><head><meta charset="utf-8"><title>Post</title></head>
            <body style="font-family:Segoe UI,sans-serif;background:#0b1424;color:#f3f7ff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0">
            <div style="text-align:center"><h2 style="margin:0 0 8px">Post</h2><p style="color:#a9bcd6">{message}</p></div>
            </body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(page);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) { throw new PublishAuthException("Post could not open your browser for sign-in.", exception); }
    }

    public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Percent-encodes a query value.</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);
}
