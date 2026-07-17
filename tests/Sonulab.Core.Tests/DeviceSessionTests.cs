using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceSessionTests
{
    static SerialLinkOptions Fast => new() { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 300 };

    // One fake that answers identity + browse on the right baud.
    static FakeSerialPort MakeDevice()
    {
        var p = new FakeSerialPort();
        p.Responder = cmd =>
        {
            if (p.OpenedBaud != 115200) return "";
            return cmd switch
            {
                @"read root\sys\_name"    => "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n",
                @"read root\sys\_id"      => "root\\sys\\_id:{\"value\":\"abc\"}\r\n",
                @"read root\sys\_ver"     => "root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n",
                @"read root\sys\_arch"    => "root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n",
                @"read root\sys\_license" => "root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n",
                @"browse root\presets" => "root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n",
                @"browse root\amp"     => "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n",
                @"browse root\ir"      => "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n",
                _ => "",
            };
        };
        return p;
    }

    private sealed class FixedProvider(string name, Sonulab.Core.Transport.ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => Task.FromResult(link);
    }

    private sealed class ThrowingProvider : ILinkProvider
    {
        public string Name => "Broken";
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("transport unavailable");
    }

    [Fact] public async Task Connects_identifies_and_reports_tested()
    {
        var connector = new SonuConnector(MakeDevice, Fast);
        var workingLink = await connector.ConnectAsync(new[] { "COM6" }, new[] { 115200 });
        var checker = new CompatibilityChecker(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", workingLink) },
            checker);

        var state = await session.ConnectAsync();

        Assert.True(state.Connected);
        Assert.Equal("AMP Station", state.Device!.Name);
        Assert.Equal(CompatibilityStatus.Tested, state.Compatibility!.Status);
        Assert.True(state.Compatibility.WritesAllowed);
        Assert.Equal("USB", state.Transport);
        Assert.NotNull(session.Client);
    }

    [Fact] public async Task Reports_disconnected_when_no_device()
    {
        var checker = new CompatibilityChecker(System.Array.Empty<TestedFirmware>());
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null) },
            checker);

        var state = await session.ConnectAsync();

        Assert.False(state.Connected);
        Assert.Null(state.Device);
        Assert.Null(state.Transport);
        Assert.Null(session.Client);
    }

    [Fact]
    public async Task Second_provider_is_tried_when_first_returns_null_and_transport_is_reported()
    {
        var connector = new SonuConnector(MakeDevice, Fast);
        var workingLink = await connector.ConnectAsync(new[] { "COM6" }, new[] { 115200 });
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var state = await session.ConnectAsync();
        Assert.True(state.Connected);
        Assert.Equal("WiFi", state.Transport);
    }

    [Fact]
    public async Task Throwing_provider_is_skipped_not_fatal()
    {
        var connector = new SonuConnector(MakeDevice, Fast);
        var workingLink = await connector.ConnectAsync(new[] { "COM6" }, new[] { 115200 });
        var session = new DeviceSession(
            new ILinkProvider[] { new ThrowingProvider(), new FixedProvider("WiFi", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var state = await session.ConnectAsync();
        Assert.True(state.Connected);
        Assert.Equal("WiFi", state.Transport);
    }
}
