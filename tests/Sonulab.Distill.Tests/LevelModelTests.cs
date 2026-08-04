namespace Sonulab.Distill.Tests;

public class LevelModelTests
{
    // A minimal in-range amp model: a unit impulse pre-FIR, a scaled impulse cab, no
    // nonlinearity. Encoding then decoding it exercises the same path a device blob takes.
    static byte[] Slot(double cabGain = 1.0)
    {
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = (float)cabGain;
        return VxampCodec.Encode(new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                                               VxampFormat.NlmixHeaderFloats(), 0f));
    }

    // A memory-bearing amp model: a decaying-exponential pre-FIR and cab (a few dozen non-zero
    // taps each) plus a real nonlinearity, unlike the single-tap/nlmix=0 toy fixture above. This
    // is the shape a real amp model actually has (1024-tap pre-FIR + 1024-tap cab), so it
    // exercises the FIR-convolution code paths Task 5's real presets will take.
    static byte[] SlotWithMemory()
    {
        var pre = new float[1024];
        for (int i = 0; i < 32; i++) pre[i] = (float)(0.5 * Math.Pow(0.85, i));
        var g2 = new float[1024];
        for (int i = 0; i < 32; i++) g2[i] = (float)(0.3 * Math.Pow(0.8, i));
        return VxampCodec.Encode(new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2,
                                               VxampFormat.NlmixHeaderFloats(), 0.3f));
    }

    // An IR with a decaying tail rather than a single delta tap.
    static byte[] IrWithMemory()
    {
        var ir = new double[IrFormat.SampleCount];
        for (int i = 0; i < 64; i++) ir[i] = 0.2 * Math.Pow(0.9, i);
        return IrFormat.Encode(ir);
    }

    static Dictionary<string, string> Values(params (string Path, string Json)[] overrides)
    {
        var v = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [@"root\app\amp\on_off"] = "\"ON\"",
            [@"root\app\amp\gain"] = "0.0",
            [@"root\app\amp\vol"] = "50.0",
            [@"root\app\amp\sag"] = "0.0",
            [@"root\app\amp\depth"] = "5.0",
            [@"root\app\amp\presence"] = "0.0",
            [@"root\app\eq\low"] = "0.0",
            [@"root\app\eq\mid"] = "0.0",
            [@"root\app\eq\treble"] = "0.0",
            [@"root\app\eq\level"] = "0.0",
            [@"root\app\ir\on_off"] = "\"OFF\"",
            [@"root\app\ir\ir2\on_off"] = "\"OFF\"",
            [@"root\app\comp\on_off"] = "\"OFF\"",
            [@"root\app\gate\on_off"] = "\"OFF\"",
            [@"root\app\mod\on_off"] = "\"OFF\"",
            [@"root\app\delay\on_off"] = "\"OFF\"",
            [@"root\app\reverb\on_off"] = "\"OFF\"",
            [@"root\app\output\pst\level"] = "0.0",
        };
        foreach (var (p, j) in overrides) v[p] = j;
        return v;
    }

    static Dictionary<string, double> Defaults() => new(StringComparer.Ordinal)
    {
        [@"root\app\amp\vol"] = 50.0,
        [@"root\app\amp\sag"] = 0.0,
        [@"root\app\amp\depth"] = 5.0,
        [@"root\app\amp\presence"] = 0.0,
        [@"root\app\eq\low"] = 0.0,
        [@"root\app\eq\mid"] = 0.0,
        [@"root\app\eq\treble"] = 0.0,
    };

    static PresetLevelEstimate Est(Dictionary<string, string> v, byte[]? slot = null) =>
        LevelModel.Estimate(v, slot ?? Slot(), null, null, Defaults());

    [Fact]
    public void Eq_level_moves_the_estimate_by_exactly_that_many_db()
    {
        double flat = Est(Values()).RelativeLufs;
        double lifted = Est(Values((@"root\app\eq\level", "6.0"))).RelativeLufs;
        Assert.Equal(6.0, lifted - flat, 6);
    }

    [Fact]
    public void Amp_gain_moves_the_estimate_by_exactly_that_many_db_when_linear()
    {
        // nlmix = 0 in the fixture, so the chain is linear and input gain passes straight through.
        double flat = Est(Values()).RelativeLufs;
        double driven = Est(Values((@"root\app\amp\gain", "3.0"))).RelativeLufs;
        Assert.Equal(3.0, driven - flat, 6);
    }

    [Fact]
    public void Preset_level_does_not_move_the_estimate_but_is_reported()
    {
        var e = Est(Values((@"root\app\output\pst\level", "-4.5")));
        Assert.Equal(Est(Values()).RelativeLufs, e.RelativeLufs, 9);
        Assert.Equal(-4.5, e.CurrentTrimDb, 9);
    }

    [Fact]
    public void A_louder_cab_reads_louder()
    {
        Assert.True(Est(Values(), Slot(2.0)).RelativeLufs > Est(Values(), Slot(1.0)).RelativeLufs);
    }

    [Fact]
    public void Amp_volume_at_default_raises_no_flag_and_off_default_does()
    {
        Assert.DoesNotContain(LevelModel.AmpVolFlag, Est(Values()).Unmodeled);
        Assert.Contains(LevelModel.AmpVolFlag, Est(Values((@"root\app\amp\vol", "75.0"))).Unmodeled);
    }

    [Fact]
    public void Amp_volume_parked_at_zero_reads_as_silence()
    {
        // AmpVolGainDb(0) takes the percent<=0 branch (-120 dB), driving the ~-10.5 dBFS drive
        // signal down to roughly -130 dBFS — comfortably under Loudness' -70 LUFS absolute gate.
        // This branch was untested and is the root cause of Task 5's NaN-proposal bug: if BOTH
        // sides of a match estimate as -Infinity, the match arithmetic computes
        // -Infinity - (-Infinity), which is NaN.
        var e = Est(Values((@"root\app\amp\vol", "0.0")));
        Assert.True(double.IsNegativeInfinity(e.RelativeLufs));
    }

    [Fact]
    public void Amp_volume_follows_the_documented_taper()
    {
        // 100 % is +6.02 dB relative to the 50 % default under 20*log10(pct/50).
        double at50 = Est(Values()).RelativeLufs;
        double at100 = Est(Values((@"root\app\amp\vol", "100.0"))).RelativeLufs;
        Assert.Equal(6.020599913279624, at100 - at50, 6);
    }

    [Theory]
    [InlineData(@"root\app\comp\on_off", "Compressor")]
    [InlineData(@"root\app\reverb\on_off", "Reverb")]
    [InlineData(@"root\app\delay\on_off", "Delay")]
    [InlineData(@"root\app\mod\on_off", "Modulation")]
    [InlineData(@"root\app\gate\on_off", "Noise gate")]
    public void An_unmodeled_block_that_is_on_raises_a_flag(string path, string label)
    {
        var e = Est(Values((path, "\"ON\"")));
        Assert.Contains(e.Unmodeled, f => f.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_non_flat_eq_band_raises_one_flag_not_three()
    {
        var e = Est(Values((@"root\app\eq\low", "4.0"), (@"root\app\eq\treble", "-2.0")));
        Assert.Single(e.Unmodeled, f => f.Contains("EQ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_amp_knob_off_default_raises_a_flag()
    {
        Assert.Contains(Est(Values((@"root\app\amp\sag", "0.5"))).Unmodeled,
                        f => f.Contains("Sag", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Amp_switched_off_is_flagged_and_still_returns_a_number()
    {
        var e = Est(Values((@"root\app\amp\on_off", "\"OFF\"")));
        Assert.Contains(e.Unmodeled, f => f.Contains("Amp block is off", StringComparison.Ordinal));
        Assert.True(double.IsFinite(e.RelativeLufs));
    }

    [Fact]
    public void A_missing_amp_blob_is_flagged_rather_than_throwing()
    {
        var e = LevelModel.Estimate(Values(), Array.Empty<byte>(), null, null, Defaults());
        Assert.Contains(e.Unmodeled, f => f.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void An_enabled_ir_convolves_and_changes_the_estimate()
    {
        var ir = new double[IrFormat.SampleCount];
        ir[0] = 0.5;
        var withIr = LevelModel.Estimate(Values((@"root\app\ir\on_off", "\"ON\"")),
                                         Slot(), IrFormat.Encode(ir), null, Defaults());
        // A 0.5-tap impulse IR is exactly -6.02 dB.
        Assert.Equal(-6.020599913279624, withIr.RelativeLufs - Est(Values()).RelativeLufs, 4);
    }

    [Fact]
    public void A_pure_gain_term_moves_the_estimate_by_exactly_that_many_db_through_a_memory_bearing_chain()
    {
        // EQ Level is scaled in strictly after the (nonlinear, memory-bearing) amp sim and
        // strictly before the (memory-bearing) IR convolution, so it is a uniform scalar
        // multiply on the whole waveform regardless of what surrounds it — the property that
        // actually matters for Task 5's "make this preset as loud as that one" arithmetic.
        var slot = SlotWithMemory();
        var ir = IrWithMemory();
        var flat = Values((@"root\app\ir\on_off", "\"ON\""));
        var lifted = Values((@"root\app\ir\on_off", "\"ON\""), (@"root\app\eq\level", "4.0"));

        double a = LevelModel.Estimate(flat, slot, ir, null, Defaults()).RelativeLufs;
        double b = LevelModel.Estimate(lifted, slot, ir, null, Defaults()).RelativeLufs;
        Assert.Equal(4.0, b - a, 6);
    }

    [Fact]
    public void InputPaths_covers_every_path_the_model_reads()
    {
        foreach (var p in Values().Keys) Assert.Contains(p, LevelModel.InputPaths);
    }
}
