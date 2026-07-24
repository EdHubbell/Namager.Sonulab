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

    /// <summary>The batch MUST hold a TimerResolutionScope for its duration. That scope is what
    /// makes Task.Delay accurate enough to pace against a 30 ms floor; without it the loop falls
    /// back to burning a core, or (if the fallback were ever removed) to 43 ms/chunk.
    ///
    /// Nothing else catches its removal: with the scope held a plain Task.Delay is accurate, and
    /// without it the spin fallback is accurate too — so every timing assertion passes either way.
    /// Observed from inside the injected delay, which only runs while the loop is running.</summary>
    [Fact]
    public async Task Batch_holds_the_raised_timer_resolution_for_its_duration()
    {
        long now = 0;
        bool activeDuringBatch = false;
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now)) { FirstByteLatencyMs = 5 };
        port.Responder = c =>
        {
            var chunk = c.Split("\"chunk\":")[1].TrimEnd('}');
            return $"root\\presets:{{\"index\":0,\"chunk\":{chunk},\"value\":\"aa\"}}\r\n\0";
        };
        var link = new SerialSonuLink(port, "COM6", 115200,
            new SerialLinkOptions { PipelineMinPaceMs = Pace, PipelinePollMs = 3, MaxWaitMs = 2000 },
            tickSource: () => Volatile.Read(ref now),
            delay: (ms, ct) =>
            {
                if (TimerResolutionScope.IsActive) activeDuringBatch = true;
                Volatile.Write(ref now, Volatile.Read(ref now) + ms);
                return Task.CompletedTask;
            });
        await link.OpenAsync();

        await link.SendBatchAsync(Enumerable.Range(1, 3).Select(Cmd).ToArray());

        if (OperatingSystem.IsWindows())
            Assert.True(activeDuringBatch, "SendBatchAsync did not hold a TimerResolutionScope — " +
                "its pacing waits are back to the ~15.6 ms timer tick");
        Assert.Equal(0, TimerResolutionScope.ActiveCount);   // and released on the way out
    }

    /// <summary>The scope must be released even when the batch dies mid-burst. It raises a
    /// PROCESS-WIDE setting, and a real IOException from the port ("a device attached to the
    /// system is not functioning") was observed unwinding through this exact path during hardware
    /// validation — leaking there would leave the machine at a 1 ms tick indefinitely.</summary>
    [Fact]
    public async Task Timer_resolution_is_released_when_the_batch_throws()
    {
        var port = new ThrowOnSecondWritePort();
        var link = new SerialSonuLink(port, "COM6", 115200,
            new SerialLinkOptions { PipelineMinPaceMs = Pace, PipelinePollMs = 1, MaxWaitMs = 200 });
        await link.OpenAsync();

        await Assert.ThrowsAsync<IOException>(
            () => link.SendBatchAsync(Enumerable.Range(1, 4).Select(Cmd).ToArray()));

        Assert.Equal(0, TimerResolutionScope.ActiveCount);
        Assert.False(TimerResolutionScope.IsActive);
    }

    /// <summary>Port double that fails partway through a burst, the way a pedal yanked off the
    /// USB bus does.</summary>
    private sealed class ThrowOnSecondWritePort : ISerialPortStream
    {
        private int _commands;
        public bool IsOpen { get; private set; }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;
        public void DiscardInBuffer() { }
        public int BytesToRead => 0;
        public int Read(byte[] buffer, int offset, int count) => 0;
        public void Write(byte[] buffer, int offset, int count)
        {
            // Count whole commands: each is written as payload then a NUL terminator.
            if (count == 1 && buffer[offset] == 0 && ++_commands >= 2)
                throw new IOException("A device attached to the system is not functioning. : 'COM6'.");
        }
        public void Dispose() { }
    }

    /// <summary>Companion to the fallback test above: under a held scope the wait takes the
    /// production path (plain Task.Delay at a 1 ms tick) and must still resolve a sub-tick
    /// interval. Without this, only the fallback is covered and the shipped path is not.</summary>
    [Fact]
    public async Task Pipeline_wait_is_accurate_on_the_production_path_under_a_held_scope()
    {
        using var scope = TimerResolutionScope.Acquire();
        const int Samples = 12, Requested = 4;

        await SerialSonuLink.PipelineWaitAsync(Requested, CancellationToken.None);   // warm up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Samples; i++) await SerialSonuLink.PipelineWaitAsync(Requested, CancellationToken.None);
        double mean = sw.Elapsed.TotalMilliseconds / Samples;

        // Only meaningful where the OS actually granted the raised resolution.
        if (!TimerResolutionScope.IsActive) return;
        Assert.True(mean < 12.0,
            $"a {Requested} ms wait averaged {mean:F2} ms with the scope held — the raised timer " +
            "resolution is not taking effect, so the batch loop will oversleep its pace floor");
    }

    /// <summary>End-to-end pacing: a real batch, real clock, no injection. Four sends at a 30 ms
    /// floor cannot beat ~93 ms, and must not drift far past it.
    ///
    /// Deliberately generous: this is wall-clock timing and a loaded CI box measured 134 ms and
    /// 618 ms on an earlier, tighter bound. It is a smoke test that pacing is in the right
    /// ballpark, not a precision instrument — the precision claims live in the tests above.</summary>
    [Fact]
    public async Task Pipelined_batch_paces_close_to_its_floor_end_to_end()
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

        await link.SendBatchAsync(new[] { Cmd(1) });                    // warm up JIT and the port
        var sw = Stopwatch.StartNew();
        var windows = await link.SendBatchAsync(Enumerable.Range(1, 4).Select(Cmd).ToArray());
        sw.Stop();

        Assert.Equal(4, windows.Count);
        // 3 paced gaps at ~31 ms = ~93 ms ideal; the floor makes anything faster impossible.
        Assert.True(sw.Elapsed.TotalMilliseconds >= 90,
            $"4 paced sends took only {sw.Elapsed.TotalMilliseconds:F0} ms — faster than the " +
            $"{Pace} ms floor allows, so the floor is not being honoured");
        Assert.True(sw.Elapsed.TotalMilliseconds < 200,
            $"4 paced sends took {sw.Elapsed.TotalMilliseconds:F0} ms against a ~93 ms ideal — " +
            "pacing has drifted well past its floor");
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
