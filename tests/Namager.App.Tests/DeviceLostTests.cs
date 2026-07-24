using System.IO;
using Namager.App.ViewModels;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceLostTests
{
    private sealed class FixedProvider(string name, ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(link);
    }

    /// <summary>Identifies as a real pedal, then dies on demand.</summary>
    private sealed class KillableLink : ISonuLink
    {
        public bool Kill;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (Kill) { IsOpen = false; throw new DeviceDisconnectedException("USB", new IOException("cable pulled")); }
            return Task.FromResult(command switch
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
            });
        }
    }

    static (ConnectionViewModel Vm, KillableLink Link) Connected()
    {
        var link = new KillableLink();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", link) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        // Synchronous dispatch: unit tests have no Avalonia dispatcher loop to drain.
        var vm = new ConnectionViewModel(session, null, null, dispatch: a => a());
        vm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (vm, link);
    }

    [Fact] public async Task Device_loss_flips_the_session_to_a_dead_state()
    {
        var (vm, link) = Connected();
        Assert.True(vm.IsConnected);

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.False(vm.IsConnected);
        Assert.True(vm.IsDeviceLost);
        Assert.Equal("Device disconnected — reconnect the pedal and restart NAMager", vm.Status);
    }

    [Fact] public async Task Connect_stays_disabled_after_a_loss()
    {
        // Without the IsDeviceLost latch, IsConnected = false would RE-ENABLE Connect — the
        // reconnect-in-place this design rejects (re-opening a live session wedged the ESP32).
        var (vm, link) = Connected();
        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact] public async Task DeviceLost_event_fires_once()
    {
        var (vm, link) = Connected();
        int fired = 0;
        vm.DeviceLost += (_, _) => fired++;

        link.Kill = true;
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Equal(1, fired);
    }

    [Fact] public async Task Status_bar_gets_the_slot_naming_message()
    {
        var status = new FakeStatusService();
        var link = new KillableLink();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", link) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, null, status, dispatch: a => a());
        await vm.ConnectCommand.ExecuteAsync(null);

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Contains(status.Failed, f => f.Contains("Device disconnected"));
    }
}
