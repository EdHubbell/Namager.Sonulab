using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Namager.Tone3000;

public interface IT3kAuth
{
    bool IsSignedIn { get; }
    string? Username { get; }
    Task SignInAsync(CancellationToken ct = default);
    void SignOut();
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>OAuth 2.0 + PKCE for a native app: system browser to /oauth/authorize, code
/// caught on a one-shot 127.0.0.1 HttpListener, exchanged at /oauth/token; access token
/// cached and proactively refreshed (60 s skew) from the DPAPI-stored refresh token.
/// Uses ONLY the publishable client_id — never the t3k_cs_ secret (spec §4).</summary>
public sealed class T3kAuth(
    T3kConfig config, T3kTokenStore store,
    HttpMessageHandler? handler = null, Action<string>? openBrowser = null,
    string? baseUrl = null) : IT3kAuth
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    { BaseAddress = new Uri(baseUrl ?? T3kConfig.DefaultBaseUrl) };
    private readonly Action<string> _openBrowser = openBrowser ?? (url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires = DateTimeOffset.MinValue;
    private string? _refreshToken = store.Load();

    public bool IsSignedIn => _refreshToken is not null;
    public string? Username { get; private set; }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        // PKCE material (RFC 7636): 32-byte verifier, S256 challenge, CSRF state.
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        int port = config.RedirectPort > 0 ? config.RedirectPort : GetFreePort();
        string redirect = $"http://127.0.0.1:{port}/callback";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException e)
        {
            throw new T3kException(
                $"Could not open the sign-in listener on port {port} — it may already be in use.",
                T3kError.Auth, e);
        }

        var authorizeUrl = $"{_http.BaseAddress!.ToString().TrimEnd('/')}/api/v1/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(config.PublishableKey)}&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}";
        _openBrowser(authorizeUrl);

        // Wait for the real callback, ignoring unrelated noise (port scans, browser
        // preflight requests) that arrive on the loopback listener without a `code`.
        // A request that DOES carry a code but the wrong state is a CSRF signal and
        // must fail the flow immediately rather than keep waiting.
        HttpListenerContext ctx;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                HttpListenerContext candidate;
                try { candidate = await listener.GetContextAsync().WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new T3kException("Sign-in timed out — the browser window was closed or never completed.", T3kError.Auth);
                }
                if (candidate.Request.QueryString["code"] is null)
                {
                    candidate.Response.StatusCode = 404;
                    candidate.Response.Close();
                    continue;
                }
                ctx = candidate;
                break;
            }
        }
        var q = ctx.Request.QueryString;
        string? code = q["code"];
        bool ok = code is not null && q["state"] == state;
        var page = ok ? "<html><body>Signed in — you can close this tab and return to ToneManager.</body></html>"
                      : "<html><body>Sign-in failed.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        ctx.Response.ContentType = "text/html";
        ctx.Response.StatusCode = ok ? 200 : 400;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
        if (!ok) throw new T3kException("Sign-in was rejected (state mismatch or no code) — try again.", T3kError.Auth);

        var grant = await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirect,
            ["client_id"] = config.PublishableKey,
            ["code_verifier"] = verifier,
        }, ct);
        ApplyGrant(grant);
    }

    public void SignOut()
    {
        _accessToken = null; _refreshToken = null; Username = null;
        _accessExpires = DateTimeOffset.MinValue;
        store.Clear();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpires - TimeSpan.FromSeconds(60))
            return _accessToken;
        if (_refreshToken is null)
            throw new T3kException("Not signed in to Tone3000.", T3kError.Auth);

        // Single-flight refresh: the stored refresh token is single-use/rotating, so two
        // concurrent callers near expiry must not both spend it. The loser re-checks the
        // (now fresh) access token after acquiring the lock instead of refreshing again.
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpires - TimeSpan.FromSeconds(60))
                return _accessToken;
            if (_refreshToken is null)
                throw new T3kException("Not signed in to Tone3000.", T3kError.Auth);

            Dictionary<string, string> grant;
            try
            {
                grant = await TokenRequestAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken,
                    ["client_id"] = config.PublishableKey,
                }, ct);
            }
            catch (T3kException e) when (e.Kind == T3kError.Auth)
            {
                SignOut();                                   // dead refresh token: honest sign-out
                throw new T3kException("Your Tone3000 session expired — please sign in again.", T3kError.Auth);
            }
            ApplyGrant(grant);
            return _accessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<Dictionary<string, string>> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.PostAsync("/api/v1/oauth/token", new FormUrlEncodedContent(form), ct); }
        catch (HttpRequestException e)
        { throw new T3kException("Could not reach tone3000.com — check your connection.", T3kError.Network, e); }
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // A transient token-endpoint outage (429/5xx) must NOT map to Auth: the refresh
            // caller (GetAccessTokenAsync) treats Kind==Auth as "dead refresh token" and signs
            // out, wiping a perfectly good stored refresh token over a blip. Only genuine
            // credential/grant rejections (4xx other than 429) are Auth.
            var kind = resp.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => T3kError.RateLimited,
                _ when (int)resp.StatusCode >= 500 => T3kError.Api,
                _ => T3kError.Auth,
            };
            throw new T3kException($"Tone3000 sign-in failed (HTTP {(int)resp.StatusCode}).", kind);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var d = new Dictionary<string, string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // Skip null (and any non-string/number) properties: a JSON null must NOT
                // become the literal string "null" via GetRawText() and get persisted —
                // e.g. a rotating refresh_token that comes back null means "unchanged".
                if (p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
                d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
            return d;
        }
        catch (JsonException e)
        { throw new T3kException("Tone3000 returned an unreadable sign-in response.", T3kError.Api, e); }
    }

    private void ApplyGrant(Dictionary<string, string> grant)
    {
        _accessToken = grant.GetValueOrDefault("access_token");
        if (_accessToken is null)
            throw new T3kException("Tone3000 sign-in response had no access token.", T3kError.Api);
        int expiresIn = int.TryParse(grant.GetValueOrDefault("expires_in"), out var e) ? e : 3600;
        _accessExpires = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        if (grant.GetValueOrDefault("refresh_token") is { } rt)
        { _refreshToken = rt; store.Save(rt); }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
