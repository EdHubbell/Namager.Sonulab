using System.Text;

namespace Sonulab.Transport.Wifi;

/// <summary>Hand-rolled one-shot mDNS (RFC 6762/1035 subset): build the _http._tcp.local PTR
/// query; parse a response datagram into the pedal's record. The pedal is identified by the
/// TXT key id=voidx (other devices — printers etc. — advertise the same service type).</summary>
public static class MdnsMessages
{
    private const string ServiceName = "_http._tcp.local";

    public static byte[] BuildHttpTcpPtrQuery()
    {
        var ms = new MemoryStream();
        // Header: ID=0, flags=0, QDCOUNT=1, ANCOUNT=NSCOUNT=ARCOUNT=0
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in ServiceName.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 12, 0, 1 });   // QTYPE=PTR, QCLASS=IN
        return ms.ToArray();
    }

    public static MdnsRecord? TryParsePedal(byte[] datagram)
    {
        try { return ParsePedal(datagram); }
        catch { return null; }   // malformed/truncated input must never throw to callers
    }

    private static MdnsRecord? ParsePedal(byte[] d)
    {
        if (d.Length < 12) return null;
        int qd = (d[4] << 8) | d[5];
        int answers = ((d[6] << 8) | d[7]) + ((d[8] << 8) | d[9]) + ((d[10] << 8) | d[11]);
        int off = 12;
        for (int i = 0; i < qd; i++) { (_, off) = ReadName(d, off); off += 4; }

        string? instance = null, host = null, address = null, deviceName = null;
        int port = 0;
        bool isVoidx = false;

        for (int i = 0; i < answers; i++)
        {
            var (name, o1) = ReadName(d, off);
            off = o1;
            if (off + 10 > d.Length) return null;
            int rtype = (d[off] << 8) | d[off + 1];
            int rdlen = (d[off + 8] << 8) | d[off + 9];
            int rdOff = off + 10;
            if (rdOff + rdlen > d.Length) return null;

            switch (rtype)
            {
                case 12: // PTR: _http._tcp.local -> instance
                {
                    var (target, _) = ReadName(d, rdOff);
                    if (name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                        instance = target.Split('.')[0];
                    break;
                }
                case 33: // SRV: priority(2) weight(2) port(2) target
                {
                    port = (d[rdOff + 4] << 8) | d[rdOff + 5];
                    (host, _) = ReadName(d, rdOff + 6);
                    instance ??= name.Split('.')[0];
                    break;
                }
                case 16: // TXT: length-prefixed key=value strings
                {
                    int p = rdOff;
                    while (p < rdOff + rdlen)
                    {
                        int len = d[p];
                        var kv = Encoding.UTF8.GetString(d, p + 1, len);
                        if (kv == "id=voidx") isVoidx = true;
                        else if (kv.StartsWith("name=", StringComparison.Ordinal)) deviceName = kv[5..];
                        p += 1 + len;
                    }
                    break;
                }
                case 1: // A
                    if (rdlen == 4) address = $"{d[rdOff]}.{d[rdOff + 1]}.{d[rdOff + 2]}.{d[rdOff + 3]}";
                    break;
            }
            off = rdOff + rdlen;
        }

        return isVoidx && instance is not null && host is not null && address is not null && port > 0
            ? new MdnsRecord(instance, host, address, port, deviceName)
            : null;
    }

    /// <summary>RFC 1035 name decoding with compression pointers. Returns (name, offset after
    /// the name at the ORIGINAL position — pointers do not advance it).</summary>
    private static (string Name, int NextOffset) ReadName(byte[] d, int off)
    {
        var parts = new List<string>();
        int next = -1;
        int hops = 0;
        while (true)
        {
            if (off >= d.Length || ++hops > 64) throw new InvalidDataException("bad name");
            int len = d[off];
            if (len == 0) { if (next < 0) next = off + 1; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (next < 0) next = off + 2;
                off = ((len & 0x3F) << 8) | d[off + 1];
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(d, off + 1, len));
            off += 1 + len;
        }
        return (string.Join('.', parts), next);
    }
}
