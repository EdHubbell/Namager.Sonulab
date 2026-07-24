using Sonulab.Core.Transport;
using Xunit;

public class SerialSonuLinkBatchTests
{
    private const int Pace = 30;

    /// <summary>Builds a link whose clock the test drives. The injected delay advances the
    /// virtual clock by exactly the requested amount, so the link's poll loop makes
    /// deterministic progress with no real waiting.</summary>
    private static (SerialSonuLink link, ScriptedSerialPort port, Func<long> now) Make(
        SerialLinkOptions? options = null)
    {
        long now = 0;
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now));
        var opts = options ?? new SerialLinkOptions
        {
            PipelineMinPaceMs = Pace, PipelinePollMs = 1,
            PollMs = 1, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 100,
        };
        var link = new SerialSonuLink(port, "COM6", 115200, opts,
            tickSource: () => Volatile.Read(ref now),
            delay: (ms, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref now, Volatile.Read(ref now) + ms);
                return Task.CompletedTask;
            });
        return (link, port, () => Volatile.Read(ref now));
    }

    private static string Cmd(int chunk) => $"dread root\\presets:{{\"index\":0,\"chunk\":{chunk}}}";
    private static string[] Cmds(int count) => Enumerable.Range(1, count).Select(Cmd).ToArray();

    /// <summary>Device answers every dread with a record for the chunk it asked for.</summary>
    private static void RespondNormally(ScriptedSerialPort port) =>
        port.Responder = c =>
        {
            var chunk = c.Split("\"chunk\":")[1].TrimEnd('}');
            return $"root\\presets:{{\"index\":0,\"chunk\":{chunk},\"value\":\"aa\"}}\r\n\0";
        };

    [Fact]
    public async Task Returns_one_window_per_command_in_order()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(4));

        Assert.Equal(4, windows.Count);
        for (int i = 0; i < 4; i++) Assert.Contains($"\"chunk\":{i + 1}", windows[i]);
        Assert.Equal(4, port.Received.Count);
        Assert.Empty(port.Dropped);
    }

    [Fact]
    public async Task Never_sends_faster_than_the_pace_floor()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 1;          // device answers instantly — only the floor holds us back
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(6));

        Assert.Equal(6, port.ReceivedAt.Count);
        for (int i = 1; i < port.ReceivedAt.Count; i++)
            Assert.True(port.ReceivedAt[i] - port.ReceivedAt[i - 1] >= Pace,
                $"send {i} came {port.ReceivedAt[i] - port.ReceivedAt[i - 1]}ms after the previous — under the {Pace}ms floor");
    }

    [Fact]
    public async Task Self_clocks_on_the_first_response_byte_rather_than_the_pace_alone()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 90;         // much slower than the 30ms floor
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(4));

        // If we sent purely on the pace floor, gaps would be ~30ms. Waiting for the first byte
        // means each gap is at least the device's latency.
        for (int i = 1; i < port.ReceivedAt.Count; i++)
            Assert.True(port.ReceivedAt[i] - port.ReceivedAt[i - 1] >= 90,
                $"send {i} did not wait for the previous response to start ({port.ReceivedAt[i] - port.ReceivedAt[i - 1]}ms)");
    }

    [Fact]
    public async Task Discards_the_input_buffer_once_before_the_first_send()
    {
        // A mid-batch discard would destroy in-flight responses; lockstep SendAsync discards
        // per command, the batch must not.
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(5));

        Assert.Equal(1, port.DiscardCount);
    }

    [Fact]
    public async Task Keeps_sending_after_the_device_eats_a_command()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        port.DropWhen = c => c.Contains("\"chunk\":3");
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(5));

        Assert.Equal(new[] { 1, 2, 4, 5 }, port.Received
            .Select(c => int.Parse(c.Split("\"chunk\":")[1].TrimEnd('}'))).ToArray());
        Assert.Equal(4, windows.Count);                       // chunk 3 simply never arrives
        Assert.DoesNotContain(windows, w => w.Contains("\"chunk\":3"));
    }

    [Fact]
    public async Task Pipelining_disabled_falls_back_to_lockstep()
    {
        var (link, port, _) = Make(new SerialLinkOptions
        {
            PipelineEnabled = false,
            PollMs = 1, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 100,
        });
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(3));

        Assert.Equal(3, windows.Count);
        Assert.Equal(3, port.DiscardCount);   // SendAsync discards per command — proves the fallback ran
    }

    [Fact]
    public async Task Returns_a_short_list_when_the_device_stops_answering()
    {
        var (link, port, _) = Make();
        port.FirstByteLatencyMs = 5;
        port.Responder = c => c.Contains("\"chunk\":1")
            ? "root\\presets:{\"index\":0,\"chunk\":1,\"value\":\"aa\"}\r\n\0"
            : "";                              // silence from chunk 2 on
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(4));

        Assert.Single(windows);                // deadline reached, no hang
    }

    [Fact]
    public async Task Throws_if_the_port_is_not_open()
    {
        var (link, _, _) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendBatchAsync(Cmds(2)));
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        var (link, port, _) = Make();
        port.Responder = _ => "";              // never answers, so the loop keeps polling
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => link.SendBatchAsync(Cmds(4), cts.Token));
    }

    [Fact]
    public async Task Empty_command_list_returns_empty()
    {
        var (link, _, _) = Make();
        await link.OpenAsync();
        Assert.Empty(await link.SendBatchAsync(Array.Empty<string>()));
    }
}
