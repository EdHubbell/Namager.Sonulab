using System.IO;
using Sonulab.Core.Transport;
using Xunit;

public class SerialSonuLinkDisconnectTests
{
    static SerialLinkOptions Fast => new()
    { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 50,
      PipelineMinPaceMs = 1, PipelinePollMs = 1 };

    static string[] Commands(int n)
    {
        var c = new string[n];
        for (int i = 0; i < n; i++) c[i] = $@"dread root\amp:{{""index"":0,""chunk"":{i + 1}}}";
        return c;
    }

    [Fact] public async Task SendAsync_translates_a_write_IOException()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.Equal("USB", ex.Transport);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact] public async Task SendAsync_closes_the_port_so_IsOpen_stops_lying()
    {
        // The zombie-link bug: SerialPort.IsOpen stays true after an unplug until someone calls
        // Close(). Nothing did, so every later operation re-attacked a dead handle.
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        Assert.True(link.IsOpen);

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task SendBatchAsync_translates_a_mid_batch_IOException()
    {
        // The reported failure. FakeSerialPort.Write is called TWICE per command (payload, then
        // the NUL), so write #5 lands partway through the third command of a ten-command batch.
        int writes = 0;
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write" && ++writes == 5) throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => link.SendBatchAsync(Commands(10)));
        Assert.Equal("USB", ex.Transport);
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task SendBatchAsync_translates_a_read_IOException()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "read") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendBatchAsync(Commands(10)));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task Cancellation_is_not_a_disconnect()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => link.SendBatchAsync(Commands(10), cts.Token));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
        Assert.True(link.IsOpen);   // a cancel must NOT close the port
    }

    [Fact] public async Task Not_open_guard_still_reports_a_caller_bug_not_a_disconnect()
    {
        // IsFatal matches InvalidOperationException (SerialPort raises it on a closed handle), so
        // the link's own precondition check must sit OUTSIDE the classification try.
        var link = new SerialSonuLink(new FakeSerialPort(), "COM6", 115200, Fast);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read x"));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
    }
}
