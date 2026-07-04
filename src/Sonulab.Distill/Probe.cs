namespace Sonulab.Distill;

/// <summary>Anything that can run audio through a NAM model (implemented by NamModel;
/// faked in tests). Process is float32 in/out, same length, causal, prewarmed, DC-removed.</summary>
public interface INamProcessor
{
    float[] Process(float[] x);
    int? SampleRate { get; }
}

/// <summary>Response prober (port of tools/distiller/probe.py).</summary>
public static class Probe
{
    public static float[] LinearIrOfModel(INamProcessor model, int n = 4096, double amp = 1e-3)
    {
        var x = new float[n];
        x[0] = (float)amp;
        var y = model.Process(x);
        var outIr = new float[n];
        for (int i = 0; i < n; i++) outIr[i] = (float)(y[i] / amp);
        return outIr;
    }

    public static double[] Logmag(double[] ir, int nFft = 4096)
    {
        var z = Dsp.Fft(ir, nFft);
        var lm = new double[nFft / 2 + 1];
        for (int i = 0; i < lm.Length; i++)
            lm[i] = 20.0 * Math.Log10(Math.Max(z[i].Magnitude, 1e-12));
        return lm;
    }

    public static double LogmagCorr(double[] aIr, double[] bIr, int nFft = 4096)
    {
        var a = Logmag(aIr, nFft);
        var b = Logmag(bIr, nFft);
        double ma = a.Average(), mb = b.Average();
        double sa = Math.Sqrt(a.Sum(v => (v - ma) * (v - ma)) / a.Length);
        double sb = Math.Sqrt(b.Sum(v => (v - mb) * (v - mb)) / b.Length);
        if (sa < 1e-12 || sb < 1e-12) return 0.0;
        double cov = 0;
        for (int i = 0; i < a.Length; i++) cov += (a[i] - ma) * (b[i] - mb);
        cov /= a.Length;
        double corr = cov / (sa * sb);
        return double.IsFinite(corr) ? corr : 0.0;
    }
}
