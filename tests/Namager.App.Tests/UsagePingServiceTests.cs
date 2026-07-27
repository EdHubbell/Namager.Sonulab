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

    /// <summary>Every test must supply isEnabled explicitly. The 4-arg positional constructor
    /// (no isEnabled) falls back to AppSettingsStore.Load().ShareUsageData, which reads the real
    /// %APPDATA%\Namager\settings.json — the developer's actual opt-out choice, not a fixture. A
    /// contributor who has opted out locally would otherwise see these tests fail. This helper
    /// keeps "isEnabled: () => true" the default so a new test has to opt out of the opt-out
    /// deliberately, rather than accidentally inheriting the ambient default.</summary>
    private static UsagePingService Svc(HttpMessageHandler handler, string? statePath = null,
        string appVersion = "1.2.0", string endpoint = "https://example.test/ping") =>
        new(handler, endpoint, appVersion, statePath, isEnabled: () => true);

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
        var svc = Svc(handler, path);

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
        var svc = Svc(handler, path);

        await svc.PingAsync("2.5.1", "USB");
        await svc.PingAsync("2.5.1", "USB");   // same day, fresh read of the saved state

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PingAsync_reuses_the_same_install_id_across_days()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = Svc(handler, path);

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
        var svc = Svc(new FakeHandler(status), path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);   // offline/rejected days are not burned
    }

    [Fact]
    public async Task PingAsync_swallows_transport_exceptions()
    {
        var path = TempPath();
        var svc = Svc(new FakeHandler(toThrow: new HttpRequestException("offline")), path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);
    }

    [Fact]
    public async Task PingAsync_records_the_day_on_success()
    {
        var path = TempPath();
        var svc = Svc(new FakeHandler(), path);

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
        var svc = Svc(handler, TempPath());

        await svc.PingAsync("", "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("unknown", doc.RootElement.GetProperty("fw").GetString());
    }

    [Fact]
    public async Task PingAsync_sends_unknown_for_whitespace_only_firmware()
    {
        var handler = new FakeHandler();
        var svc = Svc(handler, TempPath());

        await svc.PingAsync("   ", "USB");

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("unknown", doc.RootElement.GetProperty("fw").GetString());
    }

    [Fact]
    public async Task PingAsync_truncates_oversized_firmware_to_twenty_chars()
    {
        var handler = new FakeHandler();
        var svc = Svc(handler, TempPath());
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
        var svc = Svc(handler, TempPath());

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
        var svc = Svc(handler, path, appVersion: "1.0.0-dev");

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(path));   // dev runs leave no trace at all
    }

    // ---------- opt-out ----------
    [Fact]
    public async Task Does_not_send_when_sharing_is_disabled()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                       appVersion: "1.0.0", statePath: TempPath(),
                                       isEnabled: () => false);

        await svc.PingAsync("2.5.1", "usb");

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Sends_when_sharing_is_enabled()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                       appVersion: "1.0.0", statePath: TempPath(),
                                       isEnabled: () => true);

        await svc.PingAsync("2.5.1", "usb");

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_throwing_settings_read_does_not_break_the_ping_path()
    {
        var handler = new FakeHandler();
        var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                       appVersion: "1.0.0", statePath: TempPath(),
                                       isEnabled: () => throw new InvalidOperationException("boom"));

        await svc.PingAsync("2.5.1", "usb");   // must not throw

        Assert.Equal(0, handler.Calls);        // fail closed: no send when consent is unknown
    }
}
