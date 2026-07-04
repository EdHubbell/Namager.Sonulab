namespace Sonulab.Distill.Tests;

public class DeviceSimTests
{
    private static WhTensors Delta(float preGain = 1f, float g2Gain = 1f, float nlmix = 0f)
    {
        var pre = new float[1024]; pre[0] = preGain;
        var g2 = new float[1024]; g2[0] = g2Gain;
        return new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                             VxampFormat.NlmixHeaderFloats(), nlmix);
    }

    [Fact]
    public void Delta_cascade_is_identity()
    {
        var x = new float[] { 0.1f, -0.5f, 0.25f, 0f };
        var y = DeviceSim.Simulate(Delta(), x);
        Assert.Equal(x, y);
    }

    [Fact]
    public void Gains_multiply_through_the_cascade()
    {
        var x = new float[] { 1f, 0f, 0f };
        var y = DeviceSim.Simulate(Delta(preGain: 2f, g2Gain: 3f), x);
        Assert.Equal(6f, y[0], 1e-5f);
    }

    [Fact]
    public void Nlmix_zero_equals_linear_path_exactly()
    {
        var t = Delta(preGain: 1.5f, g2Gain: 0.8f, nlmix: 0f);
        var x = Enumerable.Range(0, 500).Select(i => (float)Math.Sin(i * 0.13) * 0.4f).ToArray();
        Assert.Equal(DeviceSim.SimulateLinear(t, x), DeviceSim.Simulate(t, x));
    }

    [Fact]
    public void Nonzero_nlmix_changes_output()
    {
        var t = Delta(nlmix: 0.5f);
        var x = Enumerable.Range(0, 500).Select(i => (float)Math.Sin(i * 0.13)).ToArray();
        Assert.NotEqual(DeviceSim.SimulateLinear(t, x), DeviceSim.Simulate(t, x));
    }

    [Fact]
    public void LinearIr_is_full_convolution()
    {
        var t = Delta(preGain: 2f, g2Gain: 0.5f);
        var ir = DeviceSim.LinearIr(t);
        Assert.Equal(2047, ir.Length);
        Assert.Equal(1f, ir[0], 1e-6f);
        Assert.All(ir.Skip(1), v => Assert.Equal(0f, v));
    }
}
