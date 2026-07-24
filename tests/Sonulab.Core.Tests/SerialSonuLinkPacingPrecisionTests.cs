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
            var lastSend = sends.Where(s => s <= at).DefaultIfEmpty(long.MinValue).Max();
            if (lastSend == long.MinValue) continue;
            long floor = lastSend + Pace;
            Assert.False(at < floor && at + ms > floor,
                $"a {ms} ms wait starting at {at} ms sleeps past the pace floor at {floor} ms — " +
                $"the send was legal at {floor} but cannot happen until {at + ms}");
        }
    }

    /// <summary>INVARIANT 2 (how to wait): the DEFAULT wait must actually be able to resolve a
    /// sub-tick interval. A plain Task.Delay(4) measures ~15.5 ms on Windows; anything near that
    /// makes invariant 1 unenforceable in production no matter what the loop asks for.
    ///
    /// Real wall-clock timing, so the bound is deliberately loose: it only has to separate
    /// "genuinely short" from "a full 15.6 ms timer tick".</summary>
    [Fact]
    public async Task Default_pipeline_wait_resolves_intervals_shorter_than_a_timer_tick()
    {
        var link = new SerialSonuLink(new FakeSerialPort(), "COM6", 115200);

        await SerialSonuLink.PipelineWaitAsync(4, CancellationToken.None);   // warm up

        const int Samples = 12;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Samples; i++) await SerialSonuLink.PipelineWaitAsync(4, CancellationToken.None);
        sw.Stop();

        double mean = sw.Elapsed.TotalMilliseconds / Samples;
        Assert.True(mean < 10.0,
            $"a 4 ms pipeline wait averaged {mean:F2} ms — at or near the ~15.6 ms timer tick, " +
            "so the batch loop will keep oversleeping its pace floor");
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
