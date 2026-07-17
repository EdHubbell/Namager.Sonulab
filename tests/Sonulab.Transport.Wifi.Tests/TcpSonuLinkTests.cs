using System.Text;
using Sonulab.Transport.Wifi;
using Sonulab.Transport.Wifi.Tests;

public class TcpSonuLinkTests
{
    private static readonly TcpLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static (TcpSonuLink link, FakeTcpConn conn) Open()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        link.OpenAsync().GetAwaiter().GetResult();
        return (link, conn);
    }

    [Fact]
    public async Task Open_connects_to_host_and_port_and_IsOpen_tracks_conn()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        Assert.False(link.IsOpen);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        Assert.Equal(("192.168.8.241", 8080), conn.ConnectedTo);
        link.Close();
        Assert.False(link.IsOpen);
    }

    [Fact]
    public async Task Command_is_sent_with_trailing_nul_and_response_collected_to_nul()
    {
        var (link, conn) = Open();
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0");
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.Contains("AMP Station", resp);
        var sent = conn.Sends.SelectMany(s => s).ToArray();
        Assert.Equal((byte)0, sent[^1]);
        Assert.Equal(@"read root\sys\_name", Encoding.ASCII.GetString(sent, 0, sent.Length - 1));
    }

    [Fact]
    public async Task Response_arriving_in_pieces_is_reassembled()
    {
        var (link, conn) = Open();
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\sys\\wifi\\ssid:{\"val");
        var task = link.SendAsync(@"read root\sys\wifi\ssid");
        await Task.Delay(10);
        conn.Feed("ue\":\"Duke Park Mesh\"}\0");
        var resp = await task;
        Assert.Contains("Duke Park Mesh", resp);
    }

    [Fact]
    public async Task Stale_bytes_before_send_are_drained()
    {
        var (link, conn) = Open();
        conn.Feed("leftover-garbage\r\n");
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\app\\preset:{\"value\":\"Pano-Verb\"}\0");
        var resp = await link.SendAsync(@"read root\app\preset");
        Assert.DoesNotContain("leftover", resp);
        Assert.Contains("Pano-Verb", resp);
    }

    [Fact]
    public async Task No_response_command_returns_empty_after_first_byte_timeout()
    {
        var (link, conn) = Open();                                  // RespondWith null -> silence
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await link.SendAsync(@"write root\app\x:{""value"":1}");
        Assert.Equal("", resp);
        Assert.True(sw.ElapsedMilliseconds < Fast.MaxWaitMs, "stops at FirstByteTimeout, not MaxWait");
    }

    [Fact]
    public async Task Send_on_closed_link_throws()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read root"));
    }
}
