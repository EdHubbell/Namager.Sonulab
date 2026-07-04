namespace Sonulab.Distill.Tests;

public class NonlinearityTests
{
    [Fact]
    public void Scalar_zero_is_exact_identity()
    {
        var x = new double[] { 0.5, -1.25, 3.75e-8, 0 };
        var y = Nonlinearity.ApplyNl(x, 0.0);
        Assert.Equal(x, y);
        Assert.NotSame(x, y);   // a copy, like xf.copy()
    }

    [Fact]
    public void Silence_returns_copy()
    {
        var x = new double[64];   // rms < 1e-12
        Assert.Equal(x, Nonlinearity.ApplyNl(x, 0.5));
    }

    [Fact]
    public void Formula_matches_python()
    {
        // x = [1.0, -2.0]; r = sqrt(mean(x^2)) = sqrt(2.5); s = 0.4
        // y = 0.6*x + 0.4*r*tanh(x/r)
        var x = new double[] { 1.0, -2.0 };
        double r = Math.Sqrt(2.5);
        var y = Nonlinearity.ApplyNl(x, 0.4);
        Assert.Equal(0.6 * 1.0 + 0.4 * r * Math.Tanh(1.0 / r), y[0], 14);
        Assert.Equal(0.6 * -2.0 + 0.4 * r * Math.Tanh(-2.0 / r), y[1], 14);
    }

    [Fact]
    public void Compresses_peaks()
    {
        var x = Enumerable.Range(0, 1000).Select(i => Math.Sin(i * 0.1) * 2).ToArray();
        double p0 = Nonlinearity.ApplyNl(x, 0.0).Max(Math.Abs);
        double p5 = Nonlinearity.ApplyNl(x, 0.5).Max(Math.Abs);
        Assert.True(p5 < p0);
    }
}
