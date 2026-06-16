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

    [Fact] public async Task Connects_identifies_and_reports_tested()
    {
        var connector = new SonuConnector(MakeDevice, Fast);
        var checker = new CompatibilityChecker(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });
        var session = new DeviceSession(connector, checker);

        var state = await session.ConnectAsync(new[] { "COM6" }, new[] { 115200 });

        Assert.True(state.Connected);
        Assert.Equal("AMP Station", state.Device!.Name);
        Assert.Equal(CompatibilityStatus.Tested, state.Compatibility!.Status);
        Assert.True(state.Compatibility.WritesAllowed);
        Assert.NotNull(session.Client);
    }

    [Fact] public async Task Reports_disconnected_when_no_device()
    {
        var connector = new SonuConnector(() => new FakeSerialPort(), Fast); // never answers
        var checker = new CompatibilityChecker(System.Array.Empty<TestedFirmware>());
        var session = new DeviceSession(connector, checker);

        var state = await session.ConnectAsync(new[] { "COM6" }, new[] { 115200 });

        Assert.False(state.Connected);
        Assert.Null(state.Device);
        Assert.Null(session.Client);
    }
}
