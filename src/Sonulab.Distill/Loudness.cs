namespace Sonulab.Distill;

/// <summary>ITU-R BS.1770 K-weighted loudness, mono, for comparing preset chains.
///
/// Separate from <see cref="Distiller.LoudnessNormalize"/> on purpose: that one measures
/// broadband RMS and must keep bit-parity with the Python oracle. This one exists to match
/// PERCEIVED level between a dark amp and a bright one, which broadband RMS gets wrong by a
/// couple of dB.
///
/// Coefficients are the analytic BS.1770 filter designs evaluated at our sample rate rather
/// than the standard's tabulated 48 kHz values (we run at 44100).
///
/// GATING: the absolute gate (-70 LUFS) only. The relative gate is deliberately omitted — the
/// input is a single fixed, continuously-excited drive signal, not program material with
/// silence to exclude, so the relative gate would add a discontinuity in the measure for no
/// benefit. Absolute values from this class are not comparable to a mastering meter's; only
/// DIFFERENCES between two chains are meaningful.</summary>
public static class Loudness
{
    public const int SampleRate = DeviceSim.SampleRate;   // 44100

    /// <summary>High-shelf gain in the limit, +3.999843853973347 dB as a linear ratio. A
    /// bilinear-transformed shelf hits exactly this at Nyquist, which is what pins the
    /// coefficients in the tests.</summary>
    public static readonly double ShelfGain = Math.Pow(10.0, ShelfGainDb / 20.0);

    private const double ShelfGainDb = 3.999843853973347;
    private const double ShelfQ = 0.7071752369554196;
    private const double ShelfFc = 1681.974450955533;
    private const double HpQ = 0.5003270373238773;
    private const double HpFc = 38.13547087602444;

    private const double BlockSeconds = 0.400;
    private const double HopSeconds = 0.100;              // 75 % overlap
    private const double LufsOffset = -0.691;             // BS.1770 channel-weighting offset
    private const double AbsoluteGateLufs = -70.0;

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    private static double[] Apply(Biquad f, double[] x)
    {
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = f.B0 * x[i] + f.B1 * x1 + f.B2 * x2 - f.A1 * y1 - f.A2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            y[i] = v;
        }
        return y;
    }

    private static Biquad HighShelf()
    {
        double k = Math.Tan(Math.PI * ShelfFc / SampleRate);
        double vh = Math.Pow(10.0, ShelfGainDb / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / ShelfQ + k * k;
        return new Biquad(
            (vh + vb * k / ShelfQ + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / ShelfQ + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / ShelfQ + k * k) / a0);
    }

    private static Biquad HighPass()
    {
        double k = Math.Tan(Math.PI * HpFc / SampleRate);
        double a0 = 1.0 + k / HpQ + k * k;
        return new Biquad(1.0, -2.0, 1.0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / HpQ + k * k) / a0);
    }

    /// <summary>Run the two K-weighting stages. Exposed so the filter itself is testable.</summary>
    public static double[] KWeight(double[] x) => Apply(HighPass(), Apply(HighShelf(), x));

    /// <summary>Integrated K-weighted loudness in LUFS, or <see cref="double.NegativeInfinity"/>
    /// when the signal is shorter than one 400 ms block or every block falls under the
    /// absolute gate.</summary>
    public static double IntegratedLufs(double[] x)
    {
        var y = KWeight(x);
        int block = (int)(BlockSeconds * SampleRate);
        int hop = (int)(HopSeconds * SampleRate);
        if (y.Length < block) return double.NegativeInfinity;

        double sum = 0;
        int kept = 0;
        for (int start = 0; start + block <= y.Length; start += hop)
        {
            double ms = 0;
            for (int i = start; i < start + block; i++) ms += y[i] * y[i];
            ms /= block;
            if (ms <= 0) continue;
            if (LufsOffset + 10.0 * Math.Log10(ms) <= AbsoluteGateLufs) continue;
            sum += ms;
            kept++;
        }
        return kept == 0 ? double.NegativeInfinity : LufsOffset + 10.0 * Math.Log10(sum / kept);
    }
}
