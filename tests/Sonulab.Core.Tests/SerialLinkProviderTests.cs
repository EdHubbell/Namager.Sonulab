using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class SerialLinkProviderTests
{
    // A fake that only answers the name query when opened at the "correct" baud.
    static FakeSerialPort MakePort(int answersAtBaud)
    {
        var p = new FakeSerialPort();
        p.Responder = cmd =>
            (cmd == @"read root\sys\_name" && p.OpenedBaud == answersAtBaud)
                ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "";
        return p;
    }

    [Fact]
    public void Name_is_USB()
        => Assert.Equal("USB", new SerialLinkProvider(() => new FakeSerialPort()).Name);

    [Fact]
    public async Task Connects_via_existing_connector_path()
    {
        var provider = new SerialLinkProvider(
            () => MakePort(115200),
            new SerialLinkOptions { FirstByteTimeoutMs = 50, MaxWaitMs = 200 },
            portNames: () => new[] { "COM6" });
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
    }

    [Fact]
    public async Task Port_names_are_enumerated_fresh_on_every_attempt()
    {
        int calls = 0;
        var provider = new SerialLinkProvider(
            () => new FakeSerialPort(),
            new SerialLinkOptions { FirstByteTimeoutMs = 20, MaxWaitMs = 50 },
            portNames: () => { calls++; return new[] { "COM1" }; });
        await provider.TryConnectAsync();
        await provider.TryConnectAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Returns_null_when_no_ports_present()
    {
        var provider = new SerialLinkProvider(
            () => new FakeSerialPort(), portNames: () => Array.Empty<string>());
        Assert.Null(await provider.TryConnectAsync());
    }
}
