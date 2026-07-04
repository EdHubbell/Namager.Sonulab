namespace Sonulab.Distill.Tests;

public class IrFormatTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "ir-pair", name);

    [Fact]
    public void Encode_decode_roundtrip_and_size_validation()
    {
        var s = Enumerable.Range(0, IrFormat.SampleCount).Select(i => Math.Sin(i * 0.05) * 0.5).ToArray();
        var blob = IrFormat.Encode(s);
        Assert.Equal(IrFormat.BlobBytes, blob.Length);
        var back = IrFormat.Decode(blob);
        // Tolerance-based (not decimal-place rounding): float32 round-trip loses ~7 significant
        // digits, and a fixed 6-decimal-place Assert.Equal flakes when a value sits right at a
        // rounding boundary (observed: -0.4783175081... vs -0.4783174991..., true diff ~9e-9).
        for (int i = 0; i < s.Length; i++) Assert.True(Math.Abs(s[i] - back[i]) < 1e-6,
            $"sample {i}: expected {s[i]}, got {back[i]}");
        Assert.Throws<ArgumentException>(() => IrFormat.Encode(new double[3]));
        Assert.Throws<ArgumentException>(() => IrFormat.Decode(new byte[3]));
    }

    [Fact]
    public void Device_pair_fixture_decodes_to_a_bounded_wave()
    {
        var dec = IrFormat.Decode(File.ReadAllBytes(Fixture("device.irblob")));
        Assert.Equal(IrFormat.SampleCount, dec.Length);
        Assert.All(dec, v => Assert.True(double.IsFinite(v) && Math.Abs(v) < 16.0));
        // wave-like, not noise/garbage: nonzero lag-1 autocorrelation. The fixture is a
        // resonator IR (fast-ringing transient, not a smooth reverb decay), so the true
        // measured value is ~-0.157 (tools/ir-re/analyze_irs.py) rather than the >0.5 a
        // decaying-reverb IR would show. The rejected raw-i16 "noise" hypothesis measured
        // |lag1| in 0.01-0.04 for these same slots, so 0.1 still cleanly separates real
        // signal from garbage while matching pinned evidence.
        double lag1 = Pearson(dec[..^1], dec[1..]);
        Assert.True(Math.Abs(lag1) > 0.1, $"lag1 autocorr {lag1:F3} — decoded blob doesn't look like audio");
    }

    private static double Pearson(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), cov = 0, va = 0, vb = 0;
        for (int i = 0; i < a.Length; i++)
        { cov += (a[i] - ma) * (b[i] - mb); va += (a[i] - ma) * (a[i] - ma); vb += (b[i] - mb) * (b[i] - mb); }
        return cov / Math.Sqrt(va * vb);
    }
}
