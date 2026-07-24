using System.Diagnostics;
using System.Text;

namespace Sonulab.Core.Transport;

public sealed class SerialSonuLink : ISonuLink
{
    private static readonly byte[] Nul = { 0 };
    private readonly ISerialPortStream _port;
    private readonly string _portName;
    private readonly int _baud;
    private readonly SerialLinkOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _tick;
    private readonly Func<int, CancellationToken, Task>? _injectedDelay;

    /// <summary>Windows' default timer resolution. Task.Delay cannot resolve below it — measured
    /// ~15.5 ms for EVERY requested value from 1 to 10 ms on the validation bench.</summary>
    private const int TimerTickMs = 16;

    /// <param name="tickSource">Millisecond clock for the response-collection loops. Defaults to
    /// this instance's Stopwatch — NOT Environment.TickCount64, whose ~15.6 ms Windows resolution
    /// cannot express the 30 ms pipelining pace. Tests inject a virtual clock.</param>
    /// <param name="delay">Awaited between read polls, on BOTH the lockstep and pipelined paths.
    /// Tests inject a function that advances the virtual clock instead of really waiting. When it
    /// is not supplied the two paths take different defaults: lockstep waits for a NUL and is
    /// happy with plain Task.Delay, while the pipelined loop paces against a 30 ms floor and goes
    /// through PipelineWaitAsync. On Windows both end up on Task.Delay (the batch raises the timer
    /// resolution first); the split is what keeps the busy-wait FALLBACK off the lockstep path on
    /// hosts where the resolution cannot be raised.</param>
    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate,
        SerialLinkOptions? options = null, Func<long>? tickSource = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _port = port; _portName = portName; _baud = baudRate; _options = options ?? new SerialLinkOptions();
        _tick = tickSource ?? (() => _clock.ElapsedMilliseconds);
        _injectedDelay = delay;
    }

    private Task DelayAsync(int ms, CancellationToken ct) =>
        _injectedDelay?.Invoke(ms, ct) ?? Task.Delay(ms, ct);

    private Task PipelineDelayAsync(int ms, CancellationToken ct) =>
        _injectedDelay?.Invoke(ms, ct) ?? PipelineWaitAsync(ms, ct);

    /// <summary>Accurate short wait for the pacing loop, and the reason the pipelined path does
    /// not simply use Task.Delay.
    ///
    /// Hardware validation (2026-07-24) measured 43.3 ms/chunk against a 30 ms floor while a
    /// busy-spin probe reached 33.4 ms/chunk on the same device in the same run. The cause was
    /// not the pace and not the self-clock gate (instrumentation showed the gate binding on 1 of
    /// 63 sends): a chunk response stops streaming a few ms BEFORE the floor opens, so the loop
    /// slept one full ~15.6 ms timer tick and woke well past the moment it could have sent.
    ///
    /// The fix is to remove the constraint rather than work around it: SendBatchAsync holds a
    /// <see cref="TimerResolutionScope"/> for the burst, which puts the scheduler tick at 1 ms, and
    /// then a plain Task.Delay is accurate (3.57 ms for a 3 ms request, against 15.61 without) at
    /// idle CPU. Where that is unavailable — non-Windows, or winmm refusing — this falls back to
    /// sleeping coarsely and spinning the tail: equally accurate, but it burns a core, so callers
    /// must keep it off a UI thread (see SonuClient.SendBatchGatedAsync, which hops to the pool).</summary>
    internal static async Task PipelineWaitAsync(int ms, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ms <= 0) return;

        // Fast path: a TimerResolutionScope is holding the scheduler tick at 1 ms, so Task.Delay
        // is accurate to about a millisecond (measured 3.57 ms for a 3 ms request, against 15.61
        // without) and no busy-wait is warranted.
        if (TimerResolutionScope.IsActive) { await Task.Delay(ms, ct); return; }

        // Fallback for a non-Windows host, or a winmm that would not load: sleep for whatever is
        // comfortably more than one tick, then spin the remainder. Accurate, but it burns a core.
        var sw = Stopwatch.StartNew();
        int coarse = ms - TimerTickMs;
        if (coarse > 0) await Task.Delay(coarse, ct);
        while (sw.Elapsed.TotalMilliseconds < ms)
        {
            ct.ThrowIfCancellationRequested();
            Thread.SpinWait(100);
        }
    }

    public bool IsOpen => _port.IsOpen;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        _port.Open(_portName, _baud);
        if (_options.OpenSettleMs > 0) await Task.Delay(_options.OpenSettleMs, ct); // ESP32 reboots on DTR/RTS at open
    }

    public void Close() => _port.Close();

    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        // The precondition check stays OUTSIDE the try: IsFatal matches InvalidOperationException
        // (SerialPort raises it on a closed handle), so leaving this inside would silently
        // reclassify a caller bug as a device disconnect.
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        try { return await SendCoreAsync(command, ct); }
        catch (Exception ex) when (DeviceDisconnectedException.IsFatal(ex)) { throw Fault(ex); }
    }

    private async Task<string> SendCoreAsync(string command, CancellationToken ct)
    {
        _port.DiscardInBuffer();
        var bytes = Encoding.ASCII.GetBytes(command);
        _port.Write(bytes, 0, bytes.Length);
        _port.Write(Nul, 0, 1);

        var sb = new StringBuilder();
        long start = _tick();
        long lastData = 0;
        bool sawData = false;

        while (_tick() - start < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _port.Read(buf, 0, avail);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = _tick() - start;
                // Device terminates each response with a NUL byte — stop as soon as we see it
                // (deterministic and size-independent; the idle gap below is only a fallback).
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) break;
            }
            else
            {
                if (sawData && _tick() - start - lastData >= _options.IdleGapMs) break;
                // No-response command (e.g. a write): if nothing has arrived by the first-byte
                // timeout, stop instead of blocking the full MaxWaitMs.
                if (!sawData && _tick() - start >= _options.FirstByteTimeoutMs) break;
                await DelayAsync(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }

    /// <summary>Paced-overlap pipelining (PROTOCOL.md "dread limits &amp; hazards"): the firmware
    /// drops zero-gap command bursts, but it DOES accept the next command while still streaming
    /// the previous response. So we self-clock — send N+1 once ANY response byte has arrived
    /// since send N. That byte need not be response N's own first byte; it may be the tail of an
    /// EARLIER response still streaming in, and that is just as valid a signal that the device is
    /// mid-transmission and listening again. PipelineMinPaceMs is a hard floor regardless (30 ms
    /// proven; 25 ms is the cliff), and FirstByteTimeoutMs is the stall escape — if a command was
    /// eaten, no byte will ever arrive and the batch must keep moving instead of hanging on it.
    /// Measured 35.1 ms/chunk through SonuClient vs ~57 lockstep (raw achievable: 33.4).
    ///
    /// Per the interface contract, the returned windows are NOT positionally aligned with
    /// <paramref name="commands"/>; callers match responses by content.</summary>
    public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        if (commands.Count == 0) return Array.Empty<string>();
        try { return await SendBatchCoreAsync(commands, ct); }
        catch (Exception ex) when (DeviceDisconnectedException.IsFatal(ex)) { throw Fault(ex); }
    }

    private async Task<IReadOnlyList<string>> SendBatchCoreAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        if (!_options.PipelineEnabled || commands.Count == 1)
        {
            var seq = new List<string>(commands.Count);
            foreach (var c in commands) seq.Add(await SendAsync(c, ct));
            return seq;
        }

        // Hold the scheduler tick at 1 ms for the burst so the pacing waits below are accurate
        // without a busy-wait. Scoped to this batch only — it is a global setting. Inert on a
        // non-Windows host, where PipelineWaitAsync falls back to sleep-then-spin.
        using var timerResolution = TimerResolutionScope.Acquire();

        // ONE discard, before the first send. A mid-batch discard would destroy responses that
        // are still in flight — which is exactly what pipelining creates.
        _port.DiscardInBuffer();

        var windows = new List<string>(commands.Count);
        var pending = new StringBuilder();          // bytes accumulated since the last NUL
        var buf = new byte[4096];
        int sent = 0;
        long lastSendAt = 0;
        bool sawByteSinceSend = false;
        long start = _tick();
        // Budget for the OVERLAPPED case (MaxWaitMs slack + one pace interval per command), not a
        // worst case: if the device does not honor overlap it self-clocks at the lockstep rate
        // (~57 ms/chunk), and for the largest read in the app (a 96-chunk amp slot) ideal lockstep
        // time (~5472 ms) exceeds this deadline (~5380 ms) — the tail chunks would time out here
        // and fall through to the per-chunk repair pass below. Still correct, just slower than
        // plain lockstep would have been.
        long deadline = _options.MaxWaitMs + (long)_options.PipelineMinPaceMs * commands.Count;

        while (_tick() - start < deadline && (sent < commands.Count || windows.Count < commands.Count))
        {
            ct.ThrowIfCancellationRequested();

            long now = _tick();
            // STRICTLY greater: _tick() truncates to whole ms, so ">= 30" admits a true interval
            // of just over 29 ms — measured on ~5% of sends. The floor is a hardware constant
            // (25 ms is where the firmware starts eating commands), so it should mean what it says.
            bool paceOk = sent == 0 || now - lastSendAt > _options.PipelineMinPaceMs;
            // Self-clock: a response byte has arrived since the last send, so the device is
            // mid-transmission and listening again. That byte may be the tail of an EARLIER
            // response still streaming in rather than this response's own first byte — either
            // way it's a valid signal. FirstByteTimeoutMs is the escape hatch — if the device ate
            // the previous command, nothing will ever arrive and the batch must not stall on it.
            bool previousMoving = sent == 0 || sawByteSinceSend
                                  || now - lastSendAt >= _options.FirstByteTimeoutMs;
            if (sent < commands.Count && paceOk && previousMoving)
            {
                var bytes = Encoding.ASCII.GetBytes(commands[sent]);
                _port.Write(bytes, 0, bytes.Length);
                _port.Write(Nul, 0, 1);
                sent++;
                lastSendAt = _tick();
                sawByteSinceSend = false;
                continue;
            }

            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                int n = _port.Read(buf, 0, Math.Min(avail, buf.Length));
                sawByteSinceSend = true;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == 0) { windows.Add(pending.ToString()); pending.Clear(); }
                    else pending.Append((char)buf[i]);
                }
            }
            else
            {
                // Wait for exactly the instant the next send becomes legal — the WHOLE remaining
                // interval, not a fixed poll tick. Two reasons:
                //   * never sleep PAST it (a fixed 3 ms poll straddles the floor and overshoots by
                //     a full ~15.6 ms timer tick — the 43.3 ms/chunk defect), and
                //   * never poll THROUGH it: asking for 3 ms at a time made the fallback path spin
                //     the entire inter-send gap (~100% of a core, measured). Asking for the whole
                //     interval lets it sleep for the bulk and spin only the last stretch.
                // Not draining the port meanwhile is safe: a chunk response is ~340 B against a
                // 4 KB driver buffer, and we drain on the very next iteration after waking.
                int wait = _options.PipelinePollMs;
                if (sent > 0 && sent < commands.Count)
                {
                    // +1 because paceOk is strictly greater on a truncating ms clock.
                    long untilPace = _options.PipelineMinPaceMs + 1 - (_tick() - lastSendAt);
                    if (untilPace > 0) wait = (int)untilPace;
                }
                await PipelineDelayAsync(Math.Max(1, wait), ct);
            }
        }
        return windows;
    }

    /// <summary>Close the port and translate. Closing is what stops IsOpen from lying — a real
    /// SerialPort reports IsOpen == true after an unplug until someone closes it.</summary>
    private DeviceDisconnectedException Fault(Exception inner)
    {
        try { _port.Close(); } catch { /* already gone — the throw below is the real signal */ }
        return new DeviceDisconnectedException("USB", inner);
    }
}
