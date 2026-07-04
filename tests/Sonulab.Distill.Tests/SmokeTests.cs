namespace Sonulab.Distill.Tests;

public class SmokeTests
{
    [Fact]
    public void MathNet_Fft_Matlab_convention_matches_numpy()
    {
        // numpy: fft([1,0,0,0]) == [1,1,1,1] (unscaled forward)
        var z = new System.Numerics.Complex[] { 1, 0, 0, 0 };
        MathNet.Numerics.IntegralTransforms.Fourier.Forward(
            z, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
        Assert.All(z, c => Assert.Equal(1.0, c.Real, 12));
    }
}
