namespace Sonulab.Distill;

/// <summary>root\ir blob format, pinned 2026-07-04 by tools/ir-re/analyze_irs.py against
/// Ed's dobro .wav sources and their VoidX-uploaded device dumps (see docs/ir-format.md).
/// Winner: raw-f32 (1024 LE float32 samples, no obfuscation) — corr = +1.0000 against the
/// wav's own first 1024 samples for all 4 dobro pairs (slots 16-19); xor-f32 decoded to
/// non-finite garbage and raw-i16 scored near-zero correlation, so both were rejected.
/// Callers MUST normalize their truncated 1024-sample window to unit L2 norm (see
/// <see cref="NormalizeUnitL2"/>) before calling <see cref="Encode"/> — Encode/Decode are a
/// pure byte codec and do not scale samples themselves.</summary>
public static class IrFormat
{
    public const int BlobBytes = 4096;
    public const int SampleCount = 1024;         // BlobBytes / 4 for float32, confirmed by analysis

    /// <summary>Scale samples so the vector has unit Euclidean (L2) norm: sum(s_i^2) == 1.0.
    /// Pinned scaling rule (docs/ir-format.md): computed over the already-truncated/padded
    /// 1024-sample window, not the full-length capture. Guards against a zero norm (silent/empty
    /// window) by returning the input unchanged rather than dividing by zero.</summary>
    public static double[] NormalizeUnitL2(double[] samples)
    {
        double sumSq = 0;
        foreach (var s in samples) sumSq += s * s;
        double norm = Math.Sqrt(sumSq);
        if (norm <= 0) return samples;
        var result = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) result[i] = samples[i] / norm;
        return result;
    }

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
