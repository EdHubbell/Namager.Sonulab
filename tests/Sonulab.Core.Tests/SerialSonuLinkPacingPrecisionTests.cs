using System.Diagnostics;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Regression tests for the pacing-precision defect found during hardware validation
/// (2026-07-24). The batch loop achieved 43.3 ms/chunk against a 30 ms floor while a busy-spin
/// probe hit 33.4 ms/chunk on the same device in the same run. Root cause: the loop only
/// re-evaluates "may I send?" when its poll sleep returns, and Task.Delay cannot resolve below
/// the Windows timer tick (~15.6 ms measured, for EVERY requested value from 1 to 10 ms). The
/// response stops streaming a few ms before the floor opens, so the loop sleeps a full tick and
/// wakes ~13 ms past the moment it could have sent.
///
/// Two invariants, one per half of the fix.</summary>
[Collection(TimingSensitive.Name)]
public class SerialSonuLinkPacingPrecisionTests
{
    private const int Pace = 30;

    private static string Cmd(int chunk) => $"dread root\\presets:{{\"index\":0,\"chunk\":{chunk}}}";

    /// <summary>INVARIANT 1 (what to wait for): a poll wait must never straddle the instant the
    /// pace floor opens. Sleeping from 29 ms to 32 ms across a 30 ms floor is the defect — the
    /// send was legal at 30 and did not happen until 32.
    ///
    /// This is asserted at the seam (what the loop ASKS for) rather than on elapsed time,
    /// because the injected virtual clock advances by exactly what is requested and so cannot
    /// reproduce the real timer's overshoot. Invariant 2 covers the accuracy half.</summary>
    [Fact]
    public async Task Poll_wait_never_straddles_the_moment_the_pace_floor_opens()
    {
        long now = 0;
        var requests = new List<(long At, int Ms)>();
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now)) { FirstByteLatencyMs = 5 };
        port.Responder = c =>
        {
            var chunk = c.Split("\"chunk\":")[1].TrimEnd('}');
            return $"root\\presets:{{\"index\":0,\"chunk\":{chunk},\"value\":\"aa\"}}\r\n\0";
        };

        var link = new SerialSonuLink(port, "COM6", 115200,
            // A poll interval that deliberately does NOT divide the 30 ms floor. With a divisor
            // (the default 3) the poll grid lands exactly on the floor by luck and the straddle
            // can never occur — this test passes without the fix. Production is the misaligned
            // case: the grid is anchored by when the response stops streaming, not by the send.
            new SerialLinkOptions { PipelineMinPaceMs = Pace, PipelinePollMs = 4, MaxWaitMs = 2000, FirstByteTimeoutMs = 300 },
            tickSource: () => Volatile.Read(ref now),
            delay: (ms, ct) =>
            {
                requests.Add((Volatile.Read(ref now), ms));
                Volatile.Write(ref now, Volatile.Read(ref now) + ms);
                return Task.CompletedTask;
            });
        await link.OpenAsync();

        await link.SendBatchAsync(Enumerable.Range(1, 6).Select(Cmd).ToArray());

        var sends = port.ReceivedAt;
        Assert.True(sends.Count >= 2, "need at least two sends to have a pace floor to straddle");

        foreach (var (at, ms) in requests)
        {
            // The floor that applies to this wait is set by the most recent send before it.
            // +1: the loop's pace check is strictly greater, because the ms clock truncates.
            var lastSend = sends.Where(s => s <= at).DefaultIfEmpty(long.MinValue).Max();
            if (lastSend == long.MinValue) continue;
            long floor = lastSend + Pace + 1;
            Assert.False(at < floor && at + ms > floor,
                $"a {ms} ms wait starting at {at} ms sleeps past the pace floor at {floor} ms — " +
                $"the send was legal at {floor} but cannot happen until {at + ms}");
        }
    }

    /// <summary>INVARIANT 2 (how to wait): the DEFAULT wait must actually be able to resolve a
    /// sub-tick interval. A plain Task.Delay(4) measures ~15.5 ms on Windows; anything near that
    /// makes invariant 1 unenforceable in production no matter what the loop asks for.
    ///
    /// Real wall-clock timing, so the comparison is SELF-CALIBRATING rather than a fixed bound:
    /// it measures Task.Delay(4) in the same run and requires the pipeline wait to be markedly
    /// faster. A fixed "under 10 ms" bound fails about half the time on a 2x-oversubscribed
    /// machine, and xUnit runs collections in parallel — but under that same load Task.Delay
    /// degrades too, so the RATIO holds where an absolute number would not.</summary>
    [Fact]
    public async Task Default_pipeline_wait_resolves_intervals_shorter_than_a_timer_tick()
    {
        const int Samples = 12;
        const int Requested = 4;

        await SerialSonuLink.PipelineWaitAsync(Requested, CancellationToken.None);   // warm up
        await Task.Delay(Requested);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Samples; i++) await SerialSonuLink.PipelineWaitAsync(Requested, CancellationToken.None);
        double precise = sw.Elapsed.TotalMilliseconds / Samples;

        sw.Restart();
        for (int i = 0; i < Samples; i++) await Task.Delay(Requested);
        double coarse = sw.Elapsed.TotalMilliseconds / Samples;

        Assert.True(precise < coarse * 0.75,
            $"a {Requested} ms pipeline wait averaged {precise:F2} ms against Task.Delay's {coarse:F2} ms — " +
            "it is not resolving below the timer tick, so the batch loop will oversleep its pace floor");
    }

    /// <summary>Wiring check: with NO delay injected, the pipelined path must take the precise
    /// wait and the lockstep path must not. Every other batch test injects a delay, so without
    /// this one the defaults could be swapped back to Task.Delay and the whole suite would still
    /// pass while the device ran 30% slower.
    ///
    /// Asserted by timing a real batch against a port that answers instantly: 4 sends at a 30 ms
    /// floor take ~120 ms with a precise wait, but ~15.6 ms of oversleep per send with a coarse
    /// one. The midpoint separates them with room to spare.</summary>
    [Fact]
    public async Task Pipelined_path_uses_the_precise_wait_when_no_delay_is_injected()
    {
        var port = new FakeSerialPort
        {
            Responder = c =>
            {
                var chunk = c.Split("\"chunk\":")[1].TrimEnd('}');
                return $"root\\presets:{{\"index\":0,\"chunk\":{chunk},\"value\":\"aa\"}}\r\n\0";
            }
        };
        // Real clock, real waits — no tickSource, no delay.
        var link = new SerialSonuLink(port, "COM6", 115200,
            new SerialLinkOptions { PipelineMinPaceMs = Pace, PipelinePollMs = 3, MaxWaitMs = 2000 });
        await link.OpenAsync();

        var sw = Stopwatch.StartNew();
        var windows = await link.SendBatchAsync(Enumerable.Range(1, 4).Select(Cmd).ToArray());
        sw.Stop();

        Assert.Equal(4, windows.Count);
        // 3 paced gaps at ~31 ms = ~93 ms ideal. Coarse waiting adds a tick per gap (~140 ms+).
        Assert.True(sw.Elapsed.TotalMilliseconds < 125,
            $"4 paced sends took {sw.Elapsed.TotalMilliseconds:F0} ms — the pipelined path is not " +
            "using the precise wait by default (a full timer tick is being lost per send)");
    }

    /// <summary>The wait must still honor cancellation — it is on the path that a user's
    /// cancelled bulk read unwinds through.</summary>
    [Fact]
    public async Task Pipeline_wait_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SerialSonuLink.PipelineWaitAsync(50, cts.Token));
    }
}
