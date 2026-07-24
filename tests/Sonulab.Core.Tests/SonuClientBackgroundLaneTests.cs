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
}
