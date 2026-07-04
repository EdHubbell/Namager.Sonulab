using System.Runtime.InteropServices;

namespace Sonulab.Distill;

/// <summary>Encode/decode 12288-byte vxamp slots (port of codec.py + nam_to_vxamp.write_vxamp).</summary>
public static class VxampCodec
{
    public static byte[] Encode(WhTensors t)
    {
        Check(t.PreFir, VxampFormat.PreFirTaps, nameof(t.PreFir));
        Check(t.G2Header, 3, nameof(t.G2Header));
        Check(t.G2Fir, VxampFormat.G2FirTaps, nameof(t.G2Fir));
        Check(t.NlmixHeader, 4, nameof(t.NlmixHeader));

        var floats = new float[2056];
        t.PreFir.CopyTo(floats, 0);
        t.G2Header.CopyTo(floats, 1024);
        t.G2Fir.CopyTo(floats, 1027);
        t.NlmixHeader.CopyTo(floats, 2051);
        floats[2055] = t.Nlmix;

        var body = MemoryMarshal.AsBytes<float>(floats).ToArray();  // little-endian on all supported platforms
        var ks = VxampFormat.Keystream(body.Length);
        for (int i = 0; i < body.Length; i++) body[i] ^= ks[i];

        var slot = new byte[VxampFormat.SlotSize];
        VxampFormat.HeaderBytes.CopyTo(slot, 0);
        body.CopyTo(slot, VxampFormat.HeaderSize);
        return slot;   // remainder is already zero padding
    }

    public static WhTensors Decode(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != VxampFormat.SlotSize)
            throw new ArgumentException($"expected {VxampFormat.SlotSize}-byte slot, got {slot.Length}");
        var body = slot.Slice(VxampFormat.HeaderSize, VxampFormat.BodySize).ToArray();
        var ks = VxampFormat.Keystream(body.Length);
        for (int i = 0; i < body.Length; i++) body[i] ^= ks[i];
        var f = MemoryMarshal.Cast<byte, float>(body);
        return new WhTensors(
            f.Slice(0, 1024).ToArray(),
            f.Slice(1024, 3).ToArray(),
            f.Slice(1027, 1024).ToArray(),
            f.Slice(2051, 4).ToArray(),
            f[2055]);
    }

    private static void Check(float[] a, int n, string name)
    {
        if (a.Length != n) throw new ArgumentException($"{name}: expected {n} elements, got {a.Length}");
    }
}
