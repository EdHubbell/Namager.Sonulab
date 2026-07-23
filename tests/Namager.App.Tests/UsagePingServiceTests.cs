using System.Globalization;
using System.Net;
using System.Text.Json;
using Namager.App.Services;
using Xunit;

public class UsagePingServiceTests : IDisposable
{
    // Every test's throwaway files live under one scoped temp directory; nothing touches the
    // real %APPDATA%, and the directory is removed after each test.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"usage-ping-test-{Guid.NewGuid():N}");
    public UsagePingServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string TempPath() =>
        Path.Combine(_dir, $"usage-{Guid.NewGuid():N}.json");

    /// Records every request and replays a scripted outcome.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _throw;
        public List<string> Bodies { get; } = new();
        public HttpRequestMessage? LastRequest { get; private set; }
        public int Calls => Bodies.Count;

        public FakeHandler(HttpStatusCode status = HttpStatusCode.NoContent, Exception? toThrow = null)
        { _status = status; _throw = toThrow; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            if (_throw is not null) throw _throw;
            return new HttpResponseMessage(_status);
        }
    }

    // ---------- transport normalization ----------
    [Theory]
    [InlineData("USB", "usb")]
    [InlineData("WiFi", "wifi")]
    [InlineData("wifi", "wifi")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("Bluetooth", "unknown")]
    public void NormalizeTransport_maps_provider_names_to_wire_values(string? input, string expected)
        => Assert.Equal(expected, UsagePingService.NormalizeTransport(input));

    // ---------- payload ----------
    [Fact]
    public async Task PingAsync_posts_exactly_the_four_documented_fields()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.test/ping", handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "appVersion", "fw", "installId", "transport" }, keys);
        Assert.Equal("1.2.0", doc.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("2.5.1", doc.RootElement.GetProperty("fw").GetString());
        Assert.Equal("usb", doc.RootElement.GetProperty("transport").GetString());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("installId").GetString(), out _));
    }

    // ---------- day gate ----------
    [Fact]
    public async Task PingAsync_sends_once_per_day()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");
        await svc.PingAsync("2.5.1", "USB");   // same day, fresh read of the saved state

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PingAsync_reuses_the_same_install_id_across_days()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");
        // Rewind the gate to simulate "yesterday", keeping the minted install ID.
        (UsageState.Load(path) with { LastPingUtc = "2000-01-01" }).Save(path);
        await svc.PingAsync("2.5.1", "WiFi");

        Assert.Equal(2, handler.Calls);
        static string Id(string body) =>
            JsonDocument.Parse(body).RootElement.GetProperty("installId").GetString()!;
        Assert.Equal(Id(handler.Bodies[0]), Id(handler.Bodies[1]));
    }

    // ---------- failure handling ----------
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PingAsync_does_not_record_the_day_when_the_server_rejects_it(HttpStatusCode status)
    {
        var path = TempPath();
        var svc = new UsagePingService(new FakeHandler(status), "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);   // offline/rejected days are not burned
    }

    [Fact]
    public async Task PingAsync_swallows_transport_exceptions()
    {
        var path = TempPath();
        var svc = new UsagePingService(
            new FakeHandler(toThrow: new HttpRequestException("offline")),
            "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);
    }

    [Fact]
    public async Task PingAsync_records_the_day_on_success()
    {
        var path = TempPath();
        var svc = new UsagePingService(new FakeHandler(), "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            UsageState.Load(path).LastPingUtc);
    }

    // ---------- firmware sanitization ----------
    // The worker rejects blank or oversized `fw` values with 400 (VALIDATION-usage-telemetry.md
    // cases 8/9), and a rejected ping is never retried into a recorded day — so a device whose
    // firmware read comes back null/empty (CompatibilityChecker builds it as `... ?? ""`) would
    // otherwise make that install permanently invisible.
    [Fact]
    public async Task PingAsync_sends_unknown_for_empty_firmware()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", TempPath());

        await svc.PingAsync("", "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("unknown", doc.RootElement.GetProperty("fw").GetString());
    }

    [Fact]
    public async Task PingAsync_sends_unknown_for_whitespace_only_firmware()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", TempPath());

        await svc.PingAsync("   ", "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("unknown", doc.RootElement.GetProperty("fw").GetString());
    }

    [Fact]
    public async Task PingAsync_truncates_oversized_firmware_to_twenty_chars()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", TempPath());
        var thirtyChars = new string('x', 30);

        await svc.PingAsync(thirtyChars, "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var sent = doc.RootElement.GetProperty("fw").GetString();
        Assert.Equal(20, sent!.Length);
        Assert.Equal(thirtyChars[..20], sent);
    }

    [Fact]
    public async Task PingAsync_leaves_a_normal_firmware_value_unchanged()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", TempPath());

        await svc.PingAsync("2.5.1", "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("2.5.1", doc.RootElement.GetProperty("fw").GetString());
    }

    // ---------- dev builds ----------
    [Fact]
    public async Task PingAsync_is_a_no_op_for_dev_builds()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.0.0-dev", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(path));   // dev runs leave no trace at all
    }
}
