using Sonulab.Transport.Wifi;

namespace Sonulab.Transport.Wifi.Tests;

/// <summary>Scripted TCP connection. Sends are recorded; when a NUL-terminated command has
/// accumulated, RespondWith (if set) queues response bytes for the Available/Receive pull side.</summary>
public sealed class FakeTcpConn : ITcpConn
{
    private readonly MemoryStream _pendingCmd = new();
    private readonly Queue<byte> _rx = new();
    public List<byte[]> Sends { get; } = new();
    public (string Host, int Port)? ConnectedTo { get; private set; }
    public bool Connected { get; private set; }
    public bool FailConnect { get; set; }
    public Func<string, byte[]>? RespondWith;   // command (no NUL) -> raw response bytes

    public int Available { get { lock (_rx) return _rx.Count; } }

    public Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        if (FailConnect) throw new InvalidOperationException("connect refused");
        Connected = true; ConnectedTo = (host, port);
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        Sends.Add(data);
        _pendingCmd.Write(data, 0, data.Length);
        var all = _pendingCmd.ToArray();
        int nul = Array.IndexOf(all, (byte)0);
        if (nul >= 0)
        {
            _pendingCmd.SetLength(0);
            var cmd = System.Text.Encoding.ASCII.GetString(all, 0, nul);
            if (RespondWith is not null) Feed(RespondWith(cmd));
        }
        return Task.CompletedTask;
    }

    public int Receive(byte[] buffer)
    {
        lock (_rx)
        {
            int n = Math.Min(buffer.Length, _rx.Count);
            for (int i = 0; i < n; i++) buffer[i] = _rx.Dequeue();
            return n;
        }
    }

    public void Close() => Connected = false;

    public void Feed(byte[] data) { lock (_rx) foreach (var b in data) _rx.Enqueue(b); }
    public void Feed(string ascii) => Feed(System.Text.Encoding.ASCII.GetBytes(ascii));
}
