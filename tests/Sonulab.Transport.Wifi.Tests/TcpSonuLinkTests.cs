using System.Text;
using Sonulab.Transport.Wifi;
using Sonulab.Transport.Wifi.Tests;

public class TcpSonuLinkTests
{
    private static readonly TcpLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40, ResyncQuietMs = 30 };

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

    [Fact]
    public async Task Late_response_to_an_abandoned_command_is_skipped_not_returned_as_the_next_response()
    {
        // Field desync (live wire capture 2026-07-21): the pedal ACKs EVERY command, but during
        // flash work (rename dwrite / save copy) the ACK can arrive after the first-byte timeout.
        // The abandoned response then arrives with (or before) the NEXT command's response, and the
        // old code returned it as that response — off-by-one desync ("Reorder verify failed",
        // stranded __sstmp names). An owed response must be consumed, not returned.
        var (link, conn) = Open();
        var r1 = await link.SendAsync(@"dwrite root\presets:{""index"":23,""chunk"":-1}"); // silence -> abandoned
        Assert.Equal("", r1);
        // The late ACK arrives together with the next command's real response:
        conn.RespondWith = _ => Encoding.ASCII.GetBytes(
            "dwrite root\\presets:{\"index\":23,\"chunk\":-1}\0root\\presets:{\"value\":[\"A\"]}\0");
        var r2 = await link.SendAsync(@"read root\presets");
        Assert.DoesNotContain("chunk", r2);      // the stale ACK must NOT be returned
        Assert.Contains("\"A\"", r2);            // the real response must be
    }

    [Fact]
    public async Task Late_response_arriving_while_idle_is_drained_and_accounted()
    {
        // Same owed response, but it lands in the buffer BEFORE the next send. The pre-send drain
        // must both discard it AND settle the debt — otherwise the skip logic would wrongly treat
        // the next real response as stale.
        var (link, conn) = Open();
        var r1 = await link.SendAsync(@"write root\app\preset:{""value"":""x"",""save"":""save""}");
        Assert.Equal("", r1);                                            // abandoned
        conn.Feed("write root\\app\\preset:{\"done\":1}\0");             // late ACK arrives while idle
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\presets:{\"value\":[\"B\"]}\0");
        var r2 = await link.SendAsync(@"read root\presets");
        Assert.Contains("\"B\"", r2);                                    // real response returned intact
    }

    [Fact]
    public async Task A_response_that_never_arrives_does_not_permanently_wedge_the_link()
    {
        // Review finding: if the device EVER fails to answer (firmware drops a command), the owed
        // count would stick — every later real response consumed as "stale", every read returning
        // empty forever ("no presets" with no recovery but reconnect). After two consecutive
        // cycles of consume-stale-then-find-nothing, the debt must be declared phantom and reset.
        var (link, conn) = Open();
        Assert.Equal("", await link.SendAsync(@"write root\app\x:{""value"":1}"));   // never answered
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\presets:{\"value\":[\"A\"]}\0");
        await link.SendAsync(@"read root\presets");        // real answer eaten as 'stale' (strike 1)
        await link.SendAsync(@"read root\presets");        // eaten again (strike 2 -> self-heal)
        var healed = await link.SendAsync(@"read root\presets");
        Assert.Contains("\"A\"", healed);                  // link recovered without a reconnect
    }

    [Fact]
    public async Task Reopening_the_link_clears_owed_response_debt()
    {
        // A fresh socket owes nothing: bytes from the old connection can't arrive on the new one.
        var (link, conn) = Open();
        Assert.Equal("", await link.SendAsync(@"write root\app\x:{""value"":1}"));   // abandoned -> debt
        link.Close();
        await link.OpenAsync();
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\presets:{\"value\":[\"B\"]}\0");
        var r = await link.SendAsync(@"read root\presets");
        Assert.Contains("\"B\"", r);                       // not skipped as stale debt from the old socket
    }

    [Fact]
    public async Task Large_response_streamed_with_gap_longer_than_idlegap_is_not_truncated()
    {
        // Regression (field crash "Slot 23 is empty" over WiFi): a multi-record response — e.g. the
        // 30-slot preset list — can arrive over TCP in bursts separated by gaps LONGER than a serial
        // link ever sees. The device NUL-terminates the whole response, so an idle gap must NOT be
        // treated as end-of-data: an early idle-gap break dropped the tail, so trailing preset slots
        // read as empty and the reorder threw. The NUL terminator is authoritative.
        var (link, conn) = Open();                      // Fast: IdleGap=30, MaxWait=500
        conn.RespondWith = _ => Encoding.ASCII.GetBytes(
            "root\\presets\\0:{\"value\":\"A\"}\r\nroot\\presets\\1:{\"value\":\"B\"}\r\n");  // first burst, NO NUL yet
        var task = link.SendAsync(@"read root\presets");
        await Task.Delay(90);                           // 90ms > IdleGap 30 — old code breaks here and truncates
        conn.Feed("root\\presets\\2:{\"value\":\"C\"}\0");   // tail + terminating NUL arrives late
        var resp = await task;
        Assert.Contains("\"A\"", resp);
        Assert.Contains("\"C\"", resp);                 // the late tail must survive (fails on the idle-gap-break code)
    }
}
