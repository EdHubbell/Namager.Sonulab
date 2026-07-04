namespace Sonulab.Distill.Tests;

/// <summary>Linear gain "model" at the device rate — no resampling, no nonlinearity.</summary>
file sealed class LinearDeviceRateModel(float gain) : INamProcessor
{
    public int? SampleRate => 44100;
    public float[] Process(float[] x) => x.Select(v => v * gain).ToArray();
}

public class FirFitterTests
{
    [Fact]
    public void CascadeErr_zero_for_exact_split()
    {
        var pre = new double[1024]; pre[0] = 1.0;
        var target = new double[2047]; target[0] = 0.5; target[100] = -0.25;
        var g2 = target[..1024];
        Assert.Equal(0.0, FirFitter.CascadeErr(pre, g2, target), 12);
    }

    [Fact]
    public void DesignLinear_reproduces_a_short_ir()
    {
        // IR fully inside 1024 taps -> the delta split is exact
        var ir = new double[2047];
        ir[0] = 1.2; ir[3] = -0.4; ir[900] = 0.05;
        var (pre, g2) = FirFitter.DesignLinear(ir);
        Assert.Equal(1024, pre.Length);
        Assert.Equal(1024, g2.Length);
        Assert.All(pre.Skip(FirFitter.PreZeroTail), v => Assert.Equal(0f, v));   // corpus invariant
        var cascade = Dsp.Convolve(Dsp.ToDouble(pre), Dsp.ToDouble(g2));
        Assert.True(FirFitter.CascadeErr(Dsp.ToDouble(pre), Dsp.ToDouble(g2), ir) < 1e-6);
    }

    [Fact]
    public void FitNl_snaps_to_zero_for_a_linear_model()
    {
        var model = new LinearDeviceRateModel(2.0f);
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = 2f;   // cascade == model -> already exact
        var (s, gain) = FirFitter.FitNl(model, pre, g2);
        Assert.Equal(0.0, s);
        Assert.Equal(1.0, gain, 2);
    }

    [Fact]
    public void FitWh_on_synthetic_fixture_returns_valid_tensors()
    {
        var model = NamParser.Load(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic.nam"));
        var t = FirFitter.FitWh(model);
        Assert.Equal(1024, t.PreFir.Length);
        Assert.Equal(1024, t.G2Fir.Length);
        Assert.All(t.PreFir.Skip(FirFitter.PreZeroTail), v => Assert.Equal(0f, v));
        Assert.InRange(t.Nlmix, 0f, (float)FirFitter.NlmixMax);
        Assert.Equal(VxampFormat.G2HeaderFloats(), t.G2Header);
        Assert.Equal(VxampFormat.NlmixHeaderFloats(), t.NlmixHeader);
    }

    [Fact]
    public void DriveSignal_is_the_embedded_reference()
    {
        var x = DriveSignal.Get();
        Assert.Equal(16000, x.Length);
        Assert.Equal(0.3, Dsp.Rms(Dsp.ToDouble(x)), 2);
    }
}
