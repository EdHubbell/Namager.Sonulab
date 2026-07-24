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
}
