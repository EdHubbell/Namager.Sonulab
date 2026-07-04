namespace Sonulab.Distill;

/// <summary>Gain-, polarity- and delay-invariant fidelity metric (port of the
/// metric half of tools/distiller/distill.py). C# computes our_err only — the
/// VoidX-pair comparisons remain Python-side analysis tooling.</summary>
public static class Fidelity
{
    public const int AlignMaxLag = 128;   // ±samples (~2.9 ms) searched for bulk delay

    public static int BestLag(double[] refY, double[] y, int maxLag = AlignMaxLag)
    {
        int bestLag = 0;
        double bestC = -1.0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            var (a, b) = Slice(refY, y, lag);
            double denom = Dsp.Norm(a) * Dsp.Norm(b);
            if (denom < 1e-30) continue;
            double c = Math.Abs(Dsp.Dot(a, b)) / denom;
            if (c > bestC) { bestC = c; bestLag = lag; }
        }
        return bestLag;
    }

    public static double AlignedNrmse(double[] refY, double[] y)
    {
        int n = Math.Min(refY.Length, y.Length);
        refY = refY[..n]; y = y[..n];
        int lag = BestLag(refY, y);
        var (a, b) = Slice(refY, y, lag);
        double aNrm = Dsp.Norm(a);
        double bSq = Dsp.Dot(b, b);
        if (aNrm < 1e-12 || bSq < 1e-24)
            return aNrm < 1e-12 && bSq < 1e-24 ? 0.0 : 1.0;
        double g = Dsp.Dot(a, b) / bSq;                 // signed gain (absorbs polarity)
        var diff = new double[a.Length];
        for (int i = 0; i < a.Length; i++) diff[i] = g * b[i] - a[i];
        return Dsp.Norm(diff) / aNrm;
    }

    /// <summary>lag >= 0: a = ref[lag:], b = y[:len(a)]; lag &lt; 0: a = ref[:lag], b = y[-lag:].</summary>
    private static (double[] a, double[] b) Slice(double[] refY, double[] y, int lag) =>
        lag >= 0
            ? (refY[lag..], y[..(refY.Length - lag)])
            : (refY[..^(-lag)], y[(-lag)..]);

    public static double ShapeErr(double[] refIr, double[] devIr,
                                  double[] refDriven, double[] devDriven) =>
        0.5 * ((1.0 - Probe.LogmagCorr(refIr, devIr)) + AlignedNrmse(refDriven, devDriven));

    /// <summary>NAM small-signal IR at the device rate (port of _nam_ir_dev).</summary>
    public static double[] NamIrDev(INamProcessor model)
    {
        var ir = Dsp.ToDouble(Probe.LinearIrOfModel(model, n: 8192));
        int sr = model.SampleRate ?? FirFitter.NamDefaultSampleRate;
        return sr != DeviceSim.SampleRate
            ? Resampler.ResamplePoly(ir, DeviceSim.SampleRate, sr)
            : ir;
    }

    public static double FidelityVsNam(INamProcessor model, WhTensors tensors)
    {
        var x = DriveSignal.Get();
        var namIr = NamIrDev(model);
        var namDriven = FirFitter.ModelRefAtDeviceRate(model, x);
        var ourIr = Dsp.ToDouble(DeviceSim.LinearIr(tensors));
        var ourDriven = Dsp.ToDouble(DeviceSim.Simulate(tensors, x));
        return ShapeErr(namIr, ourIr, namDriven, ourDriven);
    }
}
