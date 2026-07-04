namespace Sonulab.Distill.Tests;

file sealed class FakeGainModel(float gain) : INamProcessor
{
    public int? SampleRate => 48000;
    public float[] Process(float[] x) => x.Select(v => v * gain).ToArray();
}

public class ProbeTests
{
    [Fact]
    public void LinearIr_normalizes_out_the_probe_amplitude()
    {
        var ir = Probe.LinearIrOfModel(new FakeGainModel(3f), n: 64);
        Assert.Equal(64, ir.Length);
        Assert.Equal(3f, ir[0], 1e-4f);       // gain-3 model -> IR = 3*delta
        Assert.All(ir.Skip(1), v => Assert.Equal(0f, v, 1e-4f));
    }

    [Fact]
    public void Logmag_of_delta_is_flat_zero_db()
    {
        var ir = new double[16]; ir[0] = 1.0;
        var lm = Probe.Logmag(ir, 4096);
        Assert.Equal(4096 / 2 + 1, lm.Length);
        Assert.All(lm, v => Assert.Equal(0.0, v, 9));
    }

    [Fact]
    public void LogmagCorr_identical_is_one_scaled_is_one()
    {
        var rng = new Random(7);
        var ir = Enumerable.Range(0, 512).Select(_ => rng.NextDouble() - 0.5).ToArray();
        Assert.Equal(1.0, Probe.LogmagCorr(ir, ir), 9);
        // uniform gain shifts log-mag by a constant -> correlation still 1
        var scaled = ir.Select(v => v * 7.3).ToArray();
        Assert.Equal(1.0, Probe.LogmagCorr(ir, scaled), 9);
    }

    [Fact]
    public void LogmagCorr_degenerate_returns_zero()
    {
        // all-zero IR -> flat floored spectrum -> std < 1e-12 -> 0.0
        Assert.Equal(0.0, Probe.LogmagCorr(new double[64], new double[64] ));
    }
}
