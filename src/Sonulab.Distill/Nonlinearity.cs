namespace Sonulab.Distill;

/// <summary>The pinned firmware nonlinearity (port of tools/distiller/nonlinearity.py):
/// nl(u) = (1-s)*u + s*r*tanh(u/r), r = rms(u). s==0 is exact identity.</summary>
public static class Nonlinearity
{
    public static double[] ApplyNl(double[] x, double scalar)
    {
        if (scalar == 0.0) return (double[])x.Clone();
        double r = x.Length > 0 ? Dsp.Rms(x) : 0.0;
        if (r < 1e-12) return (double[])x.Clone();
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
            y[i] = (1.0 - scalar) * x[i] + scalar * r * Math.Tanh(x[i] / r);
        return y;
    }
}
