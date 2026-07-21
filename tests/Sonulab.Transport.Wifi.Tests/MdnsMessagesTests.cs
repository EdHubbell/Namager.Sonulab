using System.Text;
using Sonulab.Transport.Wifi;

public class MdnsMessagesTests
{
    // Helper: Build a synthetic mDNS datagram (no compression, plain label encoding)
    private static byte[] BuildDatagram(params (string name, int type, byte[] rdata)[] records)
    {
        var ms = new MemoryStream();
        // Header: ID=0, flags=0x8400 (response), QD=0, AN=records.Length, NS=0, AR=0
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0x84);
        ms.WriteByte(0x00);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)(records.Length >> 8));
        ms.WriteByte((byte)(records.Length & 0xFF));
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte(0);

        foreach (var (name, type, rdata) in records)
        {
            // Encode name
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)b.Length);
                ms.Write(b);
            }
            ms.WriteByte(0);

            // Type, class, TTL, rdlen
            ms.WriteByte((byte)(type >> 8));
            ms.WriteByte((byte)(type & 0xFF));
            ms.WriteByte(0);
            ms.WriteByte(1); // IN
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(0x00);
            ms.WriteByte(0x78); // TTL=120
            ms.WriteByte((byte)(rdata.Length >> 8));
            ms.WriteByte((byte)(rdata.Length & 0xFF));
            ms.Write(rdata);
        }

        return ms.ToArray();
    }

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

    [Fact]
    public void Rejects_id_voidx_txt_on_wrong_service_type()
    {
        // Synthetic datagram: SRV and TXT under _other._tcp.local (not _http._tcp.local)
        // with id=voidx in TXT and a valid A record. Parser must reject because
        // the TXT/SRV owners don't match the pedal's service type.
        var srvRdata = new MemoryStream();
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0); // priority
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0); // weight
        srvRdata.WriteByte(0x1F);
        srvRdata.WriteByte(0x90); // port 8080
        srvRdata.Write(Encoding.ASCII.GetBytes("\x01x\x05local\x00")); // target: x.local
        var srvData = srvRdata.ToArray();

        var txtRdata = new byte[] { 0x08, 0x69, 0x64, 0x3D, 0x76, 0x6F, 0x69, 0x64, 0x78 }; // "\x08id=voidx"

        var aRdata = new byte[] { 0x01, 0x02, 0x03, 0x04 }; // 1.2.3.4

        var datagram = BuildDatagram(
            ("pedal._other._tcp.local", 33, srvData),
            ("pedal._other._tcp.local", 16, txtRdata),
            ("x.local", 1, aRdata)
        );

        Assert.Null(MdnsMessages.TryParsePedal(datagram));
    }

    [Fact]
    public void Rejects_label_boundary_spoof()
    {
        // Synthetic datagram: SRV+TXT owner "x_http._tcp.local" (labels: "x_http", "_tcp", "local")
        // looks like _http._tcp.local but isn't (first label is x_http, not _http).
        // The service-type check must use label-boundary comparison, not string suffix.
        var srvRdata = new MemoryStream();
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0x1F);
        srvRdata.WriteByte(0x90); // port 8080
        srvRdata.Write(Encoding.ASCII.GetBytes("\x01x\x05local\x00")); // target: x.local
        var srvData = srvRdata.ToArray();

        var txtRdata = new byte[] { 0x08, 0x69, 0x64, 0x3D, 0x76, 0x6F, 0x69, 0x64, 0x78 }; // "\x08id=voidx"

        var aRdata = new byte[] { 0x01, 0x02, 0x03, 0x04 }; // 1.2.3.4

        var datagram = BuildDatagram(
            ("x_http._tcp.local", 33, srvData),
            ("x_http._tcp.local", 16, txtRdata),
            ("x.local", 1, aRdata)
        );

        Assert.Null(MdnsMessages.TryParsePedal(datagram));
    }

    [Fact]
    public void Truncated_txt_rdlen_returns_null()
    {
        // Synthetic datagram: TXT record where rdlen is set so that a second string entry's
        // length byte makes it overrun the TXT record's OWN rdlen boundary (rdOff+rdlen) —
        // but the claimed bytes still land WITHIN the overall datagram (d.Length), because the
        // following A record's bytes occupy that space. This is deliberate: a datagram whose
        // overrun also exceeds d.Length is NOT a genuine test of the rdlen guard at line ~88,
        // because Encoding.UTF8.GetString would then throw ArgumentOutOfRangeException on its
        // own (caught by the outer try/catch in TryParsePedal) — the same null would come back
        // whether or not the guard exists. By keeping the overrun within d.Length, removing the
        // guard here would NOT throw: GetString would succeed by reading into the A record's
        // leading bytes, and the loop would exit (p advances past rdOff+rdlen), so this test
        // only fails without the guard because of the guard's absence specifically.
        //
        // First string: 0x08 + "id=voidx" (9 bytes total, valid — this ensures isVoidx=true)
        // Second string: length byte 0x05 (claims 5 bytes follow), but rdlen=10 only covers the
        // first string (9 bytes) + this one length byte (1 byte) = 10 bytes — no room for the
        // claimed 5 bytes within rdlen. Those 5 bytes DO exist further along in the datagram
        // (start of the following A record's owner name), so d.Length is not exceeded.
        var srvRdata = new MemoryStream();
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0);
        srvRdata.WriteByte(0x1F);
        srvRdata.WriteByte(0x90); // port 8080
        srvRdata.Write(Encoding.ASCII.GetBytes("\x04test\x05local\x00")); // target: test.local

        // TXT: [0x08, 'i', 'd', '=', 'v', 'o', 'i', 'd', 'x', 0x05]
        // (9 bytes for id=voidx + 1 byte claiming 5 more = 10 bytes total, matching rdlen=10)
        var txtRdata = new byte[] { 0x08, 0x69, 0x64, 0x3D, 0x76, 0x6F, 0x69, 0x64, 0x78, 0x05 };

        var aRdata = new byte[] { 0xC0, 0xA8, 0x08, 0xF1 }; // 192.168.8.241

        var datagram = BuildDatagram(
            ("test._http._tcp.local", 33, srvRdata.ToArray()),
            ("test._http._tcp.local", 16, txtRdata),
            ("test.local", 1, aRdata)
        );

        // Non-vacuity: with the line-88 guard removed, GetString(d, p+1, 5) succeeds (those 5
        // bytes exist — they're the start of the following A record's owner-name label), so this
        // assertion FAILS without the guard. See task-4-fix-report.md for the recorded proof.
        Assert.Null(MdnsMessages.TryParsePedal(datagram));
    }
}
