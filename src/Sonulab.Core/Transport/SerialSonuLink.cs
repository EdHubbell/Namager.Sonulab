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

    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate, SerialLinkOptions? options = null)
    {
        _port = port; _portName = portName; _baud = baudRate; _options = options ?? new SerialLinkOptions();
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
        var sw = Stopwatch.StartNew();
        long lastData = 0;
        bool sawData = false;

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _port.Read(buf, 0, avail);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = sw.ElapsedMilliseconds;
                // Device terminates each response with a NUL byte — stop as soon as we see it
                // (deterministic and size-independent; the idle gap below is only a fallback).
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) break;
            }
            else
            {
                if (sawData && sw.ElapsedMilliseconds - lastData >= _options.IdleGapMs) break;
                await Task.Delay(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }
}
