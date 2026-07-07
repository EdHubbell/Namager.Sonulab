using System.Net;
using System.Text;
using Sonulab.Tone3000;

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
}
