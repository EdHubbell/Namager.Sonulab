using System.Runtime.InteropServices;

namespace Sonulab.Distill;

/// <summary>Container constants + XOR keystream for the vxamp slot format.
/// Source of truth: tools/vxamp-re/{vxamp,decode_body,arch}.py and docs/vxamp-format.md.</summary>
public static class VxampFormat
{
    public const int SlotSize = 12288;
    public const int HeaderSize = 32;
    public const int BodySize = 8224;      // 2056 float32 LE
    public const int PreFirTaps = 1024;
    public const int G2FirTaps = 1024;

    public static readonly byte[] HeaderBytes = Convert.FromHexString(
        "4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be");

    private static readonly byte[] KeystreamBase =
    {
        0x99, 0x97, 0x77, 0x6f, 0x67, 0x44, 0x45, 0x22, 0x21, 0x02, 0x01, 0xde,
        0xdd, 0xbf, 0xab, 0xa2, 0x93, 0x86, 0x63, 0x64, 0x55, 0x46, 0x33, 0x24,
        0x01, 0x02, 0xdf, 0xe0, 0xbd, 0xb6, 0xa4, 0x9e,
    };

    // TLV chunk headers, byte-identical across all 20 corpus models (arch.py / Task 4).
    private static readonly byte[] G2HeaderBytes =
        { 0x0C, 0x10, 0, 0, 0, 0, 0, 0, 0x47, 0x32, 0, 0 };
    private static readonly byte[] NlmixHeaderBytes =
        { 0x14, 0, 0, 0, 0, 0, 0, 0, 0x6E, 0x6C, 0x6D, 0x69, 0x78, 0, 0, 0 };

    /// <summary>k[i] = (K0[i % 32] - 0x20 * (i / 32)) mod 256.</summary>
    public static byte[] Keystream(int n)
    {
        var k = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int v = KeystreamBase[i % 32] - 0x20 * (i / 32);
            k[i] = (byte)(((v % 256) + 256) % 256);
        }
        return k;
    }

    public static float[] G2HeaderFloats() => MemoryMarshal.Cast<byte, float>(G2HeaderBytes).ToArray();
    public static float[] NlmixHeaderFloats() => MemoryMarshal.Cast<byte, float>(NlmixHeaderBytes).ToArray();
}
