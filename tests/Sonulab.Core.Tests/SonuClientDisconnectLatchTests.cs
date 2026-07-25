using System.IO;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientDisconnectLatchTests
{
    /// <summary>A link that dies on the Nth send and counts how many times it was touched.</summary>
    private sealed class DyingLink(int dieOnSend) : ISonuLink
    {
        public int Sends;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (++Sends >= dieOnSend)
            {
                IsOpen = false;
                throw new DeviceDisconnectedException("USB", new IOException("cable pulled"));
            }
            return Task.FromResult("root\\x:{\"value\":1}\r\n");
        }
    }

    /// <summary>Models what a REAL transport does, which <see cref="DyingLink"/> deliberately does
    /// not: it throws <see cref="DeviceDisconnectedException"/> ONCE and closes its own port, so
    /// every later send trips the precondition check that sits OUTSIDE the classification try
    /// (SerialSonuLink.cs:98 / TcpSonuLink.cs:59) and raises a raw
    /// <see cref="InvalidOperationException"/> instead.
    ///
    /// The first send parks until the test releases it, so a second caller can be pinned in the
    /// one state that matters: past SonuClient's PRE-gate ThrowIfDead, queued on the gate, and
    /// therefore about to touch a link that will be dead by the time it is let through.</summary>
    private sealed class DyingThenClosedLink : ISonuLink
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sends;

        /// <summary>Completes once the first send is inside the link, holding SonuClient's gate.</summary>
        public Task Entered => _entered.Task;
        public void ReleaseAndDie() => _release.TrySetResult();

        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _sends) == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
                IsOpen = false;                       // SerialSonuLink.Fault() closes the port…
                throw new DeviceDisconnectedException("USB", new IOException("cable pulled"));
            }
            // …so from here on the transport's own precondition check fires, unclassified.
            throw new InvalidOperationException("Serial link is not open.");
        }
    }

    static SonuClient Client(ISonuLink link) =>
        new(link, readRetryAttempts: 1, readRetryDelayMs: 0, backgroundQuietMs: 0,
            tickSource: () => 0, backgroundPollDelay: _ => Task.CompletedTask);

    [Fact] public async Task First_disconnect_surfaces_and_sets_IsDisconnected()
    {
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        Assert.False(c.IsDisconnected);

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        Assert.True(c.IsDisconnected);
    }

    [Fact] public async Task Later_sends_fail_instantly_without_touching_the_dead_link()
    {
        // This is the point of the latch: PresetListViewModel's failure handler immediately calls
        // ReloadAsync(), which used to re-attempt a 30-slot read against a dead port and throw a
        // second raw I/O error.
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        int touchedSoFar = link.Sends;

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read y"));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.ReadListAsync(@"root\amp"));
        Assert.Equal(touchedSoFar, link.Sends);
    }

    [Fact] public async Task Repeated_failures_carry_the_original_message()
    {
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        var again = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read y"));
        Assert.Equal("Device disconnected (USB).", again.Message);
        Assert.Equal("USB", again.Transport);
    }

    [Fact] public async Task Disconnected_is_raised_exactly_once_under_concurrency()
    {
        int raised = 0;
        var c = Client(new DyingLink(dieOnSend: 1));
        c.Disconnected += _ => Interlocked.Increment(ref raised);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await c.SendRawAsync($"read {i}"); } catch (DeviceDisconnectedException) { }
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(1, raised);
    }

    [Fact] public async Task Batch_read_latches_too()
    {
        // Multi-chunk dread goes through SendBatchGatedAsync, a different gate method.
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => c.DReadChunkRangeAsync(@"root\amp", 0, 1, 8));
        Assert.True(c.IsDisconnected);
    }

    [Fact] public async Task Background_lane_returns_immediately_on_a_latched_client()
    {
        // SendBackgroundAsync calls _link.SendAsync DIRECTLY, bypassing the private SendAsync, and
        // its while(true) quiet-window loop would otherwise keep polling a corpse forever.
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));

        var background = c.SendBackgroundAsync(@"read root\amp");
        var finished = await Task.WhenAny(background, Task.Delay(2000));
        Assert.Same(background, finished);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => background);
    }

    [Fact] public async Task Background_lane_latches_its_own_failure()
    {
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendBackgroundAsync("read x"));
        Assert.True(c.IsDisconnected);
    }

    [Fact] public async Task A_caller_queued_on_the_gate_still_gets_a_typed_disconnect()
    {
        // The pre-gate ThrowIfDead alone is not enough: a caller that passed it BEFORE the link
        // died then waits on the gate and touches an already-closed port, whose raw
        // "Serial link is not open." is exactly the unreadable string this feature exists to
        // eliminate. Two foreground calls contending on the gate is normal in the app
        // (MainWindowViewModel fires LoadInitialAsync and NavigateToUploadAsync fire-and-forget).
        var link = new DyingThenClosedLink();
        var c = Client(link);

        var first = Task.Run(() => c.SendRawAsync("read x"));
        await link.Entered;                       // A is inside the link, holding the gate

        // Started on THIS thread on purpose: SendAsync runs synchronously through the pre-gate
        // ThrowIfDead (the client is still alive) and only then parks on _gate.WaitAsync — no
        // sleep needed to pin the interleaving.
        var queued = c.SendRawAsync("read y");
        Assert.False(queued.IsCompleted);         // genuinely queued behind A

        link.ReleaseAndDie();
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => first);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => queued);
        Assert.Equal("Device disconnected (USB).", ex.Message);
    }

    [Fact] public async Task A_batch_queued_on_the_gate_still_gets_a_typed_disconnect()
    {
        // Same trace through SendBatchGatedAsync, which has its own copy of the gate dance.
        var link = new DyingThenClosedLink();
        var c = Client(link);

        var first = Task.Run(() => c.SendRawAsync("read x"));
        await link.Entered;

        var queued = c.DReadChunkRangeAsync(@"root\amp", 0, 1, 4);   // >1 chunk = batch path
        Assert.False(queued.IsCompleted);

        link.ReleaseAndDie();
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => first);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => queued);
        Assert.Equal("Device disconnected (USB).", ex.Message);
    }
}
