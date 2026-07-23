using System.Net;
using System.Text;
using Namager.Tone3000;

public class T3kClientTests
{
    private sealed class FakeAuth : IT3kAuth
    {
        public bool IsSignedIn => true;
        public string? Username => "ed";
        public Task SignInAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignOut() { }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("at_test");
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{}";
        public Exception? Throw { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new HttpResponseMessage(Status)
            { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private static (T3kClient client, CannedHandler h) Make()
    { var h = new CannedHandler(); return (new T3kClient(new FakeAuth(), h), h); }

    /// <summary>Returns each queued body in order, one per request (last one repeats if exhausted).</summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<HttpRequestMessage> Requests { get; } = new();
        public SequencedHandler(IEnumerable<string> bodies) => _bodies = new Queue<string>(bodies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req);
            var body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Search_builds_the_query_and_sends_the_bearer()
    {
        var (client, h) = Make();
        h.Body = """{ "data": [ { "id": 1, "title": "T" } ], "page": 2, "page_size": 20, "total": 40, "total_pages": 2 }""";
        var page = await client.SearchAsync("fender deluxe", "nam", page: 2);

        var req = h.Requests.Single();
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("at_test", req.Headers.Authorization.Parameter);
        Assert.Equal("/api/v1/tones/search", req.RequestUri!.AbsolutePath);
        Assert.Contains("query=fender%20deluxe", req.RequestUri.Query);
        Assert.Contains("format=nam", req.RequestUri.Query);
        Assert.Contains("page=2", req.RequestUri.Query);
        Assert.Equal(2, page.Page);
        Assert.Single(page.Data);
    }

    [Fact]
    public async Task Null_query_and_format_are_omitted()
    {
        var (client, h) = Make();
        h.Body = """{ "data": [], "page": 1, "page_size": 20, "total": 0, "total_pages": 0 }""";
        await client.SearchAsync(null, null, 1);
        Assert.DoesNotContain("query=", h.Requests.Single().RequestUri!.Query);
        Assert.DoesNotContain("format=", h.Requests.Single().RequestUri!.Query);
    }

    [Fact]
    public async Task Models_unwraps_the_page_envelope()
    {
        var (client, h) = Make();
        h.Body = """{ "data": [ { "id": 5, "name": "Clean", "format": "nam", "model_url": "https://x/m5" } ], "page": 1, "page_size": 20, "total": 1, "total_pages": 1 }""";
        var models = await client.GetModelsAsync(42);
        Assert.Equal("/api/v1/models", h.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Contains("tone_id=42", h.Requests.Single().RequestUri!.Query);
        Assert.Single(models);
        Assert.Equal("https://x/m5", models[0].ModelUrl);
    }

    [Fact]
    public async Task GetModels_follows_pagination()
    {
        var h = new SequencedHandler(new[]
        {
            """{ "data": [ { "id": 1, "name": "M1", "model_url": "https://cdn.tone3000.com/m1" } ], "page": 1, "page_size": 100, "total": 2, "total_pages": 2 }""",
            """{ "data": [ { "id": 2, "name": "M2", "model_url": "https://cdn.tone3000.com/m2" } ], "page": 2, "page_size": 100, "total": 2, "total_pages": 2 }""",
        });
        var client = new T3kClient(new FakeAuth(), h);

        var models = await client.GetModelsAsync(42);

        Assert.Equal(2, models.Count);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("page=1", h.Requests[0].RequestUri!.Query);
        Assert.Contains("page=2", h.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task Favorite_uses_put_and_unfavorite_uses_delete()
    {
        var (client, h) = Make();
        await client.SetFavoriteAsync(7, favorite: true);
        await client.SetFavoriteAsync(7, favorite: false);
        Assert.Equal(HttpMethod.Put, h.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, h.Requests[1].Method);
        Assert.All(h.Requests, r => Assert.Equal("/api/v1/tones/7/favorite", r.RequestUri!.AbsolutePath));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, T3kError.Auth)]
    [InlineData(HttpStatusCode.TooManyRequests, T3kError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, T3kError.Api)]
    public async Task Http_failures_map_to_typed_errors(HttpStatusCode status, T3kError expected)
    {
        var (client, h) = Make();
        h.Status = status;
        var ex = await Assert.ThrowsAsync<T3kException>(() => client.SearchAsync("x", null, 1));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Network_failure_maps_to_network_error()
    {
        var (client, h) = Make();
        h.Throw = new HttpRequestException("boom");
        var ex = await Assert.ThrowsAsync<T3kException>(() => client.GetToneAsync(1));
        Assert.Equal(T3kError.Network, ex.Kind);
    }

    [Fact]
    public async Task Favorited_hits_the_right_endpoint()
    {
        var (client, h) = Make();
        h.Body = """{ "data": [ { "id": 1, "title": "T" } ], "page": 1, "page_size": 20, "total": 1, "total_pages": 1 }""";
        var page = await client.FavoritedAsync(1);

        var req = h.Requests.Single();
        Assert.Equal("/api/v1/tones/favorited", req.RequestUri!.AbsolutePath);
        Assert.Contains("page=1", req.RequestUri.Query);
        Assert.Single(page.Data);
    }

    [Fact]
    public async Task Downloaded_hits_the_right_endpoint()
    {
        var (client, h) = Make();
        h.Body = """{ "data": [ { "id": 1, "title": "T" } ], "page": 1, "page_size": 20, "total": 1, "total_pages": 1 }""";
        var page = await client.DownloadedAsync(1);

        var req = h.Requests.Single();
        Assert.Equal("/api/v1/tones/downloaded", req.RequestUri!.AbsolutePath);
        Assert.Contains("page=1", req.RequestUri.Query);
        Assert.Single(page.Data);
    }

    [Fact]
    public async Task GetUser_parses_the_user()
    {
        var (client, h) = Make();
        h.Body = """{"id":"uuid-123","username":"ed"}""";
        var user = await client.GetUserAsync();

        var req = h.Requests.Single();
        Assert.Equal("/api/v1/user", req.RequestUri!.AbsolutePath);
        Assert.NotNull(user);
        Assert.Equal("ed", user.Username);
    }

    [Fact]
    public async Task GetTone_parses_a_tone()
    {
        var (client, h) = Make();
        h.Body = """{"id":7,"title":"T","user":{"username":"ed"}}""";
        var tone = await client.GetToneAsync(7);

        var req = h.Requests.Single();
        Assert.Equal("/api/v1/tones/7", req.RequestUri!.AbsolutePath);
        Assert.NotNull(tone);
        Assert.Equal("T", tone.Title);
        Assert.Equal("ed", tone.Author);
    }
}
