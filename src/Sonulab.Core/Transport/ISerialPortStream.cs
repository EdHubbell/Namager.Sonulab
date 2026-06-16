namespace Sonulab.Core.Transport;

public interface ISerialPortStream : IDisposable
{
    bool IsOpen { get; }
    void Open(string portName, int baudRate);
    void Close();
    void DiscardInBuffer();
    int BytesToRead { get; }
    void Write(byte[] buffer, int offset, int count);
    int Read(byte[] buffer, int offset, int count);
}
