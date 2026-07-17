using System.Net.Sockets;

namespace Sonulab.Transport.Wifi;

/// <summary>Real-socket ITcpConn. Deliberately thin — logic lives in TcpSonuLink.
/// Not unit-tested (live checks via HwCheck --wifi).</summary>
public sealed class SystemTcpConn : ITcpConn
{
    private TcpClient? _client;

    public bool Connected => _client?.Connected == true;
    public int Available => _client?.Available ?? 0;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var c = _client ?? throw new InvalidOperationException("TCP connection is not open.");
        return c.GetStream().WriteAsync(data, 0, data.Length, ct);
    }

    public int Receive(byte[] buffer)
    {
        var c = _client ?? throw new InvalidOperationException("TCP connection is not open.");
        return c.GetStream().Read(buffer, 0, Math.Min(buffer.Length, c.Available));
    }

    public void Close() { _client?.Close(); _client = null; }
}
