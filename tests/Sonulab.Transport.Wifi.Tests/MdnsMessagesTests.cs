using Sonulab.Transport.Wifi;

public class MdnsMessagesTests
{
    // Real datagram captured from the pedal (192.168.8.241) on 2026-07-17:
    // PTR _http._tcp.local -> voidxc7e811051914272110b41dc7c558._http._tcp.local
    // SRV port=8080 target=voidxc7e811051914272110b41dc7c558.local
    // TXT id=voidx, MAC=10:B4:1D:C7:C5:5A, name=AMP Station ; A -> 192.168.8.241
    private const string PedalB64 =
        "AACEAAABAAMAAAABBV9odHRwBF90Y3AFbG9jYWwAAAwAAcAMAAwAAQAAEZQAJCF2b2lkeGM3ZTgxMTA1MTkxNDI3MjExMGI0MWRjN2M1NTjADMAuACEAAQAAAHgAKgAAAAAfkCF2b2lkeGM3ZTgxMTA1MTkxNDI3MjExMGI0MWRjN2M1NTjAF8AuABAAAQAAEZQAMAhpZD12b2lkeBVNQUM9MTA6QjQ6MUQ6Qzc6QzU6NUEQbmFtZT1BTVAgU3RhdGlvbsBkAAEAAQAAAHgABMCoCPE=";

    // Real decoy captured the same night: a Canon printer also advertising _http._tcp
    // (TXT has path=/ but NO id=voidx) — the parser must reject it.
    private const string CanonB64 =
        "AACAAAAAAAEAAAAFBV9odHRwBF90Y3AFbG9jYWwAAAwAAQAAD6AAFhNDYW5vbiBNRjc1MEMgU2VyaWVzwAwLQ2Fub25mMTA0N2TAFwABAAEAAAB4AATAqAigwCgAIQABAAAAeAAIAAAAAABQwD7AKAAQAAEAAA+gAAcGcGF0aD0vwD4ALwABAAAPoAAIwD4ABEAAAATAKAAvAAEAAA+gAAnAKAAFAACAAEA=";

    [Fact]
    public void Parses_the_real_pedal_datagram()
    {
        var rec = MdnsMessages.TryParsePedal(Convert.FromBase64String(PedalB64));
        Assert.NotNull(rec);
        Assert.Equal("voidxc7e811051914272110b41dc7c558", rec!.InstanceName);
        Assert.Equal("voidxc7e811051914272110b41dc7c558.local", rec.Host);
        Assert.Equal("192.168.8.241", rec.Address);
        Assert.Equal(8080, rec.Port);
        Assert.Equal("AMP Station", rec.DeviceName);
    }

    [Fact]
    public void Rejects_the_canon_printer_decoy()
        => Assert.Null(MdnsMessages.TryParsePedal(Convert.FromBase64String(CanonB64)));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Malformed_datagrams_return_null_never_throw(byte[] junk)
        => Assert.Null(MdnsMessages.TryParsePedal(junk));

    [Fact]
    public void Truncated_real_datagram_returns_null()
    {
        var full = Convert.FromBase64String(PedalB64);
        Assert.Null(MdnsMessages.TryParsePedal(full[..40]));
    }

    [Fact]
    public void Query_is_a_wellformed_ptr_question_for_http_tcp_local()
    {
        var q = MdnsMessages.BuildHttpTcpPtrQuery();
        // Header: id=0, flags=0, QD=1, AN/NS/AR=0
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        // QNAME _http._tcp.local, QTYPE=PTR(12), QCLASS=IN(1)
        Assert.Equal((byte)5, q[12]);
        Assert.Equal("_http", System.Text.Encoding.ASCII.GetString(q, 13, 5));
        Assert.Equal((byte)4, q[18]);
        Assert.Equal("_tcp", System.Text.Encoding.ASCII.GetString(q, 19, 4));
        Assert.Equal((byte)5, q[23]);
        Assert.Equal("local", System.Text.Encoding.ASCII.GetString(q, 24, 5));
        Assert.Equal(0, q[29]);
        Assert.Equal(12, (q[30] << 8) | q[31]);
        Assert.Equal(1, (q[32] << 8) | q[33]);
    }
}
