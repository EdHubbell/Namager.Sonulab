using System.Text;
using Sonulab.Transport.Wifi;
using Sonulab.Transport.Wifi.Tests;

public class WifiLinkProviderTests
{
    private static readonly TcpLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static readonly MdnsRecord Pedal =
        new("voidxc7e8", "voidxc7e8.local", "192.168.8.241", 8080, "AMP Station");

    private sealed class FakeQuerier(MdnsRecord? result) : IMdnsQuerier
    {
        public Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static FakeTcpConn AnsweringConn()
    {
        var conn = new FakeTcpConn();
        conn.RespondWith = cmd => cmd == @"read root\sys\_name"
            ? Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0")
            : Array.Empty<byte>();
        return conn;
    }

    [Fact]
    public void Name_is_WiFi()
        => Assert.Equal("WiFi", new WifiLinkProvider(new FakeQuerier(null), TimeSpan.Zero).Name);

    [Fact]
    public async Task Connects_to_discovered_endpoint_and_verifies_identity()
    {
        var conn = AnsweringConn();
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
        Assert.Equal(("192.168.8.241", 8080), conn.ConnectedTo);
    }

    [Fact]
    public async Task Returns_null_when_discovery_finds_nothing()
    {
        var provider = new WifiLinkProvider(new FakeQuerier(null), TimeSpan.FromSeconds(1),
            connFactory: () => new FakeTcpConn(), options: Fast);
        Assert.Null(await provider.TryConnectAsync());
    }

    [Fact]
    public async Task Probe_retries_cover_the_empty_first_response_quirk()
    {
        // Live finding: the FIRST command on a fresh TCP connection can come back as an
        // empty record; the provider must retry the probe, not give up.
        int n = 0;
        var conn = new FakeTcpConn();
        conn.RespondWith = cmd =>
        {
            if (cmd != @"read root\sys\_name") return Array.Empty<byte>();
            n++;
            return n == 1
                ? Encoding.ASCII.GetBytes("\r\n\0")                    // the observed empty record
                : Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0");
        };
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast, probeAttempts: 3, probeRetryDelayMs: 1);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Silent_device_fails_probe_and_link_is_closed()
    {
        var conn = new FakeTcpConn();                                   // never answers
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast, probeAttempts: 2, probeRetryDelayMs: 1);
        Assert.Null(await provider.TryConnectAsync());
        Assert.False(conn.Connected);
    }

    [Fact]
    public async Task ForKnownEndpoint_skips_discovery()
    {
        var conn = AnsweringConn();
        var provider = WifiLinkProvider.ForKnownEndpoint("10.0.0.5", 8080, () => conn, Fast);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.Equal(("10.0.0.5", 8080), conn.ConnectedTo);
    }
}
