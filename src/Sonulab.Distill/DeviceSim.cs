namespace Sonulab.Distill;

/// <summary>Device forward model (port of tools/distiller/device_sim.py):
/// y = g2_fir ⊛ nl(pre_fir ⊛ x). Float32 is materialized after every stage,
/// mirroring the Python dtype flow (parity-critical).</summary>
public static class DeviceSim
{
    public const int SampleRate = 44100;

    public static float[] Simulate(WhTensors t, float[] x) =>
        SimulateCore(t, x, mid => Nonlinearity.ApplyNl(mid, t.Nlmix));

    public static float[] SimulateLinear(WhTensors t, float[] x) =>
        SimulateCore(t, x, mid => mid);

    private static float[] SimulateCore(WhTensors t, float[] x, Func<double[], double[]> nl)
    {
        // Stage 1: pre_fir (float32 materialized, like _apply_fir)
        var mid32 = Dsp.ToFloat(Dsp.FirFilter(Dsp.ToDouble(t.PreFir), Dsp.ToDouble(x)));
        // Stage 2: nonlinearity (float64 in, float32 out, like apply_nl(...).astype(float32))
        var nl32 = Dsp.ToFloat(nl(Dsp.ToDouble(mid32)));
        // Stage 3: g2_fir
        return Dsp.ToFloat(Dsp.FirFilter(Dsp.ToDouble(t.G2Fir), Dsp.ToDouble(nl32)));
    }

    public static float[] LinearIr(WhTensors t) =>
        Dsp.ToFloat(Dsp.Convolve(Dsp.ToDouble(t.PreFir), Dsp.ToDouble(t.G2Fir)));
}
