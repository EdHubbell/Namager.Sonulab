namespace Sonulab.Distill;

/// <summary>Port of scipy.signal.resample_poly (window=('kaiser',5.0), padtype='constant').
/// The padding arithmetic is copied verbatim from scipy 1.17.1 — do not "simplify" it;
/// parity with the Python oracle depends on it.</summary>
public static class Resampler
{
    public static double BesselI0(double x)
    {
        double t = x * x / 4.0, term = 1.0, sum = 1.0;
        for (int k = 1; k < 1000; k++)
        {
            term *= t / ((double)k * k);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return sum;
    }

    public static double[] Kaiser(int m, double beta)
    {
        var w = new double[m];
        double denom = BesselI0(beta);
        for (int n = 0; n < m; n++)
        {
            double r = 2.0 * n / (m - 1) - 1.0;
            w[n] = BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / denom;
        }
        return w;
    }

    private static double Sinc(double x) =>
        x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>firwin(numtaps, cutoff, window=('kaiser', 5.0)): lowpass, pass_zero, scale=True.
    /// cutoff is relative to Nyquist (scipy default fs=2).</summary>
    public static double[] Firwin(int numtaps, double cutoff)
    {
        var win = Kaiser(numtaps, 5.0);
        var h = new double[numtaps];
        double center = (numtaps - 1) / 2.0, sum = 0;
        for (int i = 0; i < numtaps; i++)
        {
            double m = i - center;
            h[i] = cutoff * Sinc(cutoff * m) * win[i];
            sum += h[i];
        }
        for (int i = 0; i < numtaps; i++) h[i] /= sum;   // scale: unity DC gain
        return h;
    }

    /// <summary>Upsample by up (zero-stuff), FIR filter with h, downsample by down.
    /// Output length matches scipy's _output_len exactly.</summary>
    public static double[] Upfirdn(double[] h, double[] x, int up, int down)
    {
        int nOut = (int)(((long)(x.Length - 1) * up + h.Length - 1) / down + 1);
        var y = new double[nOut];
        for (int k = 0; k < nOut; k++)
        {
            long t = (long)k * down;   // index in the upsampled, filtered stream
            double acc = 0;
            // y[t] = sum_j h[j] * xu[t-j], where xu[m] = x[m/up] when m % up == 0, else 0
            for (long j = t % up; j < h.Length; j += up)
            {
                long m = (t - j) / up;
                if (m < 0) break;
                if (m < x.Length) acc += h[j] * x[m];
            }
            y[k] = acc;
        }
        return y;
    }

    public static double[] ResamplePoly(double[] x, int up, int down)
    {
        int g = Gcd(up, down);
        up /= g; down /= g;
        if (up == 1 && down == 1) return (double[])x.Clone();

        int nIn = x.Length;
        long nOutL = (long)nIn * up;
        int nOut = (int)(nOutL / down + (nOutL % down != 0 ? 1 : 0));

        int maxRate = Math.Max(up, down);
        double fc = 1.0 / maxRate;
        int halfLen = 10 * maxRate;
        var h = Firwin(2 * halfLen + 1, fc);
        for (int i = 0; i < h.Length; i++) h[i] *= up;

        int nPrePad = down - halfLen % down;
        int nPostPad = 0;
        int nPreRemove = (halfLen + nPrePad) / down;
        while (OutputLen(h.Length + nPrePad + nPostPad, nIn, up, down) < nOut + nPreRemove)
            nPostPad++;

        var hp = new double[nPrePad + h.Length + nPostPad];
        h.CopyTo(hp, nPrePad);

        var y = Upfirdn(hp, x, up, down);
        return y[nPreRemove..(nPreRemove + nOut)];
    }

    private static long OutputLen(int lenH, int inLen, int up, int down) =>
        ((long)(inLen - 1) * up + lenH - 1) / down + 1;

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
