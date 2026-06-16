using System.Text;

namespace Sonulab.Core.Transport;

public sealed class FakeSerialPort : ISerialPortStream
{
    private readonly Queue<byte> _in = new();
    private readonly List<byte> _cmdBuf = new();

    public bool IsOpen { get; private set; }
    public string? OpenedPort { get; private set; }
    public int OpenedBaud { get; private set; }
    public string? LastCommand { get; private set; }

    /// <summary>Maps a fully-received command (NUL stripped) to the response text to enqueue. Return "" for no response.</summary>
    public Func<string, string>? Responder { get; set; }

    public void Open(string portName, int baudRate) { IsOpen = true; OpenedPort = portName; OpenedBaud = baudRate; }
    public void Close() => IsOpen = false;
    public void DiscardInBuffer() => _in.Clear();
    public int BytesToRead => _in.Count;

    public void EnqueueResponse(string text)
    {
        foreach (var b in Encoding.ASCII.GetBytes(text)) _in.Enqueue(b);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = buffer[offset + i];
            if (b == 0)
            {
                LastCommand = Encoding.ASCII.GetString(_cmdBuf.ToArray());
                _cmdBuf.Clear();
                var resp = Responder?.Invoke(LastCommand);
                if (!string.IsNullOrEmpty(resp)) EnqueueResponse(resp);
            }
            else _cmdBuf.Add(b);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int i = 0;
        while (i < count && _in.Count > 0) buffer[offset + i++] = _in.Dequeue();
        return i;
    }

    public void Dispose() { }
}
