using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        //
        // The settled state alone does not pin the "IsDeviceLost is set BEFORE IsConnected"
        // ordering: by the time the handler returns, both flags hold their final values
        // regardless of which was assigned first, and CanExecute() re-evaluates fresh on every
        // call rather than caching a value from an intermediate NotifyCanExecuteChanged firing.
        // To actually observe the ordering, capture what CanExecute() reports at each
        // CanExecuteChanged notification during the transition — if IsConnected flipped first,
        // the first notification would see IsConnected=false, IsDeviceLost=false and report
        // CanExecute()=true, transiently re-enabling Connect.
        var (vm, link) = Connected();
        var observedDuringTransition = new List<bool>();
        vm.ConnectCommand.CanExecuteChanged += (_, _) =>
            observedDuringTransition.Add(vm.ConnectCommand.CanExecute(null));

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.NotEmpty(observedDuringTransition);
        Assert.All(observedDuringTransition, canExecute => Assert.False(canExecute));
        Assert.False(vm.ConnectCommand.CanExecute(null));   // settled state, still worth asserting directly
    }

    [Fact] public async Task DeviceLost_event_fires_once()
    {
        // SonuClient.Latch() already guarantees Disconnected fires at most once, via
        // Interlocked.CompareExchange, independent of anything downstream. So this test verifies
        // that guarantee reaches ConnectionViewModel's DeviceLost event unchanged — it does NOT
        // pin ConnectionViewModel's own `if (IsDeviceLost) return;` guard (that guard is isolated
        // by OnDeviceDisconnected_guard_survives_being_invoked_twice below, since SonuClient's
        // public surface only allows Disconnected to fire once per instance and so cannot be used
        // to exercise the VM-side guard on its own).
        var (vm, link) = Connected();
        int fired = 0;
        vm.DeviceLost += (_, _) => fired++;

        link.Kill = true;
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Equal(1, fired);
    }

    [Fact] public void OnDeviceDisconnected_guard_survives_being_invoked_twice()
    {
        // Isolates ConnectionViewModel's own `if (IsDeviceLost) return;` guard from SonuClient's
        // single-fire latch (which DeviceLost_event_fires_once above actually exercises) by
        // calling the private handler directly, twice, bypassing SonuClient entirely. This is the
        // only way to observe the VM-side guard specifically.
        var (vm, _) = Connected();
        int fired = 0;
        vm.DeviceLost += (_, _) => fired++;

        var handler = typeof(ConnectionViewModel).GetMethod("OnDeviceDisconnected",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = new DeviceDisconnectedException("USB");
        handler.Invoke(vm, new object[] { ex });
        handler.Invoke(vm, new object[] { ex });

        Assert.Equal(1, fired);
    }

    [Fact] public async Task Throwing_DeviceLost_subscriber_does_not_mask_the_original_exception()
    {
        // SonuClient.Latch() calls Disconnected?.Invoke(ex) synchronously, then `throw;` rethrows
        // the original DeviceDisconnectedException at the failing send's call site. Under
        // synchronous test dispatch (and any future non-UI dispatch caller — production is only
        // shielded because Dispatcher.UIThread.Post defers execution), an unguarded DeviceLost
        // invoke would let a throwing subscriber's exception propagate out of Latch() instead,
        // replacing the DeviceDisconnectedException the caller of the failing send is expecting.
        var (vm, link) = Connected();
        vm.DeviceLost += (_, _) => throw new InvalidOperationException("broken subscriber");

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        // The VM still reached its dead state despite the throwing subscriber.
        Assert.True(vm.IsDeviceLost);
        Assert.False(vm.IsConnected);
    }

    static (ConnectionViewModel Vm, KillableLink Link, FakeStatusService Status) ConnectedWithStatus()
    {
        var status = new FakeStatusService();
        var link = new KillableLink();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", link) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, null, status, dispatch: a => a());
        vm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (vm, link, status);
    }

    [Fact] public async Task Status_bar_reports_the_disconnect()
    {
        // The handler deliberately pushes NO Failure of its own. The exception the Disconnected
        // event carries is the BARE one the transport threw — enrichment with the at-risk slot
        // happens above SonuClient (SlotBlobService.UploadAsync), on the instance rethrown to ITS
        // caller, which never reaches this event. A Failure here could therefore only ever repeat
        // the generic message, and would stomp the enriched one (see the anti-stomp test below).
        var (vm, link, status) = ConnectedWithStatus();

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        // The dead state IS reported — through the two channels the handler owns.
        Assert.Equal("Device disconnected — reconnect the pedal and restart NAMager", vm.Status);
        Assert.Contains("Device disconnected", status.IdleSummaries);
        // …and not through a Failure push. (The failing operation's own catch reports that; here
        // the test called SendRawAsync directly, so nothing else reported anything.)
        Assert.Empty(status.Failed);
    }

    [Fact] public async Task Enriched_upload_failure_is_not_stomped_by_the_dead_state_handler()
    {
        // Real ordering in an interrupted upload: AmpListViewModel's catch (IOException) reports
        // the ENRICHED, slot-naming message synchronously; only afterwards does the UI thread pump
        // the Dispatcher.UIThread.Post from OnDeviceDisconnected. A Failure(ex.Message) in that
        // handler would make the generic message the status bar's last word.
        var (vm, link, status) = ConnectedWithStatus();
        var enriched = new DeviceDisconnectedException("USB").ForSlot("Amp", 12, writing: true);
        status.Failure(enriched.Message);          // stands in for the upload's own catch

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Equal(
            "Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.",
            status.Failed[^1]);
    }
}
