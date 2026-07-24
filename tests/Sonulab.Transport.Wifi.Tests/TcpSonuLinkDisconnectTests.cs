using System.Net.Sockets;
using Sonulab.Core.Transport;
using Sonulab.Transport.Wifi;
using Xunit;

namespace Sonulab.Transport.Wifi.Tests;

public class TcpSonuLinkDisconnectTests
{
    static TcpLinkOptions Fast => new() { PollMs = 2, MaxWaitMs = 300, ConnectTimeoutMs = 200 };

    [Fact] public async Task SendAsync_translates_a_socket_reset()
    {
        var conn = new FakeTcpConn { RespondWith = _ => "root\\x:{\"value\":1}\0"u8.ToArray() };
        var link = new TcpSonuLink(conn, "10.0.0.5", 8080, Fast);
        await link.OpenAsync();
        conn.OnIo = op => { if (op == "send") throw new SocketException(10054); };  // ECONNRESET

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.Equal("WiFi", ex.Transport);
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact] public async Task SendAsync_closes_the_socket_on_fault()
    {
        var conn = new FakeTcpConn { RespondWith = _ => "root\\x:{\"value\":1}\0"u8.ToArray() };
        var link = new TcpSonuLink(conn, "10.0.0.5", 8080, Fast);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        conn.OnIo = op => { if (op == "send") throw new System.IO.IOException("broken pipe"); };

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task Not_open_guard_still_reports_a_caller_bug()
    {
        var link = new TcpSonuLink(new FakeTcpConn(), "10.0.0.5", 8080, Fast);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read x"));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
    }
}
