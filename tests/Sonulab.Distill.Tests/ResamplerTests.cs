namespace Sonulab.Distill.Tests;

public class ResamplerTests
{
    [Fact]
    public void BesselI0_matches_reference_values()
    {
        Assert.Equal(1.0, Resampler.BesselI0(0.0), 15);
        Assert.Equal(27.239871823604442, Resampler.BesselI0(5.0), 9);   // scipy.special.i0(5)
    }

    [Fact]
    public void Firwin_center_tap_and_dc_gain_match_scipy()
    {
        // firwin(2*10*160+1, 1/160, window=('kaiser', 5.0)) — the exact filter
        // resample_poly designs for 44.1k<->48k. Values from scipy 1.17.1.
        var h = Resampler.Firwin(3201, 1.0 / 160.0);
        Assert.Equal(0.006254219468524473, h[1600], 12);   // center tap
        Assert.Equal(1.0, h.Sum(), 9);                     // DC gain scaled to 1
    }

    [Fact]
    public void Upfirdn_output_length_formula()
    {
        var y = Resampler.Upfirdn(new double[21], new double[100], 3, 2);
        Assert.Equal(((100 - 1) * 3 + 21 - 1) / 2 + 1, y.Length);   // == 159
    }

    [Fact]
    public void ResamplePoly_matches_scipy_golden()
    {
        // x = (arange(16) - 7.5) / 8.0; resample_poly(x, 3, 2) — scipy 1.17.1 output.
        var x = Enumerable.Range(0, 16).Select(i => (i - 7.5) / 8.0).ToArray();
        var expected = new[]
        {
            -0.9380682877066662, -0.9580548735963454, -0.7050461858662392,
            -0.6879167443182218, -0.6389696871368792, -0.4960984338170718,
            -0.43776520092977755, -0.36892175617266115, -0.26110131061571223,
            -0.18761365754133325, -0.10769739772373164, -0.0201632805243235,
            0.06253788584711109, 0.15085305426217982, 0.22118627998175916,
            0.31268942923555537, 0.4126946933836402, 0.45801864196656006,
            0.5628409726239997, 0.6861745164875451, 0.6746504237128529,
            0.812992516012444, 1.0331231942120538, 0.6604326293750675,
        };
        var y = Resampler.ResamplePoly(x, 3, 2);
        Assert.Equal(expected.Length, y.Length);
        for (int i = 0; i < y.Length; i++) Assert.Equal(expected[i], y[i], 10);
    }

    [Fact]
    public void ResamplePoly_reduces_by_gcd_and_handles_identity()
    {
        var x = new double[] { 1, 2, 3, 4 };
        Assert.Equal(x, Resampler.ResamplePoly(x, 5, 5));          // up==down -> copy
        // 44100 -> 48000 reduces to 160/147 internally: output length ceil(4*160/147) = 5
        Assert.Equal(5, Resampler.ResamplePoly(x, 48000, 44100).Length);
    }
}
