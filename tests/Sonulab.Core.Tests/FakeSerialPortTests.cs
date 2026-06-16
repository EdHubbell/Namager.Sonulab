using System.Text;
using Sonulab.Core.Transport;
using Xunit;

public class FakeSerialPortTests
{
    [Fact] public void Captures_command_up_to_nul_and_enqueues_response()
    {
        var p = new FakeSerialPort { Responder = cmd => cmd == "read x" ? "x:{\"value\":1}\r\n" : "" };
        p.Open("COM9", 115200);
        var bytes = Encoding.ASCII.GetBytes("read x");
        p.Write(bytes, 0, bytes.Length);
        p.Write(new byte[] { 0 }, 0, 1);                 // terminator triggers the response
        Assert.Equal("read x", p.LastCommand);
        Assert.True(p.BytesToRead > 0);
        var buf = new byte[p.BytesToRead];
        int n = p.Read(buf, 0, buf.Length);
        Assert.Equal("x:{\"value\":1}\r\n", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact] public void DiscardInBuffer_clears_pending_input()
    {
        var p = new FakeSerialPort();
        p.Open("COM9", 115200);
        p.EnqueueResponse("junk");
        p.DiscardInBuffer();
        Assert.Equal(0, p.BytesToRead);
    }

    [Fact] public void Open_records_port_and_baud()
    {
        var p = new FakeSerialPort();
        p.Open("COM6", 115200);
        Assert.True(p.IsOpen);
        Assert.Equal("COM6", p.OpenedPort);
        Assert.Equal(115200, p.OpenedBaud);
    }
}
