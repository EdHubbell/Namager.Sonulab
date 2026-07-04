namespace Sonulab.Distill;

/// <summary>root\ir blob format, pinned 2026-07-04 by tools/ir-re/analyze_irs.py against
/// Ed's dobro .wav sources and their VoidX-uploaded device dumps (see docs/ir-format.md).
/// Winner: raw-f32 (1024 LE float32 samples, no obfuscation) — corr = +1.0000 against the
/// wav's own first 1024 samples for all 4 dobro pairs (slots 16-19); xor-f32 decoded to
/// non-finite garbage and raw-i16 scored near-zero correlation, so both were rejected.</summary>
public static class IrFormat
{
    public const int BlobBytes = 4096;
    public const int SampleCount = 1024;         // BlobBytes / 4 for float32, confirmed by analysis

    /// <summary>Encode SampleCount samples (44.1 kHz mono, already scaled per the pinned
    /// rule) to a 4096-byte blob.</summary>
    public static byte[] Encode(double[] samples)
    {
        if (samples.Length != SampleCount)
            throw new ArgumentException($"Expected {SampleCount} samples, got {samples.Length}.");
        var blob = new byte[BlobBytes];
        for (int i = 0; i < SampleCount; i++)
            BitConverter.TryWriteBytes(blob.AsSpan(i * 4, 4), (float)samples[i]);
        return blob;
    }

    public static double[] Decode(byte[] blob)
    {
        if (blob.Length != BlobBytes)
            throw new ArgumentException($"Expected {BlobBytes}-byte blob, got {blob.Length}.");
        var s = new double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
            s[i] = BitConverter.ToSingle(blob, i * 4);
        return s;
    }
}
