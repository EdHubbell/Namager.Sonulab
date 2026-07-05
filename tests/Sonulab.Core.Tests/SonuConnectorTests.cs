using System.Text;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class SonuConnectorTests
{
    static SerialLinkOptions Fast => new() { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 300 };

    /// <summary>Port that ignores probes until the Nth attempt — models the ESP32 still booting.</summary>
    private sealed class BootingPort(int readyOnAttempt) : ISerialPortStream
    {
        private int _writes;
        private byte[] _pending = Array.Empty<byte>();
        public bool IsOpen { get; private set; }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;
        public int BytesToRead => _pending.Length;
        public void DiscardInBuffer() { }
        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer[0] == 0) return;                       // the NUL terminator write
            _writes++;
            _pending = _writes >= readyOnAttempt
                ? Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n\0")
                : Array.Empty<byte>();
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _pending.Length);
            Array.Copy(_pending, 0, buffer, offset, n);
            _pending = _pending[n..];
            return n;
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Connects_once_the_device_answers_even_if_early_probes_are_lost()
    {
        var options = new SerialLinkOptions
        { OpenSettleMs = 0, ProbeAttempts = 8, ProbeRetryDelayMs = 1, FirstByteTimeoutMs = 20, PollMs = 1 };
        var connector = new SonuConnector(() => new BootingPort(readyOnAttempt: 4), options);
        var link = await connector.ConnectAsync(new[] { "COMX" }, new[] { 115200 });
        Assert.NotNull(link);                                  // attempt 4 of 8 succeeded
    }

    [Fact]
    public async Task Gives_up_when_the_device_never_answers()
    {
        var options = new SerialLinkOptions
        { OpenSettleMs = 0, ProbeAttempts = 3, ProbeRetryDelayMs = 1, FirstByteTimeoutMs = 20, PollMs = 1 };
        var connector = new SonuConnector(() => new BootingPort(readyOnAttempt: 99), options);
        Assert.Null(await connector.ConnectAsync(new[] { "COMX" }, new[] { 115200 }));
    }

    // A fake that only answers the name query when opened at the "correct" baud.
    static FakeSerialPort MakePort(int answersAtBaud)
    {
        var p = new FakeSerialPort();
        p.Responder = cmd =>
            (cmd == @"read root\sys\_name" && p.OpenedBaud == answersAtBaud)
                ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "";
        return p;
    }

    [Fact] public async Task Connects_on_matching_baud()
    {
        var connector = new SonuConnector(() => MakePort(115200), Fast);
        var link = await connector.ConnectAsync(new[] { "COM6" }, new[] { 921600, 115200 });
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
    }

    [Fact] public async Task Returns_null_when_nothing_answers()
    {
        var connector = new SonuConnector(() => MakePort(115200), Fast);
        var link = await connector.ConnectAsync(new[] { "COM4", "COM5" }, new[] { 9600 });
        Assert.Null(link);
    }
}
