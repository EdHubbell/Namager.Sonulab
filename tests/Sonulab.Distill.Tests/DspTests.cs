using System.Numerics;

namespace Sonulab.Distill.Tests;

public class DspTests
{
    [Fact]
    public void Convolve_matches_numpy_full()
    {
        // np.convolve([1,2,3],[0,1,0.5]) == [0, 1, 2.5, 4, 1.5]
        var y = Dsp.Convolve(new double[] { 1, 2, 3 }, new double[] { 0, 1, 0.5 });
        Assert.Equal(new double[] { 0, 1, 2.5, 4, 1.5 }, y.Select(v => Math.Round(v, 12)).ToArray());
    }

    [Fact]
    public void FirFilter_is_causal_same_length()
    {
        // lfilter([1, -0.5], 1, [1, 0, 0, 2]) == [1, -0.5, 0, 2]
        var y = Dsp.FirFilter(new double[] { 1, -0.5 }, new double[] { 1, 0, 0, 2 });
        Assert.Equal(4, y.Length);
        Assert.Equal(new double[] { 1, -0.5, 0, 2 }, y.Select(v => Math.Round(v, 12)).ToArray());
    }

    [Fact]
    public void RmsDb_matches_python()
    {
        // _rms_db(np.full(100, 0.5)) == 20*log10(0.5) == -6.020599913279624
        var x = Enumerable.Repeat(0.5, 100).ToArray();
        Assert.Equal(-6.020599913279624, Dsp.RmsDb(x), 10);
    }

    [Fact]
    public void Fft_ifft_roundtrip_and_convention()
    {
        var x = new double[] { 3, 1, -2, 5, 0, 0, 0, 0 };
        var z = Dsp.Fft(x, 8);
        Assert.Equal(x.Sum(), z[0].Real, 12);          // DC bin = sum (unscaled forward)
        var back = Dsp.Ifft(z);
        for (int i = 0; i < 8; i++) Assert.Equal(x[i], back[i].Real, 12);
    }
}
