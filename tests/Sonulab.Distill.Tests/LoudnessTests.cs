namespace Sonulab.Distill.Tests;

public class LoudnessTests
{
    const int Fs = 44100;

    static double[] Sine(double hz, double amplitude, int seconds = 3) =>
        SineSamples(hz, amplitude, Fs * seconds);

    static double[] SineSamples(double hz, double amplitude, int samples)
    {
        var x = new double[samples];
        for (int i = 0; i < samples; i++)
            x[i] = amplitude * Math.Sin(2 * Math.PI * hz * i / Fs);
        return x;
    }

    // The K-weighting high shelf is +4 dB in the limit, and a bilinear-transformed shelf hits
    // exactly Vh at Nyquist. An alternating +/-1 signal IS Nyquist, so the steady-state output
    // amplitude pins the shelf coefficients.
    //
    // Tolerance, not equality: the second stage's numerator is [1,-2,1] UN-normalized (the
    // BS.1770 tabulated form, which pyloudnorm and ffmpeg both reproduce), so the 38 Hz
    // high-pass contributes a further factor of a0 = 1 + k/Q + k^2 ~= 1.0054 at Nyquist.
    // 1 % is tight enough to catch a wrong shelf gain — that error would be in dB, not 0.5 %.
    [Fact]
    public void KWeight_at_nyquist_reaches_the_shelf_gain()
    {
        var x = new double[4096];
        for (int i = 0; i < x.Length; i++) x[i] = i % 2 == 0 ? 1.0 : -1.0;
        var y = Loudness.KWeight(x);
        // Sample well past the transient.
        double settled = Math.Abs(y[^1]);
        Assert.InRange(settled, Loudness.ShelfGain * 0.99, Loudness.ShelfGain * 1.01);
    }

    // The second stage is a 38 Hz high-pass: a DC input must decay to nothing.
    [Fact]
    public void KWeight_removes_dc()
    {
        var x = Enumerable.Repeat(1.0, Fs).ToArray();
        var y = Loudness.KWeight(x);
        Assert.True(Math.Abs(y[^1]) < 1e-3, $"DC survived: {y[^1]}");
    }

    // Definitional: the measure is 10*log10 of a mean square, so a gain of g on the
    // input must move it by exactly 20*log10(g). This is the sharp regression test.
    [Fact]
    public void Lufs_tracks_gain_exactly()
    {
        var x = Sine(1000, 0.5);
        double a = Loudness.IntegratedLufs(x);
        double b = Loudness.IntegratedLufs(x.Select(v => v * 0.5).ToArray());
        Assert.Equal(-6.020599913279624, b - a, 9);
    }

    // K-weighting de-emphasizes bass and lifts treble: equal-RMS sines must order by frequency.
    [Fact]
    public void Lufs_orders_by_frequency()
    {
        double low = Loudness.IntegratedLufs(Sine(100, 0.5));
        double mid = Loudness.IntegratedLufs(Sine(1000, 0.5));
        double high = Loudness.IntegratedLufs(Sine(6000, 0.5));
        Assert.True(low < mid, $"100 Hz {low} should read below 1 kHz {mid}");
        Assert.True(mid < high, $"1 kHz {mid} should read below 6 kHz {high}");
    }

    // At 1 kHz the weighting is nearly flat, so the reading sits close to the dBFS RMS.
    // Loose on purpose: the sharp coefficient checks are the Nyquist/DC tests above.
    [Fact]
    public void Lufs_is_near_rms_db_at_1k()
    {
        var x = Sine(1000, 0.5);
        double rms = Dsp.RmsDb(x);
        Assert.InRange(Loudness.IntegratedLufs(x), rms - 1.5, rms + 1.5);
    }

    [Fact]
    public void Lufs_of_silence_is_negative_infinity()
    {
        Assert.Equal(double.NegativeInfinity, Loudness.IntegratedLufs(new double[Fs]));
    }

    // A signal shorter than one block is measured as a single block spanning the whole thing
    // (see IntegratedLufs) rather than short-circuited — but the absolute gate still applies,
    // so a short signal that is silent must still read -Infinity.
    [Fact]
    public void Lufs_of_a_short_silent_signal_is_still_negative_infinity()
    {
        Assert.Equal(double.NegativeInfinity, Loudness.IntegratedLufs(new double[100]));
    }

    // A stationary tone's mean square doesn't depend on how much of it you look at, once the
    // K-weighting filters have settled (tens of ms for these low-order biquads). The short
    // single-block path must converge to the same reading as the long multi-block gated
    // average of the identical continuous tone — this is the property the whole single-block
    // fallback exists to preserve.
    [Fact]
    public void Lufs_of_a_short_signal_matches_what_it_converges_to_once_longer()
    {
        var shortTone = SineSamples(1000, 0.5, 8000);   // ~181 ms, under the 400 ms block
        var longTone = Sine(1000, 0.5);                 // 3 s of the identical continuous tone
        Assert.Equal(Loudness.IntegratedLufs(longTone), Loudness.IntegratedLufs(shortTone), 2);
    }
}
