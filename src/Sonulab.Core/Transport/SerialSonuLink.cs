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
    private readonly Func<int, CancellationToken, Task> _delay;

    /// <param name="tickSource">Millisecond clock for the response-collection loops. Defaults to
    /// this instance's Stopwatch — NOT Environment.TickCount64, whose ~15.6 ms Windows resolution
    /// cannot express the 30 ms pipelining pace. Tests inject a virtual clock.</param>
    /// <param name="delay">Awaited between read polls. Defaults to Task.Delay; tests inject a
    /// function that advances the virtual clock instead of really waiting.</param>
    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate,
        SerialLinkOptions? options = null, Func<long>? tickSource = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _port = port; _portName = portName; _baud = baudRate; _options = options ?? new SerialLinkOptions();
        _tick = tickSource ?? (() => _clock.ElapsedMilliseconds);
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
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
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
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
                await _delay(_options.PollMs, ct);
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
    /// Measured ~33 ms/chunk vs ~57 lockstep.
    ///
    /// Per the interface contract, the returned windows are NOT positionally aligned with
    /// <paramref name="commands"/>; callers match responses by content.</summary>
    public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        if (commands.Count == 0) return Array.Empty<string>();
        if (!_options.PipelineEnabled || commands.Count == 1)
        {
            var seq = new List<string>(commands.Count);
            foreach (var c in commands) seq.Add(await SendAsync(c, ct));
            return seq;
        }

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
            bool paceOk = sent == 0 || now - lastSendAt >= _options.PipelineMinPaceMs;
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
            else await _delay(_options.PipelinePollMs, ct);
        }
        return windows;
    }
}
