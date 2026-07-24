using System.Text;
using Xunit;

public class ScriptedSerialPortTests
{
    private static (ScriptedSerialPort port, Action<long> advance) Make()
    {
        long now = 0;
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now));
        return (port, ms => Volatile.Write(ref now, Volatile.Read(ref now) + ms));
    }

    private static void Send(ScriptedSerialPort port, string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\0");
        port.Write(bytes, 0, bytes.Length);
    }

    private static string Drain(ScriptedSerialPort port)
    {
        var buf = new byte[4096];
        int n = port.Read(buf, 0, buf.Length);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    [Fact]
    public void Response_is_invisible_until_the_first_byte_latency_has_elapsed()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 20;
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "dread 1");
        Assert.Equal(0, port.BytesToRead);   // t=0
        advance(19);
        Assert.Equal(0, port.BytesToRead);
        advance(1);                          // t=20
        Assert.Equal("ok:dread 1\0", Drain(port));
    }

    [Fact]
    public void A_command_sent_inside_the_drop_window_is_eaten_and_never_answered()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.DropIfSentWithinMs = 25;        // the firmware's real cliff
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "first");
        advance(10);                         // too soon
        Send(port, "second");
        advance(1000);

        Assert.Equal(new[] { "first" }, port.Received);
        Assert.Equal(new[] { "second" }, port.Dropped);
        Assert.Equal("ok:first\0", Drain(port));
    }

    [Fact]
    public void DropWhen_targets_a_specific_command()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.DropWhen = c => c.Contains("chunk3");
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "chunk2");
        Send(port, "chunk3");
        Send(port, "chunk4");
        advance(10);

        Assert.Equal(new[] { "chunk2", "chunk4" }, port.Received);
        Assert.Equal(new[] { "chunk3" }, port.Dropped);
        Assert.Equal("ok:chunk2\0ok:chunk4\0", Drain(port));
    }

    [Fact]
    public void Fragmented_delivery_preserves_byte_order_across_clock_advances()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.FragmentIntervalMs = 5;
        port.FragmentSize = 3;
        port.Responder = _ => "abcdefgh";
        port.Open("COM6", 115200);

        Send(port, "x");
        advance(0);
        Assert.Equal("abc", Drain(port));
        advance(5);
        Assert.Equal("def", Drain(port));
        advance(5);
        Assert.Equal("gh", Drain(port));
    }

    [Fact]
    public void DiscardInBuffer_drops_both_ready_and_scheduled_bytes()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 10;
        port.Responder = _ => "payload\0";
        port.Open("COM6", 115200);

        Send(port, "x");
        port.DiscardInBuffer();
        advance(1000);

        Assert.Equal(0, port.BytesToRead);
        Assert.Equal(1, port.DiscardCount);
    }
}
