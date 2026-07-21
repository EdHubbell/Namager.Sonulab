using System.Diagnostics;
using System.Text;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Wifi;

/// <summary>ISonuLink over a persistent TCP socket (port 8080 on the pedal). Same bytes as
/// serial: command + NUL out, response collected until the device's NUL terminator.
/// No ESP32 reset on connect (that's a serial DTR/RTS artifact) — no settle delay; the
/// first-command-empty quirk is handled by the caller's probe retry (PROTOCOL.md §WiFi/TCP).</summary>
public sealed class TcpSonuLink : ISonuLink
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private static readonly byte[] Nul = { 0 };
    private readonly ITcpConn _conn;
    private readonly string _host;
    private readonly int _port;
    private readonly TcpLinkOptions _options;

    /// <summary>Responses the device still owes us. The pedal ACKs EVERY command, but during flash
    /// work (rename dwrite, save copy) the ACK can arrive after <see cref="TcpLinkOptions.FirstByteTimeoutMs"/>;
    /// abandoning it must not desync the stream (live wire capture 2026-07-21: a late rename ACK was
    /// returned as the next read's response — "Reorder verify failed", stranded __sstmp names). Each
    /// abandoned command increments this; every stale NUL-terminated response consumed (in the pre-send
    /// drain or the read loop) decrements it.</summary>
    private int _pending;

    public TcpSonuLink(ITcpConn conn, string host, int port, TcpLinkOptions? options = null)
    {
        _conn = conn; _host = host; _port = port; _options = options ?? new TcpLinkOptions();
    }

    public bool IsOpen => _conn.Connected;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ConnectTimeoutMs);
        await _conn.ConnectAsync(_host, _port, timeout.Token);
    }

    public void Close() => _conn.Close();

    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!_conn.Connected) throw new InvalidOperationException("TCP link is not open.");

        // Drain stale bytes (serial: DiscardInBuffer), settling response debt: every NUL discarded
        // here is an owed late response that already arrived.
        DrainAvailable();

        // Still owed a response? Give it a bounded chance to arrive BEFORE we send, so it doesn't
        // interleave with the new command's response. Exits early once the debt is settled and the
        // pipe is empty, or after ResyncQuietMs of silence (the read loop's skip covers stragglers).
        if (_pending > 0)
        {
            Log.Warn("resync: {0} late response(s) outstanding before '{1}' — absorbing", _pending, command);
            var rsw = Stopwatch.StartNew();
            long lastData = 0;
            while (rsw.ElapsedMilliseconds < _options.MaxWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                if (_conn.Available > 0) { DrainAvailable(); lastData = rsw.ElapsedMilliseconds; }
                else if (_pending == 0) break;
                else if (rsw.ElapsedMilliseconds - lastData >= _options.ResyncQuietMs) break;
                else await Task.Delay(_options.PollMs, ct);
            }
        }

        var bytes = Encoding.ASCII.GetBytes(command);
        await _conn.SendAsync(bytes, ct);
        await _conn.SendAsync(Nul, ct);

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        bool sawData = false, sawNul = false;

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _conn.Available;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _conn.Receive(buf);
                sawData = true;
                int start = 0;
                while (start < n)
                {
                    int nul = Array.IndexOf(buf, (byte)0, start, n - start);
                    if (nul < 0) { sb.Append(Encoding.ASCII.GetString(buf, start, n - start)); break; }
                    sb.Append(Encoding.ASCII.GetString(buf, start, nul - start));
                    start = nul + 1;
                    if (_pending > 0)
                    {
                        // A NUL while a response is owed terminates the PREVIOUS command's late
                        // response — consume it and keep reading for ours.
                        _pending--;
                        Log.Warn("resync: consumed a late response ({0} chars) during '{1}'", sb.Length, command);
                        sb.Clear();
                        continue;
                    }
                    sawNul = true;   // OUR terminator — the device NUL-terminates every response
                    break;
                }
                if (sawNul) break;
            }
            else
            {
                // The device NUL-terminates every response — that terminator is the authoritative
                // end-of-response. Do NOT treat a mid-response idle gap as end-of-data: over TCP a
                // large multi-record response can arrive in bursts with long gaps, and breaking on a
                // gap truncated it (field crash "Slot N is empty"). Only the no-data case
                // short-circuits; MaxWait remains the backstop.
                if (!sawData && sw.ElapsedMilliseconds >= _options.FirstByteTimeoutMs) break;
                await Task.Delay(_options.PollMs, ct);
            }
        }

        if (!sawNul)
        {
            // Our response didn't (fully) arrive — the device now owes us one more. Record the debt
            // so the late arrival is consumed instead of desyncing the next command, and log it: a
            // silent short read is how both field failures presented.
            _pending++;
            if (sawData)
                Log.Warn("TCP response for '{0}' had no NUL terminator within {1}ms ({2} chars) — likely truncated ({3}:{4})",
                    command, sw.ElapsedMilliseconds, sb.Length, _host, _port);
            else
                Log.Debug("no response to '{0}' within {1}ms — response now owed ({2} outstanding)",
                    command, sw.ElapsedMilliseconds, _pending);
        }
        return sb.ToString();
    }

    /// <summary>Discard everything currently readable, decrementing <see cref="_pending"/> for each
    /// NUL terminator seen (each one closes an owed late response that arrived while idle).</summary>
    private void DrainAvailable()
    {
        var drain = new byte[4096];
        while (_conn.Available > 0)
        {
            int n = _conn.Receive(drain);
            for (int i = 0; i < n; i++)
                if (drain[i] == 0 && _pending > 0) _pending--;
        }
    }
}
