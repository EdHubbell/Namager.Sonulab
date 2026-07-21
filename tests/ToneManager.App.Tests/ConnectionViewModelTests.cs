using ToneManager.App.ViewModels;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class ConnectionViewModelTests
{
    private sealed class FixedProvider(string name, Sonulab.Core.Transport.ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => Task.FromResult(link);
    }

    // A connector whose factory yields a fake that answers identity + per-node browse on baud 115200.
    static DeviceSession Session()
    {
        FakeSerialPort Make()
        {
            var p = new FakeSerialPort();
            p.Responder = cmd => p.OpenedBaud != 115200 ? "" : cmd switch
            {
                @"read root\sys\_name"    => "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n",
                @"read root\sys\_id"      => "root\\sys\\_id:{\"value\":\"abc\"}\r\n",
                @"read root\sys\_ver"     => "root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n",
                @"read root\sys\_arch"    => "root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n",
                @"read root\sys\_license" => "root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n",
                @"browse root\presets"    => "root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n",
                @"browse root\amp"        => "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n",
                @"browse root\ir"         => "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n",
                _ => "",
            };
            return p;
        }
        var connector = new SonuConnector(Make, new SerialLinkOptions { PollMs = 2, IdleGapMs = 10, FirstByteTimeoutMs = 20, MaxWaitMs = 300 });
        var workingLink = connector.ConnectAsync(new[] { "COM6" }, new[] { 115200 }).GetAwaiter().GetResult();
        return new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
    }

    [Fact] public async Task Connect_sets_status_and_exposes_repository()
    {
        var vm = new ConnectionViewModel(Session());
        Assert.False(vm.IsConnected);
        bool fired = false; vm.Connected += (_, _) => fired = true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.True(vm.WritesAllowed);
        Assert.Contains("AMP Station", vm.Status);
        Assert.Contains("2.5.1", vm.Status);
        Assert.Contains("(USB)", vm.Status);
        Assert.NotNull(vm.Repository);
        Assert.NotNull(vm.Reorder);
        Assert.True(fired);
        Assert.NotNull(vm.Client);
    }
}
