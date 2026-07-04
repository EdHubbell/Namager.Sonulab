namespace Sonulab.Distill.Tests;

public class VxampCodecTests
{
    private static WhTensors MakeTensors(float fill = 0.5f)
    {
        var pre = new float[1024]; pre[0] = 1f; pre[1] = fill;
        var g2 = new float[1024]; g2[0] = 2f; g2[500] = -fill;
        return new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                             VxampFormat.NlmixHeaderFloats(), 0.25f);
    }

    [Fact]
    public void Keystream_matches_python_formula()
    {
        // k[i] = (K0[i%32] - 0x20*(i//32)) % 256; K0[0]=0x99, K0[31]=0x9e
        var k = VxampFormat.Keystream(70);
        Assert.Equal(0x99, k[0]);
        Assert.Equal(0x9e, k[31]);
        Assert.Equal((byte)((0x99 - 0x20) & 0xFF), k[32]);   // second period
        Assert.Equal((byte)((0x97 - 0x40) & 0xFF), k[65]);   // third period, K0[1]
    }

    [Fact]
    public void Header_and_tlv_constants_are_exact()
    {
        Assert.Equal(32, VxampFormat.HeaderBytes.Length);
        Assert.Equal(0x40, VxampFormat.HeaderBytes[0]);
        // "Amp model" at offset 8
        Assert.Equal((byte)'A', VxampFormat.HeaderBytes[8]);
        // g2_header floats reinterpret bytes 0C 10 00 00 | 00 00 00 00 | 47 32 00 00
        var g2h = VxampFormat.G2HeaderFloats();
        Assert.Equal(3, g2h.Length);
        Assert.Equal(0x100C, BitConverter.SingleToInt32Bits(g2h[0]));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(g2h[1]));
        var nlh = VxampFormat.NlmixHeaderFloats();
        Assert.Equal(4, nlh.Length);
        Assert.Equal(0x14, BitConverter.SingleToInt32Bits(nlh[0]));
    }

    [Fact]
    public void Encode_produces_valid_slot_and_roundtrips()
    {
        var t = MakeTensors();
        var slot = VxampCodec.Encode(t);
        Assert.Equal(VxampFormat.SlotSize, slot.Length);
        Assert.Equal(VxampFormat.HeaderBytes, slot.Take(32).ToArray());
        // padding after byte 8256 is zero
        Assert.All(slot.Skip(8256), b => Assert.Equal(0, b));
        var back = VxampCodec.Decode(slot);
        Assert.Equal(t.PreFir, back.PreFir);
        Assert.Equal(t.G2Fir, back.G2Fir);
        Assert.Equal(t.Nlmix, back.Nlmix);
    }

    [Fact]
    public void Encode_rejects_wrong_sizes()
    {
        var t = MakeTensors() with { PreFir = new float[10] };
        Assert.Throws<ArgumentException>(() => VxampCodec.Encode(t));
    }
}
