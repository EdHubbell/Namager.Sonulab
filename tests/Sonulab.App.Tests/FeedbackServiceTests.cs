using System.Net;
using System.Text.Json;
using Sonulab.App.Services;

public class FeedbackServiceTests
{
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body;
        public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private static readonly FeedbackReport Report =
        new("Ed", "ed@gsdware.com", "Great app", "1.2.3", "Windows 11");

    [Fact]
    public async Task Posts_json_with_all_fields_and_empty_honeypot()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created);
        var svc = new FeedbackService(handler, "https://feedback.example.test/");

        await svc.SendAsync(Report);

        Assert.Equal("https://feedback.example.test/", handler.Uri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("Ed", root.GetProperty("name").GetString());
        Assert.Equal("ed@gsdware.com", root.GetProperty("email").GetString());
        Assert.Equal("Great app", root.GetProperty("message").GetString());
        Assert.Equal("1.2.3", root.GetProperty("appVersion").GetString());
        Assert.Equal("Windows 11", root.GetProperty("os").GetString());
        Assert.Equal("", root.GetProperty("website").GetString());   // honeypot always empty
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Non_success_status_throws_FeedbackSendException(HttpStatusCode status)
    {
        var svc = new FeedbackService(new CapturingHandler(status), "https://feedback.example.test/");
        await Assert.ThrowsAsync<FeedbackSendException>(() => svc.SendAsync(Report));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("dns failure");
    }

    [Fact]
    public async Task Network_error_wraps_into_FeedbackSendException()
    {
        var svc = new FeedbackService(new ThrowingHandler(), "https://feedback.example.test/");
        await Assert.ThrowsAsync<FeedbackSendException>(() => svc.SendAsync(Report));
    }
}
