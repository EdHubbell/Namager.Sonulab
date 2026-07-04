using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Sonulab.Distill;

/// <summary>numpy/scipy-semantics DSP primitives. FFT uses FourierOptions.Matlab
/// (= numpy convention: unscaled forward, 1/N inverse).</summary>
public static class Dsp
{
    public static double[] Convolve(double[] a, double[] b)
    {
        var y = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                y[i + j] += a[i] * b[j];
        return y;
    }

    public static double[] FirFilter(double[] taps, double[] x)
    {
        var y = new double[x.Length];
        for (int n = 0; n < x.Length; n++)
        {
            double acc = 0;
            int kMax = Math.Min(taps.Length - 1, n);
            for (int k = 0; k <= kMax; k++) acc += taps[k] * x[n - k];
            y[n] = acc;
        }
        return y;
    }

    public static double Rms(double[] x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s / x.Length);
    }

    public static double RmsDb(double[] x) => 20.0 * Math.Log10(Rms(x) + 1e-30);

    public static double Norm(ReadOnlySpan<double> x)
    {
        double s = 0;
        foreach (var v in x) s += v * v;
        return Math.Sqrt(s);
    }

    public static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    public static Complex[] Fft(double[] x, int n)
    {
        var z = new Complex[n];
        for (int i = 0; i < Math.Min(n, x.Length); i++) z[i] = x[i];
        Fourier.Forward(z, FourierOptions.Matlab);
        return z;
    }

    public static Complex[] Ifft(Complex[] x)
    {
        var z = (Complex[])x.Clone();
        Fourier.Inverse(z, FourierOptions.Matlab);
        return z;
    }

    public static double[] ToDouble(float[] x)
    {
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i];
        return y;
    }

    public static float[] ToFloat(double[] x)
    {
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (float)x[i];
        return y;
    }
}
