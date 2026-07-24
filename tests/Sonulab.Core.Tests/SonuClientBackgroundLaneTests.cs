using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientBackgroundLaneTests
{
    /// <summary>Link stub that records commands and answers dreads like FakePresetDevice.</summary>
    private sealed class RecordingLink : ISonuLink
    {
        public readonly List<string> Commands = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(command.StartsWith("read ", StringComparison.Ordinal)
                ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "");
        }
    }

    // tick + poll are injected so these tests are deterministic: the poll delay yields the
    // loop back to the test, which advances the fake clock.
    private static (SonuClient client, RecordingLink link, Action<long> setTick) Make(int quietMs = 1000)
    {
        long tick = 0;
        var link = new RecordingLink();
        var client = new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0,
            backgroundQuietMs: quietMs,
            tickSource: () => Volatile.Read(ref tick),
            backgroundPollDelay: _ => Task.Delay(1));
        return (client, link, v => Volatile.Write(ref tick, v));
    }

    [Fact]
    public async Task Background_send_waits_for_the_foreground_quiet_window()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(0);
        await client.ReadValueAsync(@"root\sys\_name");            // foreground at tick 0
        int after = link.Commands.Count;

        setTick(500);                                              // only 500 ms quiet
        var bg = client.SendBackgroundAsync("dread root\\presets:{\"index\":0,\"chunk\":1}");
        await Task.Delay(50);                                      // give the poll loop real time
        Assert.False(bg.IsCompleted);
        Assert.Equal(after, link.Commands.Count);                  // nothing sent yet

        setTick(1500);                                             // quiet window satisfied
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(after + 1, link.Commands.Count);
    }

    [Fact]
    public async Task Background_sends_do_not_reset_the_quiet_clock()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(5000);                                             // long quiet since construction tick 0? see note
        await client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":1}");
        await client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":2}");   // must not wait
        Assert.Equal(2, link.Commands.Count);
    }

    [Fact]
    public async Task Foreground_is_not_delayed_by_a_waiting_background_command()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(0);
        await client.ReadValueAsync(@"root\sys\_name");            // stamps the clock at 0
        setTick(100);
        var bg = client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":1}"); // waits (quiet not met)
        var fg = await client.ReadValueAsync(@"root\sys\_name");   // must complete promptly
        Assert.Equal("AMP Station", fg);
        setTick(5000);
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Background_dread_range_parses_chunks_like_the_foreground_twin()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        await dev.OpenAsync();
        var client = new SonuClient(dev, backgroundQuietMs: 0);    // 0 = no quiet gating
        var fgBytes = await client.DReadChunkRangeAsync(@"root\presets", 0, 1, 2);
        var bgBytes = await client.DReadChunkRangeBackgroundAsync(@"root\presets", 0, 1, 2);
        Assert.Equal(fgBytes, bgBytes);
    }

    [Fact]
    public async Task Background_list_read_parses_names()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        await dev.OpenAsync();
        var client = new SonuClient(dev, backgroundQuietMs: 0);
        var names = await client.ReadListBackgroundAsync(@"root\presets");
        Assert.Equal(30, names.Count);
        Assert.Equal("Lead", names[0]);
    }

    private sealed class EmptyReplyLink : ISonuLink
    {
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default) => Task.FromResult("");
    }

    [Fact]
    public async Task Background_list_read_throws_when_no_list_record()
    {
        // A torn/empty reply must NOT be treated as "30 empty slots" — that would let the
        // preset-usage scan complete with a map missing real amp/IR references (fail-open).
        var client = new SonuClient(new EmptyReplyLink(), backgroundQuietMs: 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadListBackgroundAsync(@"root\presets"));
    }

    /// <summary>Link whose BATCH send blocks until the test releases it, so we can observe what
    /// the background lane is allowed to do while a burst is in flight.</summary>
    private sealed class GatedBatchLink : ISonuLink
    {
        public readonly TaskCompletionSource Release = new();
        public readonly List<string> Commands = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(Window(command));
        }

        public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
        {
            await Release.Task;
            lock (Commands) Commands.AddRange(commands);
            return commands.Select(Window).ToList();
        }

        /// <summary>A well-formed dread reply (128 zero bytes) so the read needs NO repair pass —
        /// otherwise the repair reads would pollute the command count this test asserts on.</summary>
        private static string Window(string command)
        {
            var m = System.Text.RegularExpressions.Regex.Match(command, @"""index"":(\d+),""chunk"":(-?\d+)");
            return m.Success
                ? $"root\\presets:{{\"index\":{m.Groups[1].Value},\"chunk\":{m.Groups[2].Value},\"value\":\"{new string('0', 256)}\"}}\r\n"
                : "";
        }
    }

    [Fact]
    public async Task Background_send_cannot_interleave_inside_a_pipelined_batch()
    {
        // The quiet window is 0 here, so the ONLY thing that can hold the background send back
        // is the client gate — which the batch must hold for the whole burst. An interleaved
        // dread inside a burst is the documented way to get a device commit silently discarded.
        long tick = 0;
        var link = new GatedBatchLink();
        var client = new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0,
            backgroundQuietMs: 0,
            tickSource: () => Volatile.Read(ref tick),
            backgroundPollDelay: _ => Task.Delay(1));

        var batch = client.DReadChunkRangeAsync(@"root\presets", 0, 1, 4);   // takes the gate, blocks
        await Task.Delay(50);
        var bg = client.SendBackgroundAsync(@"read root\sys\_name");
        await Task.Delay(50);

        Assert.False(bg.IsCompleted);
        lock (link.Commands) Assert.Empty(link.Commands);

        link.Release.SetResult();
        await batch;
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
        lock (link.Commands) Assert.Equal(5, link.Commands.Count);   // 4 batched + 1 background
    }
}
