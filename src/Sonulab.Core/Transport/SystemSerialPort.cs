using System.IO.Ports;

namespace Sonulab.Core.Transport;

public sealed class SystemSerialPort : ISerialPortStream
{
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen ?? false;

    public void Open(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
        _port.Open();
    }

    public void Close() => _port?.Close();
    public void DiscardInBuffer() { if (_port?.IsOpen == true) _port.DiscardInBuffer(); }
    public int BytesToRead => _port?.IsOpen == true ? _port.BytesToRead : 0;
    public void Write(byte[] buffer, int offset, int count) => _port!.Write(buffer, offset, count);
    public int Read(byte[] buffer, int offset, int count) => _port!.Read(buffer, offset, count);
    public void Dispose() { _port?.Dispose(); _port = null; }
}
