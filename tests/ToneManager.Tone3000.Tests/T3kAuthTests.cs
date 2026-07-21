using System.Net;
using System.Text;
using System.Text.Json;
using ToneManager.Tone3000;

public class T3kAuthTests : IDisposable
{
    private readonly string _tok = Path.Combine(Path.GetTempPath(), $"t3kauth-{Guid.NewGuid():N}.token");
    public void Dispose() { if (File.Exists(_tok)) File.Delete(_tok); }

    /// <summary>Fake token endpoint: records the request body, returns a canned grant.</summary>
    private sealed class FakeTokenHandler : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Bodies { get; } = new();
        public int ExpiresIn { get; set; } = 3600;
        public bool Fail { get; set; }
        public HttpStatusCode FailStatus { get; set; } = HttpStatusCode.Unauthorized;
        public bool ThrowNetworkError { get; set; }
        public bool DelayResponse { get; set; }
        public bool NullRefreshToken { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (ThrowNetworkError) throw new HttpRequestException("simulated network failure");
            Assert.Equal("/api/v1/oauth/token", req.RequestUri!.AbsolutePath);
            var form = (await req.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(kv => kv.Split('=', 2))
                .ToDictionary(a => Uri.UnescapeDataString(a[0]), a => Uri.UnescapeDataString(a[1]));
            Bodies.Add(form);
            if (DelayResponse) await Task.Delay(50, ct);
            if (Fail) return new HttpResponseMessage(FailStatus) { Content = new StringContent("{}") };
            var n = Bodies.Count;
            object grant = NullRefreshToken
                ? new { access_token = $"at_{n}", refresh_token = (string?)null, expires_in = ExpiresIn, token_type = "Bearer" }
                : new { access_token = $"at_{n}", refresh_token = (string?)$"rt_{n}", expires_in = ExpiresIn, token_type = "Bearer" };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(grant), Encoding.UTF8, "application/json")
            };
        }
    }

    private static T3kConfig Cfg => new("t3k_pub_test", RedirectPort: 0);

    /// <summary>"Browser" that immediately hits the loopback redirect like a signed-in user.
    /// Parses redirect_uri and state from the authorize URL the auth flow built.</summary>
    private static void FakeBrowser(string authorizeUrl)
    {
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query);
        Assert.Equal("t3k_pub_test", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.NotNull(q["code_challenge"]);
        var redirect = q["redirect_uri"]!;
        _ = Task.Run(async () =>
        {
            using var c = new HttpClient();
            await c.GetAsync($"{redirect}?code=authcode123&state={Uri.EscapeDataString(q["state"]!)}");
        });
    }

    [Fact]
    public async Task SignIn_exchanges_the_code_with_pkce_and_persists_the_refresh_token()
    {
        var handler = new FakeTokenHandler();
        var store = new T3kTokenStore(_tok);
        string? capturedChallenge = null;
        var auth = new T3kAuth(Cfg, store, handler, url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal("t3k_pub_test", q["client_id"]);
            Assert.Equal("code", q["response_type"]);
            Assert.Equal("S256", q["code_challenge_method"]);
            Assert.NotNull(q["code_challenge"]);
            capturedChallenge = q["code_challenge"];
            var redirect = q["redirect_uri"]!;
            _ = Task.Run(async () =>
            {
                using var c = new HttpClient();
                await c.GetAsync($"{redirect}?code=authcode123&state={Uri.EscapeDataString(q["state"]!)}");
            });
        });

        await auth.SignInAsync();

        Assert.True(auth.IsSignedIn);
        var body = handler.Bodies.Single();
        Assert.Equal("authorization_code", body["grant_type"]);
        Assert.Equal("authcode123", body["code"]);
        Assert.Equal("t3k_pub_test", body["client_id"]);
        Assert.False(string.IsNullOrEmpty(body["code_verifier"]));
        Assert.StartsWith("http://127.0.0.1:", body["redirect_uri"]);
        Assert.Equal("rt_1", store.Load());                  // persisted for next launch
        Assert.Equal("at_1", await auth.GetAccessTokenAsync());

        // Cross-check: the challenge sent to /authorize must be S256(verifier sent to /token).
        var expectedChallenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(body["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expectedChallenge, capturedChallenge);
    }

    [Fact]
    public async Task Wrong_state_is_rejected()
    {
        var handler = new FakeTokenHandler();
        var auth = new T3kAuth(Cfg, new T3kTokenStore(_tok), handler, url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            var redirect = q["redirect_uri"]!;
            _ = Task.Run(async () =>
            {
                using var c = new HttpClient();
                await c.GetAsync($"{redirect}?code=evil&state=WRONG");
            });
        });
        var ex = await Assert.ThrowsAsync<T3kException>(() => auth.SignInAsync());
        Assert.Equal(T3kError.Auth, ex.Kind);
        Assert.Empty(handler.Bodies);                        // never exchanged the code
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_with_the_stored_refresh_token()
    {
        var handler = new FakeTokenHandler { ExpiresIn = 1 };    // expires ~immediately (60s skew)
        var store = new T3kTokenStore(_tok);
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);
        await auth.SignInAsync();                                // grant 1

        var token = await auth.GetAccessTokenAsync();            // must trigger refresh (grant 2)
        Assert.Equal("at_2", token);
        Assert.Equal("refresh_token", handler.Bodies[1]["grant_type"]);
        Assert.Equal("rt_1", handler.Bodies[1]["refresh_token"]);
        Assert.Equal("rt_2", store.Load());                      // rotated token persisted
    }

    [Fact]
    public async Task Restart_resumes_from_persisted_refresh_token()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_persisted");
        var handler = new FakeTokenHandler();
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);   // no SignInAsync call

        Assert.True(auth.IsSignedIn);                            // has a refresh token
        var token = await auth.GetAccessTokenAsync();
        Assert.Equal("at_1", token);
        Assert.Equal("rt_persisted", handler.Bodies.Single()["refresh_token"]);
    }

    [Fact]
    public async Task Failed_refresh_signs_out_and_throws_auth()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_dead");
        var auth = new T3kAuth(Cfg, store, new FakeTokenHandler { Fail = true }, FakeBrowser);
        var ex = await Assert.ThrowsAsync<T3kException>(() => auth.GetAccessTokenAsync());
        Assert.Equal(T3kError.Auth, ex.Kind);
        Assert.False(auth.IsSignedIn);
        Assert.Null(store.Load());                               // dead token cleared
    }

    [Fact]
    public void SignOut_clears_everything()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_x");
        var auth = new T3kAuth(Cfg, store, new FakeTokenHandler(), FakeBrowser);
        auth.SignOut();
        Assert.False(auth.IsSignedIn);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Network_failure_during_refresh_preserves_the_stored_token()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_keep");
        var handler = new FakeTokenHandler { ThrowNetworkError = true };
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);

        var ex = await Assert.ThrowsAsync<T3kException>(() => auth.GetAccessTokenAsync());

        Assert.Equal(T3kError.Network, ex.Kind);
        Assert.Equal("rt_keep", store.Load());               // NOT wiped by a transient failure
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public async Task Server_error_during_refresh_preserves_the_stored_token()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_keep");
        var handler = new FakeTokenHandler { Fail = true, FailStatus = HttpStatusCode.ServiceUnavailable };
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);

        var ex = await Assert.ThrowsAsync<T3kException>(() => auth.GetAccessTokenAsync());

        Assert.Equal(T3kError.Api, ex.Kind);                  // 503 is NOT an auth rejection
        Assert.Equal("rt_keep", store.Load());                // NOT wiped by a transient outage
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public async Task Concurrent_token_requests_refresh_only_once()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_start");
        var handler = new FakeTokenHandler { ExpiresIn = 3600, DelayResponse = true };
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);

        var results = await Task.WhenAll(auth.GetAccessTokenAsync(), auth.GetAccessTokenAsync());

        Assert.Equal("at_1", results[0]);
        Assert.Equal("at_1", results[1]);
        Assert.Single(handler.Bodies);                        // single-flight: only one refresh hit the wire
    }

    [Fact]
    public async Task Null_refresh_token_in_grant_keeps_the_existing_one()
    {
        var store = new T3kTokenStore(_tok);
        store.Save("rt_keep");
        var handler = new FakeTokenHandler { ExpiresIn = 3600, NullRefreshToken = true };
        var auth = new T3kAuth(Cfg, store, handler, FakeBrowser);

        var token = await auth.GetAccessTokenAsync();

        Assert.Equal("at_1", token);
        Assert.Equal("rt_keep", store.Load());                // null refresh_token must not overwrite it
    }
}
