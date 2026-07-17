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
    private static readonly byte[] Nul = { 0 };
    private readonly ITcpConn _conn;
    private readonly string _host;
    private readonly int _port;
    private readonly TcpLinkOptions _options;

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

        // Drain stale bytes (serial: DiscardInBuffer). TCP has no meter stream, but a prior
        // command's tail could linger after a timeout.
        var drain = new byte[4096];
        while (_conn.Available > 0) _conn.Receive(drain);

        var bytes = Encoding.ASCII.GetBytes(command);
        await _conn.SendAsync(bytes, ct);
        await _conn.SendAsync(Nul, ct);

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        long lastData = 0;
        bool sawData = false;

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _conn.Available;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _conn.Receive(buf);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = sw.ElapsedMilliseconds;
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) break;   // device NUL-terminates responses
            }
            else
            {
                if (sawData && sw.ElapsedMilliseconds - lastData >= _options.IdleGapMs) break;
                if (!sawData && sw.ElapsedMilliseconds >= _options.FirstByteTimeoutMs) break;
                await Task.Delay(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }
}
