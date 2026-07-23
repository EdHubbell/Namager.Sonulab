using System.Diagnostics;
using Namager.App.ViewModels;
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

    [Fact] public async Task Connect_when_no_device_found_sets_status()
    {
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Equal("Disconnected (no device found on USB or WiFi)", vm.Status);
    }

    // PingAsync is now fired-and-forgotten by ConnectAsync (it must never gate the Connect
    // command), so tests that need to observe a ping cannot rely on the await completing it for
    // them. The spy exposes a completion signal that positive tests await with a bounded
    // timeout: a genuine regression (ping never called) then fails fast instead of hanging.
    private sealed class SpyUsagePing : Namager.App.Services.IUsagePingService
    {
        public List<(string Firmware, string? Transport)> Pings { get; } = new();
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // When set, PingAsync waits on this gate before recording — lets a test hold the ping
        // open indefinitely (no real delay/sleep needed) to prove the caller didn't wait on it.
        public TaskCompletionSource? Gate { get; set; }

        public async Task PingAsync(string firmware, string? transport, CancellationToken ct = default)
        {
            if (Gate is not null) await Gate.Task;
            Pings.Add((firmware, transport));
            _completed.TrySetResult();
        }

        /// <summary>True if the ping completed within <paramref name="timeout"/>, false if it timed out.</summary>
        public async Task<bool> WaitForPingAsync(TimeSpan timeout)
        {
            var winner = await Task.WhenAny(_completed.Task, Task.Delay(timeout));
            return winner == _completed.Task;
        }
    }

    [Fact] public async Task Connect_pings_usage_once_with_firmware_and_transport()
    {
        var spy = new SpyUsagePing();
        var vm = new ConnectionViewModel(Session(), spy);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(await spy.WaitForPingAsync(TimeSpan.FromSeconds(5)), "usage ping did not complete in time");
        Assert.Single(spy.Pings);
        Assert.Equal("2.5.1", spy.Pings[0].Firmware);
        Assert.Equal("USB", spy.Pings[0].Transport);
    }

    [Fact] public async Task Reconnecting_in_the_same_run_does_not_ping_again()
    {
        var spy = new SpyUsagePing();
        var vm = new ConnectionViewModel(Session(), spy);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(await spy.WaitForPingAsync(TimeSpan.FromSeconds(5)), "usage ping did not complete in time");

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Single(spy.Pings);
    }

    [Fact] public async Task Failed_connect_does_not_ping()
    {
        var spy = new SpyUsagePing();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, spy);

        await vm.ConnectCommand.ExecuteAsync(null);

        // ConnectAsync returns before ever reaching the ping call on a failed connect, so no
        // signal will ever arrive — this bounded wait is expected to time out. A short timeout
        // keeps the "nothing happened" assertion honest (not just "checked too early") without
        // making the suite slow.
        Assert.False(await spy.WaitForPingAsync(TimeSpan.FromMilliseconds(200)), "ping fired on a failed connect");
        Assert.Empty(spy.Pings);
    }

    [Fact] public async Task Connect_without_a_usage_service_still_works()
    {
        var vm = new ConnectionViewModel(Session());   // null usage service
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
    }

    [Fact] public async Task Connect_returns_promptly_even_when_usage_ping_is_slow()
    {
        // Reproduces the reported bug: with the ping awaited, ExecuteAsync would block for as
        // long as PingAsync takes (up to its HTTP timeout when offline/unreachable), keeping the
        // bound Connect button disabled. Hold the ping open via a gate the test controls (no
        // real sleep, so this stays fast and non-flaky) and assert ExecuteAsync still returns
        // quickly and the command is not left "running".
        var spy = new SpyUsagePing { Gate = new TaskCompletionSource() };
        var vm = new ConnectionViewModel(Session(), spy);

        var sw = Stopwatch.StartNew();
        await vm.ConnectCommand.ExecuteAsync(null);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"ExecuteAsync took {sw.ElapsedMilliseconds}ms; it must not wait on the usage ping");
        Assert.False(vm.ConnectCommand.IsRunning);
        Assert.Empty(spy.Pings); // still gated — proves ExecuteAsync didn't wait for it

        spy.Gate.SetResult(); // release the ping so its background task completes cleanly
        Assert.True(await spy.WaitForPingAsync(TimeSpan.FromSeconds(5)), "usage ping did not complete after release");
        Assert.Single(spy.Pings);
    }
}
