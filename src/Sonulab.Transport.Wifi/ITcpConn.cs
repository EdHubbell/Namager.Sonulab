namespace Sonulab.Transport.Wifi;

/// <summary>Seam over the OS TCP socket (mirrors ISerialPortStream's pull model:
/// Available + Receive) so TcpSonuLink is unit-testable with a fake.</summary>
public interface ITcpConn
{
    bool Connected { get; }
    int Available { get; }
    Task ConnectAsync(string host, int port, CancellationToken ct = default);
    Task SendAsync(byte[] data, CancellationToken ct = default);
    /// <summary>Read up to buffer.Length of the currently Available bytes; returns count read.</summary>
    int Receive(byte[] buffer);
    void Close();
}
