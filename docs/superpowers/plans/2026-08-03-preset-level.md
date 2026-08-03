# Preset Level Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the pedal's per-preset output trim (`root\app\output\pst\level`) visible as the top section of the parameter editor, and add a one-click action that computes the trim needed to match another preset's loudness.

**Architecture:** Three layers. `Sonulab.Distill` gains a pure BS.1770 loudness meter and a preset-chain level model built on the existing `VxampCodec`/`DeviceSim`/`Dsp` DSP. `Namager.App` gains a per-device disk cache of amp-model loudness and a `Level` block synthesized into `ParameterEditorViewModel` from an explicit node path. The match action is a view-model command that takes a picker callback, so it is testable without a `Window`.

**Tech Stack:** .NET 10, C#, Avalonia 12 (built-in `FluentTheme`, `PathIcon` geometries only), CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-03-preset-level-design.md`

## Global Constraints

- **Do NOT add FluentAvalonia or any icon library.** Icons are `PathIcon` + `StreamGeometry` in `src/Namager.App/Icons.axaml`.
- **No hex colour literals in `.axaml`.** Use `Sonulab.*Brush` tokens from `Styles/SonulabTheme.axaml`.
- **`Sonulab.Distill` must not reference `Sonulab.Core`.** Verified in both `.csproj` files; new DSP takes plain values, never `PresetDocument`.
- **Do not change `Distiller.LoudnessNormalize` or `Distiller.DeviceReferenceDb`.** They hold RMS parity with the Python oracle (`tools/distiller/distill.py`); the new meter is K-weighted and separate.
- **`[RelayCommand] async` methods must not let exceptions escape** — an escape is an unhandled UI-thread rethrow, i.e. process death. Catch, log via NLog, set `ErrorMessage`, and call `_status.Failure`.
- Build: `dotnet build` · Test: `dotnet test` (490 tests pass before this work; all must still pass).
- Branch `feat/preset-level` already exists with the spec committed.

---

### Task 1: BS.1770 loudness meter

**Files:**
- Create: `src/Sonulab.Distill/Loudness.cs`
- Test: `tests/Sonulab.Distill.Tests/LoudnessTests.cs`

**Interfaces:**
- Consumes: `Dsp` (existing, `src/Sonulab.Distill/Dsp.cs`), `DeviceSim.SampleRate` (44100).
- Produces:
  - `Loudness.KWeight(double[] x) -> double[]`
  - `Loudness.IntegratedLufs(double[] x) -> double` (returns `double.NegativeInfinity` when every block is gated out)
  - `Loudness.ShelfGain` — the constant `1.5848931924611136` (+4 dB), used by tests

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Distill.Tests/LoudnessTests.cs`:

```csharp
namespace Sonulab.Distill.Tests;

public class LoudnessTests
{
    const int Fs = 44100;

    static double[] Sine(double hz, double amplitude, int seconds = 3)
    {
        var x = new double[Fs * seconds];
        for (int i = 0; i < x.Length; i++)
            x[i] = amplitude * Math.Sin(2 * Math.PI * hz * i / Fs);
        return x;
    }

    // The K-weighting high shelf is +4 dB in the limit, and for a bilinear-transformed
    // shelf the response AT Nyquist is exactly Vh. An alternating +/-1 signal IS Nyquist,
    // so the steady-state output amplitude pins the shelf coefficients exactly.
    [Fact]
    public void KWeight_at_nyquist_reaches_the_shelf_gain()
    {
        var x = new double[4096];
        for (int i = 0; i < x.Length; i++) x[i] = i % 2 == 0 ? 1.0 : -1.0;
        var y = Loudness.KWeight(x);
        // Sample well past the transient.
        Assert.Equal(Loudness.ShelfGain, Math.Abs(y[^1]), 3);
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
        Assert.InRange(Loudness.IntegratedLufs(x), rms - 1.0, rms + 1.0);
    }

    [Fact]
    public void Lufs_of_silence_is_negative_infinity()
    {
        Assert.Equal(double.NegativeInfinity, Loudness.IntegratedLufs(new double[Fs]));
    }

    [Fact]
    public void Lufs_of_a_signal_shorter_than_one_block_is_negative_infinity()
    {
        Assert.Equal(double.NegativeInfinity, Loudness.IntegratedLufs(new double[100]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter LoudnessTests`
Expected: FAIL — `The name 'Loudness' does not exist in the current context` (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Sonulab.Distill/Loudness.cs`:

```csharp
namespace Sonulab.Distill;

/// <summary>ITU-R BS.1770 K-weighted loudness, mono, for comparing preset chains.
///
/// Separate from <see cref="Distiller.LoudnessNormalize"/> on purpose: that one measures
/// broadband RMS and must keep bit-parity with the Python oracle. This one exists to match
/// PERCEIVED level between a dark amp and a bright one, which broadband RMS gets wrong by a
/// couple of dB.
///
/// Coefficients are the analytic BS.1770 filter designs evaluated at our sample rate rather
/// than the standard's tabulated 48 kHz values (we run at 44100).
///
/// GATING: the absolute gate (-70 LUFS) only. The relative gate is deliberately omitted — the
/// input is a single fixed, continuously-excited drive signal, not program material with
/// silence to exclude, so the relative gate would add a discontinuity in the measure for no
/// benefit. Absolute values from this class are not comparable to a mastering meter's; only
/// DIFFERENCES between two chains are meaningful.</summary>
public static class Loudness
{
    public const int SampleRate = DeviceSim.SampleRate;   // 44100

    /// <summary>High-shelf gain in the limit, +3.999843853973347 dB as a linear ratio. A
    /// bilinear-transformed shelf hits exactly this at Nyquist, which is what pins the
    /// coefficients in the tests.</summary>
    public static readonly double ShelfGain = Math.Pow(10.0, ShelfGainDb / 20.0);

    private const double ShelfGainDb = 3.999843853973347;
    private const double ShelfQ = 0.7071752369554196;
    private const double ShelfFc = 1681.974450955533;
    private const double HpQ = 0.5003270373238773;
    private const double HpFc = 38.13547087602444;

    private const double BlockSeconds = 0.400;
    private const double HopSeconds = 0.100;              // 75 % overlap
    private const double LufsOffset = -0.691;             // BS.1770 channel-weighting offset
    private const double AbsoluteGateLufs = -70.0;

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    private static double[] Apply(Biquad f, double[] x)
    {
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = f.B0 * x[i] + f.B1 * x1 + f.B2 * x2 - f.A1 * y1 - f.A2 * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            y[i] = v;
        }
        return y;
    }

    private static Biquad HighShelf()
    {
        double k = Math.Tan(Math.PI * ShelfFc / SampleRate);
        double vh = Math.Pow(10.0, ShelfGainDb / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / ShelfQ + k * k;
        return new Biquad(
            (vh + vb * k / ShelfQ + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / ShelfQ + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / ShelfQ + k * k) / a0);
    }

    private static Biquad HighPass()
    {
        double k = Math.Tan(Math.PI * HpFc / SampleRate);
        double a0 = 1.0 + k / HpQ + k * k;
        return new Biquad(1.0, -2.0, 1.0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / HpQ + k * k) / a0);
    }

    /// <summary>Run the two K-weighting stages. Exposed so the filter itself is testable.</summary>
    public static double[] KWeight(double[] x) => Apply(HighPass(), Apply(HighShelf(), x));

    /// <summary>Integrated K-weighted loudness in LUFS, or <see cref="double.NegativeInfinity"/>
    /// when the signal is shorter than one 400 ms block or every block falls under the
    /// absolute gate.</summary>
    public static double IntegratedLufs(double[] x)
    {
        var y = KWeight(x);
        int block = (int)(BlockSeconds * SampleRate);
        int hop = (int)(HopSeconds * SampleRate);
        if (y.Length < block) return double.NegativeInfinity;

        double sum = 0;
        int kept = 0;
        for (int start = 0; start + block <= y.Length; start += hop)
        {
            double ms = 0;
            for (int i = start; i < start + block; i++) ms += y[i] * y[i];
            ms /= block;
            if (ms <= 0) continue;
            if (LufsOffset + 10.0 * Math.Log10(ms) <= AbsoluteGateLufs) continue;
            sum += ms;
            kept++;
        }
        return kept == 0 ? double.NegativeInfinity : LufsOffset + 10.0 * Math.Log10(sum / kept);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter LoudnessTests`
Expected: PASS, 7 tests.

If `KWeight_at_nyquist_reaches_the_shelf_gain` fails, the shelf coefficients are wrong — recheck `vb`'s exponent and the `a0` division, not the test.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS (497 tests: the previous 490 plus 7).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Distill/Loudness.cs tests/Sonulab.Distill.Tests/LoudnessTests.cs
git commit -m "feat(distill): BS.1770 K-weighted loudness meter"
```

---

### Task 2: Preset chain level model

**Files:**
- Create: `src/Sonulab.Distill/LevelModel.cs`
- Test: `tests/Sonulab.Distill.Tests/LevelModelTests.cs`

**Interfaces:**
- Consumes: `Loudness.IntegratedLufs` (Task 1); existing `DriveSignal.Get()`, `DeviceSim.Simulate`, `VxampCodec.Decode`/`Encode`, `VxampFormat.SlotSize`, `IrFormat.Decode`, `Dsp.ToDouble`/`ToFloat`/`FirFilter`, `WhTensors`.
- Produces:
  - `record PresetLevelEstimate(double RelativeLufs, double CurrentTrimDb, IReadOnlyList<string> Unmodeled)`
  - `LevelModel.Estimate(IReadOnlyDictionary<string,string> values, byte[] vxampSlot, byte[]? ir1, byte[]? ir2, IReadOnlyDictionary<string,double> defaults) -> PresetLevelEstimate`
  - `LevelModel.InputPaths -> IReadOnlyList<string>` — every node path the model reads; callers build `values` from it
  - `LevelModel.PresetLevelPath`, `LevelModel.AmpNamePath`, `LevelModel.IrNamePath`, `LevelModel.Ir2NamePath`, `LevelModel.AmpVolPath` — public consts
  - `LevelModel.AmpVolFlag` — the exact flag string the match command filters on
  - `LevelModel.AmpVolGainDb(double percent) -> double`

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Distill.Tests/LevelModelTests.cs`:

```csharp
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
    public void InputPaths_covers_every_path_the_model_reads()
    {
        foreach (var p in Values().Keys) Assert.Contains(p, LevelModel.InputPaths);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter LevelModelTests`
Expected: FAIL — `The name 'LevelModel' does not exist in the current context`.

If `VxampFormat.G2HeaderFloats()` / `NlmixHeaderFloats()` are not public, make them public in `src/Sonulab.Distill/VxampFormat.cs` — `Distiller.Fit` already calls them internally, so they are the canonical header values.

- [ ] **Step 3: Write the implementation**

Create `src/Sonulab.Distill/LevelModel.cs`:

```csharp
using System.Globalization;

namespace Sonulab.Distill;

/// <summary>What the model made of one preset. <paramref name="RelativeLufs"/> EXCLUDES the
/// preset's own output trim, so a caller can propose a new trim as plain arithmetic on top of
/// <paramref name="CurrentTrimDb"/>. Only DIFFERENCES between two estimates are meaningful —
/// the absolute number is not the pedal's real output level.</summary>
public sealed record PresetLevelEstimate(
    double RelativeLufs,
    double CurrentTrimDb,
    IReadOnlyList<string> Unmodeled);

/// <summary>Estimates a preset's output loudness offline by running the fixed distiller drive
/// signal through the parts of the pedal's chain we can derive from first principles, and
/// FLAGGING the parts we cannot rather than guessing at them.
///
/// Takes a plain path -> raw-JSON dictionary rather than a PresetDocument: Sonulab.Distill
/// deliberately has no reference to Sonulab.Core.</summary>
public static class LevelModel
{
    public const string PresetLevelPath = @"root\app\output\pst\level";
    public const string AmpNamePath = @"root\app\amp\amp";
    public const string AmpVolPath = @"root\app\amp\vol";
    public const string IrNamePath = @"root\app\ir\ir";
    public const string Ir2NamePath = @"root\app\ir\ir2\ir";

    private const string AmpOnOffPath = @"root\app\amp\on_off";
    private const string AmpGainPath = @"root\app\amp\gain";
    private const string EqLevelPath = @"root\app\eq\level";
    private const string IrOnOffPath = @"root\app\ir\on_off";
    private const string Ir2OnOffPath = @"root\app\ir\ir2\on_off";

    /// <summary>The exact flag text for an off-default amp Volume. Public because the assumed
    /// taper CANCELS out of a difference when two presets share a `vol` value, so the match
    /// command filters this one flag out in that case (see the spec).</summary>
    public const string AmpVolFlag = "Amp Volume is away from default (its taper is assumed)";

    /// <summary>Reference point for the amp Volume taper: the firmware default, treated as
    /// unity. THE ONE MODELING ASSUMPTION in this file — everything else is derived. Isolated
    /// here so a later calibration against the device VU meters replaces exactly one function.
    /// </summary>
    public const double AmpVolReferencePercent = 50.0;

    public static double AmpVolGainDb(double percent) =>
        percent <= 0 ? -120.0 : 20.0 * Math.Log10(percent / AmpVolReferencePercent);

    private static readonly string[] EqBandPaths =
        { @"root\app\eq\low", @"root\app\eq\mid", @"root\app\eq\treble" };

    private static readonly (string Path, string Label)[] UnmodeledBlocks =
    {
        (@"root\app\comp\on_off", "Compressor"),
        (@"root\app\gate\on_off", "Noise gate"),
        (@"root\app\mod\on_off", "Modulation"),
        (@"root\app\delay\on_off", "Delay"),
        (@"root\app\reverb\on_off", "Reverb"),
    };

    private static readonly (string Path, string Label)[] UnmodeledAmpKnobs =
    {
        (@"root\app\amp\sag", "Sag"),
        (@"root\app\amp\depth", "Depth"),
        (@"root\app\amp\presence", "Presence"),
    };

    /// <summary>Every node this model reads. Callers build their `values` dictionary from this
    /// list, so adding a term here cannot silently leave a caller feeding a stale set.</summary>
    public static IReadOnlyList<string> InputPaths { get; } = new[]
        {
            AmpOnOffPath, AmpGainPath, AmpVolPath, AmpNamePath,
            EqLevelPath, IrOnOffPath, IrNamePath, Ir2OnOffPath, Ir2NamePath,
            PresetLevelPath,
        }
        .Concat(EqBandPaths)
        .Concat(UnmodeledBlocks.Select(b => b.Path))
        .Concat(UnmodeledAmpKnobs.Select(k => k.Path))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static PresetLevelEstimate Estimate(
        IReadOnlyDictionary<string, string> values,
        byte[] vxampSlot,
        byte[]? ir1, byte[]? ir2,
        IReadOnlyDictionary<string, double> defaults)
    {
        var flags = new List<string>();
        double[] x = Dsp.ToDouble(DriveSignal.Get());

        bool ampOn = !IsOff(values, AmpOnOffPath);
        if (!ampOn) flags.Add("Amp block is off");

        if (ampOn)
        {
            if (vxampSlot.Length == VxampFormat.SlotSize)
            {
                x = Scale(x, Num(values, AmpGainPath, 0.0));
                x = Dsp.ToDouble(DeviceSim.Simulate(VxampCodec.Decode(vxampSlot), Dsp.ToFloat(x)));
            }
            else flags.Add("Amp model could not be read");
        }

        double volPct = Num(values, AmpVolPath, AmpVolReferencePercent);
        x = Scale(x, AmpVolGainDb(volPct));
        if (OffDefault(values, defaults, AmpVolPath)) flags.Add(AmpVolFlag);

        x = Scale(x, Num(values, EqLevelPath, 0.0));
        if (EqBandPaths.Any(p => OffDefault(values, defaults, p)))
            flags.Add("EQ bands are not flat (their curves are not modeled)");

        x = ApplyIr(x, values, IrOnOffPath, ir1, "IR", flags);
        x = ApplyIr(x, values, Ir2OnOffPath, ir2, "Second IR", flags);

        foreach (var (path, label) in UnmodeledBlocks)
            if (!IsOff(values, path)) flags.Add($"{label} is on (not modeled)");

        foreach (var (path, label) in UnmodeledAmpKnobs)
            if (OffDefault(values, defaults, path)) flags.Add($"{label} is away from default (not modeled)");

        return new PresetLevelEstimate(
            Loudness.IntegratedLufs(x), Num(values, PresetLevelPath, 0.0), flags);
    }

    private static double[] ApplyIr(double[] x, IReadOnlyDictionary<string, string> values,
                                    string onOffPath, byte[]? blob, string label, List<string> flags)
    {
        if (IsOff(values, onOffPath)) return x;
        if (blob is null || blob.Length != IrFormat.BlobBytes)
        {
            flags.Add($"{label} is on but could not be read");
            return x;
        }
        // lo_cut/hi_cut are separate filters we do not model; the convolution alone is the
        // dominant level term, so this is an approximation and says so.
        flags.Add($"{label} cab filters (lo/hi cut) are not modeled");
        return Dsp.FirFilter(IrFormat.Decode(blob), x);
    }

    private static double[] Scale(double[] x, double db)
    {
        if (db == 0.0) return x;
        double g = Math.Pow(10.0, db / 20.0);
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i] * g;
        return y;
    }

    private static double Num(IReadOnlyDictionary<string, string> v, string path, double fallback) =>
        v.TryGetValue(path, out var raw)
        && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    /// <summary>True when the node is absent or reads "OFF". Absent counts as off so a preset
    /// from firmware that lacks a block never invents a flag.</summary>
    private static bool IsOff(IReadOnlyDictionary<string, string> v, string path) =>
        !v.TryGetValue(path, out var raw) || raw.Trim().Trim('"').Equals("OFF", StringComparison.OrdinalIgnoreCase);

    private static bool OffDefault(IReadOnlyDictionary<string, string> v,
                                   IReadOnlyDictionary<string, double> defaults, string path)
    {
        if (!v.ContainsKey(path) || !defaults.TryGetValue(path, out var def)) return false;
        return Math.Abs(Num(v, path, def) - def) > 1e-9;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter LevelModelTests`
Expected: PASS, 17 tests (the `[Theory]` contributes 5).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Distill/LevelModel.cs tests/Sonulab.Distill.Tests/LevelModelTests.cs
git commit -m "feat(distill): offline preset-chain level model with honest flags"
```

---

### Correction to the spec: no `AmpLoudnessCache`

**The spec's `AmpLoudnessCache` does not work and is not being built.** The spec justified it as
"an amp blob is 96 chunks (~3 s), so cache the scalar." But `LevelModel.Estimate` needs the amp
**blob** — the model is nonlinear (`nlmix` sits mid-chain), so the amp's contribution is not a
scalar offset that can be added after the fact, and a cached loudness cannot short-circuit the
read. The cache would cost a file format and eleven tests while saving nothing.

What actually helps is a per-session in-memory blob cache, which is four lines inside
`ParameterEditorViewModel` (Task 4, Step 4). That is what gets built.

If the deferred bulk-normalize feature later wants a persistent cache, the correct shape is a
**preset-keyed estimate cache** — key `(deviceId, slot, presetName, hash of the level-relevant
parameter values)`, value the `RelativeLufs` — which short-circuits the entire chain rather than
one term of it. Record that in `docs/STATUS.md` (Task 6) rather than building it now.

---

### Task 3: The Level block in the parameter editor

**Files:**
- Modify: `src/Namager.App/ViewModels/BlockSectionViewModel.cs` (add `ShowLevelIcon` beside `ShowEqIcon` at line 31)
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs` (`LoadCoreAsync`, line 111-203)
- Modify: `src/Namager.App/labels.en.json`
- Test: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs` (add to the existing class)

**Interfaces:**
- Consumes: `LevelModel.PresetLevelPath` (Task 2).
- Produces: `vm.Blocks[0]` is the Level block when the browse response carries the node; `BlockSectionViewModel.ShowLevelIcon`.

- [ ] **Step 1: Write the failing tests**

The existing `Dev()` helper in `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs` already seeds `root\app\output\vol`. Add the two `pst` nodes to that seed, then add the new facts.

In `Dev()`, replace the last seeded record with these three:

```csharp
            // output block: the global Master leaves must still be skipped, `pst\level` must be
            // promoted into its own Level block, and `pst\tmp` must stay invisible.
            "root\\app\\output\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\output\\pst\\level:{\"desc\":\"Preset Level\",\"value\":-3.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0,\"unit\":\"dB\",\"dec\":1}",
            "root\\app\\output\\pst\\tmp:{\"desc\":\"Preset TEMPO\",\"value\":120.0,\"type\":\"float\",\"min\":30.0,\"max\":240.0,\"def\":120.0,\"unit\":\"BPM\",\"dec\":1}");
```

Add these facts to the class:

```csharp
    [Fact] public async Task Level_block_is_first_and_holds_only_the_preset_trim()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        var level = vm.Blocks[0];
        Assert.Equal("Level", level.Header);
        Assert.True(level.ShowLevelIcon);
        var field = Assert.Single(level.Fields);
        Assert.Equal(Sonulab.Distill.LevelModel.PresetLevelPath, field.Path);
        Assert.Equal(-3.0, field.Number);
        Assert.Equal(-20.0, field.Min);
        Assert.Equal(20.0, field.Max);
        Assert.True(field.ShowReset);
    }

    [Fact] public async Task Level_block_is_expanded_by_default()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.True(vm.Blocks[0].IsExpanded);
        Assert.All(vm.Blocks.Skip(1), b => Assert.False(b.IsExpanded));
    }

    [Fact] public async Task Preset_tempo_and_global_master_leaves_never_appear()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var all = vm.Blocks.SelectMany(b => b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields)));
        Assert.DoesNotContain(all, f => f.Path == @"root\app\output\pst\tmp");
        Assert.DoesNotContain(all, f => f.Path == @"root\app\output\vol");
    }

    [Fact] public async Task Firmware_without_a_preset_level_node_still_loads()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.DoesNotContain(vm.Blocks, b => b.Header == "Level");
        Assert.Contains(vm.Blocks, b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Editing_the_level_slider_dirties_the_editor_and_save_writes_it()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.False(vm.IsDirty);

        vm.Blocks[0].Fields[0].Number = -6.0;
        Assert.True(vm.IsDirty);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains(d.CommandLog, c => c.StartsWith(@"write root\app\output\pst\level:", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter ParameterEditorViewModelTests`
Expected: FAIL — `ShowLevelIcon` does not exist; the Level assertions fail because `Blocks[0]` is the amp block.

`Output_block_is_skipped` (the existing fact) must keep passing: it asserts no block whose `Header` equals `"output"`, and the new header is `"Level"`.

- [ ] **Step 3: Add `ShowLevelIcon`**

In `src/Namager.App/ViewModels/BlockSectionViewModel.cs`, directly after the `ShowEqIcon` property:

```csharp
    /// <summary>True for the synthetic `Level` block: show the volume glyph in the header. That
    /// block has no `on_off` field either, so the same header slot is free.</summary>
    [ObservableProperty] private bool _showLevelIcon;
```

- [ ] **Step 4: Build the Level block**

In `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`, add near `Blocks_InScope` (line 16):

```csharp
    /// <summary>Header for the synthetic block that fronts the pedal's per-preset output trim.</summary>
    public const string LevelBlockHeader = "Level";
```

In `LoadCoreAsync`, immediately after `_optionsVersion = catalogAtLoad;` and before `foreach (var block in Blocks_InScope)`:

```csharp
        // The pedal's per-preset output trim, promoted to its own block at the top of the editor.
        // Deliberately NOT a Blocks_InScope entry: `root\app\output` is the GLOBAL Master block
        // (its `vol` is the master volume, not a preset value), and the only other leaf under
        // `pst` is a per-preset BPM we don't surface. Addressed by exact path so nothing else
        // under `output` can leak in, which also means no hidden-params.json entry is needed.
        var levelRec = records.FirstOrDefault(r => r.Path == Sonulab.Distill.LevelModel.PresetLevelPath);
        if (levelRec is not null)
        {
            var levelSchema = NodeSchema.FromRecord(levelRec);
            var levelSection = new BlockSectionViewModel(LevelBlockHeader) { ShowLevelIcon = true };
            var levelValue = levelRec.Json.TryGetProperty("value", out var lv) ? lv.GetRawText() : "0";
            var levelField = new ParameterFieldViewModel(levelSchema, levelValue)
            {
                Label = _labels.Label(levelSchema.Path, levelSchema.Desc.Length > 0 ? levelSchema.Desc : null),
                ShowReset = true,
            };
            levelField.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ParameterFieldViewModel.Number)
                                   or nameof(ParameterFieldViewModel.Text)) IsDirty = true;
            };
            levelSection.Fields.Add(levelField);
            // Expanded by default — unlike every other block. This is the headline control and
            // was invisible before; a collapsed default would leave it just as hard to find.
            // The per-session memory still wins once the user has collapsed it.
            levelSection.IsExpanded = !_expansion.TryGetValue(levelKey, out var lexp) || lexp;
            levelSection.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(BlockSectionViewModel.IsExpanded) && s is BlockSectionViewModel b)
                    _expansion[levelKey] = b.IsExpanded;
            };
            Blocks.Add(levelSection);
        }
```

Add the key constant beside `LevelBlockHeader`:

```csharp
    private const string levelKey = @"root\app\output\pst";
```

Because `Blocks` is cleared at the top of `LoadCoreAsync` and this runs before the `Blocks_InScope`
loop, `Blocks.Add` here yields `Blocks[0]`.

- [ ] **Step 5: Add the label**

In `src/Namager.App/labels.en.json`, add:

```json
  "root\\app\\output\\pst\\level": "Preset Level"
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter ParameterEditorViewModelTests`
Expected: PASS, including the pre-existing `Output_block_is_skipped` and `Load_groups_into_blocks_in_order`.

`Load_groups_into_blocks_in_order` asserts the block headers are exactly `{ amp, delay }`. It will now fail because `Level` is first. Update that assertion to `{ level, amp, delay }` — the change in ordering is the intended behaviour, not a regression.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namager.App/ViewModels/BlockSectionViewModel.cs src/Namager.App/ViewModels/ParameterEditorViewModel.cs src/Namager.App/labels.en.json tests/Namager.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(app): surface Preset Level as the top block of the parameter editor"
```

---

### Task 4: Level block view — icon, explanation

**Files:**
- Modify: `src/Namager.App/Icons.axaml`
- Modify: `src/Namager.App/Views/ParameterEditorView.axaml` (header at line 38-42; block body at line 46)

**Interfaces:**
- Consumes: `BlockSectionViewModel.ShowLevelIcon` (Task 3).
- Produces: `Icon.VolumeHigh`, `Icon.VolumeEqual` resources for Task 5's button.

- [ ] **Step 1: Add the icon geometries**

In `src/Namager.App/Icons.axaml`, add two entries alongside the existing ones. `Icon.Amp` and
`Icon.Ir` in this file are already Material Design Icons paths, so the 24×24 viewbox matches.

```xml
  <StreamGeometry x:Key="Icon.VolumeHigh">M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.84 14,18.7V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.76 16.5,12M3,9V15H7L12,20V4L7,9H3Z</StreamGeometry>
  <!-- Composed here rather than lifted from MDI: the speaker cone from volume-high plus an
       equals sign, on the same 24x24 grid. Reads as "make these two the same volume". -->
  <StreamGeometry x:Key="Icon.VolumeEqual">M3,9V15H7L12,20V4L7,9H3M14,10H21V12H14V10M14,13H21V15H14V13Z</StreamGeometry>
```

**Verify both by eye at 14×14 before committing** (Step 4 below). `Icon.VolumeHigh` is the standard
MDI `volume-high` path; if it renders wrong, replace it from the MDI set rather than editing the
path data by hand.

- [ ] **Step 2: Add the header icon**

In `src/Namager.App/Views/ParameterEditorView.axaml`, directly after the `Icon.Equalizer` PathIcon
(line 38-42), inside the same `Expander.Header` StackPanel:

```xml
                    <PathIcon Data="{StaticResource Icon.VolumeHigh}" Width="14" Height="14"
                              VerticalAlignment="Center"
                              IsVisible="{Binding ShowLevelIcon}"
                              Foreground="{Binding IsEqActive, Converter={x:Static conv:ActiveToBrush.Instance}}"
                              ToolTip.Tip="This preset is trimmed away from 0 dB"/>
```

`IsEqActive` is already the generic "any float in this block is away from its default" test
(`BlockSectionViewModel.cs:38`) — reusing it makes a trimmed preset visible without expanding the
block, exactly like a non-flat EQ.

- [ ] **Step 3: Add the explanation line**

In the same file, inside the block body `<StackPanel>` at line 46, directly after the closing tag of
the `Fields` `ItemsControl` (line 99), add:

```xml
                  <!-- Only the Level block explains itself: it is the one control whose PURPOSE
                       is not obvious from its label, and it was invisible before this change. -->
                  <StackPanel Orientation="Horizontal" Spacing="8" Margin="4,2,4,4"
                              IsVisible="{Binding ShowLevelIcon}">
                    <TextBlock Classes="section-label" TextWrapping="Wrap" MaxWidth="300"
                               Text="Trims this preset's output after every effect — use it to match loudness between presets. It doesn't change the tone."/>
                  </StackPanel>
```

- [ ] **Step 4: Build and look at it**

Run: `dotnet build`
Then: `dotnet run --project src/Namager.App` (VoidX-Control must be CLOSED), connect, select a preset.

Confirm by eye: `Level` is the top section, expanded, the volume glyph renders cleanly at 14×14
(not clipped or off-centre — a wrong viewbox shows up here), the slider spans −20…+20, and the
explanation wraps without pushing the layout.

- [ ] **Step 5: Run the suite**

Run: `dotnet test`
Expected: PASS. `LayoutContractTests` covers view/VM binding contracts — if it fails, the binding
name is wrong, not the test.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/Icons.axaml src/Namager.App/Views/ParameterEditorView.axaml
git commit -m "feat(app): Level block header icon and explanatory copy"
```

---

### Task 5: Match this preset's volume to another

**Files:**
- Create: `src/Namager.App/Views/MatchPresetDialog.axaml` + `.axaml.cs`
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs` (line 279-282, editor construction)
- Modify: `src/Namager.App/Views/ParameterEditorView.axaml`
- Test: `tests/Namager.App.Tests/MatchVolumeTests.cs`

**Interfaces:**
- Consumes: `LevelModel.Estimate`/`InputPaths`/`AmpVolFlag`/`AmpNamePath`/`IrNamePath`/`Ir2NamePath`/`PresetLevelPath` (Task 2), the Level block and `LevelField` (Task 3), `Icon.VolumeEqual` (Task 4).
- Produces: `ParameterEditorViewModel.MatchVolumeAsync(Func<Task<int?>> pickTarget) -> Task`, exposed as `MatchVolumeCommand`.

The view model must not reference `Window`. Follow the established seam: `PresetUploadFlow.cs:34`
passes `() => SlotPickerDialog.ShowAsync(owner, vm.Items)` into the view model as a callback. Amp
and IR blob reads arrive the same way `readAmpMetadata` already does (`MainWindowViewModel.cs:281`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.App.Tests/MatchVolumeTests.cs`:

```csharp
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class MatchVolumeTests
{
    // Two presets whose only difference is eq\level: the loaded one is flat, slot 1 is +6 dB.
    // Matching the loaded preset TO slot 1 must therefore propose +6 dB.
    static FakeSonuLink Dev()
    {
        var d = new FakeSonuLink();
        d.SeedList(@"root\presets", Names("Loaded", "Louder"));
        d.SeedList(@"root\amp", Names("TestAmp"));
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\amp:{\"desc\":\"Amp\",\"value\":\"TestAmp\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\amp\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0}",
            "root\\app\\eq\\level:{\"desc\":\"Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\output\\pst\\level:{\"desc\":\"Preset Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0,\"unit\":\"dB\",\"dec\":1}");
        return d;
    }

    static string[] Names(params string[] used)
    {
        var n = new string[30];
        for (int i = 0; i < n.Length; i++) n[i] = i < used.Length ? used[i] : "";
        return n;
    }

    // The target preset, in the on-disk .pst form DeviceRepository.ReadPresetAsync returns.
    static Sonulab.Core.Model.PresetDocument TargetPst(
        double eqLevel, double pstLevel = 0.0, double ampVol = 50.0)
    {
        var text = string.Join("\r\n", new[]
        {
            "root\\app\\amp\\on_off:{\"value\":\"ON\"}",
            "root\\app\\amp\\amp:{\"value\":\"TestAmp\"}",
            "root\\app\\amp\\gain:{\"value\":0.000000}",
            $"root\\app\\amp\\vol:{{\"value\":{ampVol:F6}}}",
            $"root\\app\\eq\\level:{{\"value\":{eqLevel:F6}}}",
            $"root\\app\\output\\pst\\level:{{\"value\":{pstLevel:F6}}}",
        });
        var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return Sonulab.Core.Model.PresetDocument.Parse(blob);
    }

    static byte[] FlatAmpSlot()
    {
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = 1f;
        return Sonulab.Distill.VxampCodec.Encode(new Sonulab.Distill.WhTensors(
            pre, Sonulab.Distill.VxampFormat.G2HeaderFloats(), g2,
            Sonulab.Distill.VxampFormat.NlmixHeaderFloats(), 0f));
    }

    static ParameterEditorViewModel Vm(FakeSonuLink d, FakeStatusService status,
                                       Sonulab.Core.Model.PresetDocument targetPst,
                                       Action? onAmpRead = null) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            ParameterExposure.Default,
            status: status,
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => { onAmpRead?.Invoke(); return Task.FromResult(FlatAmpSlot()); },
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => Task.FromResult(targetPst));

    [Fact]
    public async Task Matching_a_louder_preset_proposes_that_many_db()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(6.0, vm.Blocks[0].Fields[0].Number, 3);
        Assert.True(vm.IsDirty);                       // proposed, NOT written
        Assert.DoesNotContain(d.CommandLog, c => c.StartsWith(@"write root\app\output\pst\level:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_targets_own_trim_is_carried_into_the_proposal()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0, pstLevel: -2.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(4.0, vm.Blocks[0].Fields[0].Number, 3);   // 6 dB louder, trimmed 2 dB down
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(null));

        Assert.Equal(0.0, vm.Blocks[0].Fields[0].Number);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_proposal_beyond_the_range_saturates_and_says_so()
    {
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 19.0, pstLevel: 19.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(20.0, vm.Blocks[0].Fields[0].Number, 3);
        Assert.Contains(status.Succeeded, m => m.Contains("as far as it goes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failing_target_read_reports_and_leaves_the_slider_alone()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: new FakeStatusService(),
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => throw new InvalidOperationException("boom"));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));   // must not throw

        Assert.Equal(0.0, vm.Blocks[0].Fields[0].Number);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task The_amp_volume_flag_is_suppressed_when_both_presets_share_it()
    {
        // Both sides sit at vol = 75 %, so the assumed taper cancels out of the difference.
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 6.0, ampVol: 75.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path == @"root\app\amp\vol").Number = 75.0;

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.DoesNotContain(status.Succeeded, m => m.Contains("Amp Volume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_amp_blob_is_read_once_per_session_not_once_per_estimate()
    {
        var d = Dev(); await d.OpenAsync();
        int reads = 0;
        var vm = Vm(d, new FakeStatusService(), TargetPst(6.0), onAmpRead: () => reads++);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));
        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        // Four estimates (two matches x two presets), all naming "TestAmp": one 96-chunk read.
        Assert.Equal(1, reads);
    }
}
```

`FakeStatusService` already exists at `tests/Namager.App.Tests/FakeStatusService.cs` and records
`Begun` / `Succeeded` / `Failed` / `IdleSummaries`. Nothing to add — the assertions above read
`Succeeded`, which is where `_status.Success(...)` lands.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter MatchVolumeTests`
Expected: FAIL — the `readAmpBlob` / `readIrBlob` / `readPresetDoc` constructor parameters and
`MatchVolumeAsync` do not exist.

- [ ] **Step 3: Extend the view model constructor**

In `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`, add these optional parameters after
`readAmpMetadata` (all default to null so every existing call site and test keeps compiling):

```csharp
        Func<int, CancellationToken, Task<byte[]>>? readAmpBlob = null,
        Func<int, CancellationToken, Task<byte[]?>>? readIrBlob = null,
        Func<int, CancellationToken, Task<Sonulab.Core.Model.PresetDocument>>? readPresetDoc = null,
```

Store them in readonly fields. `MatchVolumeCommand` is disabled unless both readers are present:

```csharp
    /// <summary>Volume matching needs to read another preset and its amp model, so it is only
    /// offered when the app supplied those readers (the real app always does; unit tests that
    /// only exercise the editor do not).</summary>
    public bool CanMatchVolume =>
        _readAmpBlob is not null && _readPresetDoc is not null && LevelField is not null && !IsLoading;
```

`CanMatchVolume` depends on `IsLoading` and on `LevelField`, so re-notify it from
`OnIsLoadingChanged` (line 97) and at the end of `LoadCoreAsync`, the same way `CanDownload`
already is.

Expose the Level field so the command and the view can find it without index arithmetic:

```csharp
    /// <summary>The Preset Level field, or null when the firmware has no such node.</summary>
    public ParameterFieldViewModel? LevelField { get; private set; }
```

Set `LevelField = levelField;` in the Task 3 block-building code, and `LevelField = null;` right
after `Blocks.Clear()` so a reload never leaves a stale reference.

- [ ] **Step 4: Write `MatchVolumeAsync`**

```csharp
    /// <summary>Propose a Preset Level that makes THIS preset as loud as another one.
    ///
    /// Sets the slider and leaves it dirty rather than writing: the user reviews the number and
    /// presses Save, exactly like every other parameter in this panel. The estimate is offline
    /// (see Sonulab.Distill.LevelModel) — its amp-model term is exact, and everything it cannot
    /// derive is reported to the user rather than silently folded in.</summary>
    [RelayCommand]
    public async Task MatchVolumeAsync(Func<Task<int?>> pickTarget)
    {
        if (LevelField is null || _readAmpBlob is null || _readPresetDoc is null) return;

        int? target = await pickTarget();
        if (target is not { } targetIndex) return;

        ErrorMessage = null;
        using var op = _status.BeginOperation("Matching volume…");
        try
        {
            var mine = await EstimateLoadedAsync();
            var theirs = await EstimateSlotAsync(targetIndex);

            double proposed = theirs.RelativeLufs + theirs.CurrentTrimDb - mine.RelativeLufs;
            double clamped = Math.Clamp(proposed, LevelField.Min, LevelField.Max);
            LevelField.Number = clamped;

            var notes = new List<string>();
            if (Math.Abs(clamped - proposed) > 1e-6)
                notes.Add($"that's as far as it goes ({proposed:F1} dB needed)");
            // The assumed amp-Volume taper cancels out of the difference when both presets share
            // a `vol`, so it is only worth mentioning when they differ.
            foreach (var f in mine.Unmodeled.Concat(theirs.Unmodeled).Distinct(StringComparer.Ordinal))
                if (f != Sonulab.Distill.LevelModel.AmpVolFlag
                    || mine.Unmodeled.Contains(f) != theirs.Unmodeled.Contains(f))
                    notes.Add(f);

            _status.Success(notes.Count == 0
                ? $"Preset Level set to {clamped:F1} dB — Save to apply"
                : $"Preset Level set to {clamped:F1} dB — Save to apply. Check by ear: {string.Join("; ", notes)}");
        }
        catch (Exception ex)
        {
            // [RelayCommand] async: an escape here is an unhandled UI-thread rethrow.
            Log.Warn(ex, "volume match against slot {0} failed", targetIndex);
            ErrorMessage = $"Match failed: {ex.Message}";
            _status.Failure($"Match failed: {ex.Message}");
        }
    }
```

The two estimate helpers:

```csharp
    /// <summary>Estimate the preset currently in the editor, using the live field values rather
    /// than re-reading the slot — the editor already holds them, including unsaved edits, which
    /// is what the user is actually listening to.</summary>
    private async Task<Sonulab.Distill.PresetLevelEstimate> EstimateLoadedAsync()
    {
        var byPath = AllFields().ToDictionary(f => f.Path, f => f.ToJsonValue(), StringComparer.Ordinal);
        return await EstimateAsync(byPath);
    }

    private async Task<Sonulab.Distill.PresetLevelEstimate> EstimateSlotAsync(int index)
    {
        var doc = await _readPresetDoc!(index, CancellationToken.None);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Sonulab.Distill.LevelModel.InputPaths)
            if (doc.GetValueJson(p) is { } v) byPath[p] = v;
        return await EstimateAsync(byPath);
    }

    /// <summary>Resolve the preset's named amp and IRs to slot blobs, then run the model.</summary>
    private async Task<Sonulab.Distill.PresetLevelEstimate> EstimateAsync(
        IReadOnlyDictionary<string, string> byPath)
    {
        byte[] amp = await BlobForAsync(@"root\amp", byPath, Sonulab.Distill.LevelModel.AmpNamePath,
                                        _readAmpBlob!) ?? Array.Empty<byte>();
        byte[]? ir1 = await BlobForAsync(@"root\ir", byPath, Sonulab.Distill.LevelModel.IrNamePath,
                                         async (i, ct) => await _readIrBlob!(i, ct) ?? Array.Empty<byte>());
        byte[]? ir2 = await BlobForAsync(@"root\ir", byPath, Sonulab.Distill.LevelModel.Ir2NamePath,
                                         async (i, ct) => await _readIrBlob!(i, ct) ?? Array.Empty<byte>());

        var defaults = AllFields()
            .Where(f => f.Default is not null)
            .ToDictionary(f => f.Path, f => f.Default!.Value, StringComparer.Ordinal);

        return Sonulab.Distill.LevelModel.Estimate(byPath, amp, ir1, ir2, defaults);
    }

    /// <summary>A preset names its amp/IR by NAME, so resolve the name against the device list to
    /// get a slot, then read that slot's blob. Returns null when the preset names nothing, the
    /// name is not on the device (an orphaned reference), or no reader was supplied — the model
    /// flags those cases rather than failing.
    ///
    /// Blobs are memoized per view-model instance: a 96-chunk amp read is ~3 s, and both sides of
    /// a comparison usually name the same amp. NOT persisted across sessions — the model needs the
    /// blob itself, so a cached scalar could not short-circuit it (see the spec correction above).
    /// </summary>
    private readonly Dictionary<string, byte[]> _blobCache = new(StringComparer.Ordinal);

    private async Task<byte[]?> BlobForAsync(string listPath,
        IReadOnlyDictionary<string, string> byPath, string namePath,
        Func<int, CancellationToken, Task<byte[]>>? read)
    {
        if (read is null) return null;
        string name = Unquote(byPath.GetValueOrDefault(namePath, ""));
        if (name.Length == 0) return null;

        string key = listPath + "|" + name;
        if (_blobCache.TryGetValue(key, out var cached)) return cached;

        var names = await _client.ReadListAsync(listPath);
        int slot = -1;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal)) { slot = i; break; }
        if (slot < 0) return null;

        var blob = await read(slot, CancellationToken.None);
        _blobCache[key] = blob;
        return blob;
    }

    private static string Unquote(string json) => json.Trim().Trim('"');
```

Clear `_blobCache` wherever the device catalog moves — the same place `RefreshRefOptionsAsync`
already reacts to `_catalog.Version` — so an amp re-uploaded under the same name is re-read.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter MatchVolumeTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Create the dialog**

Create `src/Namager.App/Views/MatchPresetDialog.axaml`, modelled on `SlotPickerDialog.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="Namager.App.Views.MatchPresetDialog"
        Title="Match volume"
        Width="420" SizeToContent="Height"
        WindowStartupLocation="CenterOwner"
        CanResize="False" ShowInTaskbar="False"
        Background="{DynamicResource Sonulab.SurfaceBrush}">
  <StackPanel Margin="20" Spacing="16">
    <TextBlock TextWrapping="Wrap"
               Text="Which preset should this one match? Its Preset Level will be set so the two sound equally loud — nothing is written until you press Save."/>
    <ComboBox x:Name="PresetCombo" HorizontalAlignment="Stretch"/>
    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <Button Content="Cancel" Click="OnCancelClick"/>
      <Button Classes="accent-outline" Content="Match" Click="OnConfirmClick"/>
    </StackPanel>
  </StackPanel>
</Window>
```

Create `src/Namager.App/Views/MatchPresetDialog.axaml.cs`, a direct sibling of
`SlotPickerDialog.axaml.cs` (read that file and mirror it) with one difference: `ShowAsync` takes
the slot index to EXCLUDE, so a preset cannot be matched to itself.

```csharp
    public static async Task<int?> ShowAsync(Window owner, IReadOnlyList<PresetItemViewModel> items, int excludeIndex)
    {
        var dlg = new MatchPresetDialog();
        foreach (var i in items.Where(i => !i.IsEmpty && i.Index != excludeIndex))
        {
            dlg._indices.Add(i.Index);
            dlg._labels.Add($"{i.DisplaySlot:00}  {i.Name}");
        }
        dlg.PresetCombo.SelectedIndex = dlg._indices.Count > 0 ? 0 : -1;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
```

- [ ] **Step 7: Add the button to the view**

In `src/Namager.App/Views/ParameterEditorView.axaml`, inside the Level-block explanation StackPanel
added in Task 4, after the `TextBlock`:

```xml
                    <Button Width="26" Height="26" Padding="0" VerticalAlignment="Center"
                            IsEnabled="{Binding $parent[views:ParameterEditorView].((vm:ParameterEditorViewModel)DataContext).CanMatchVolume}"
                            Click="OnMatchVolumeClick"
                            ToolTip.Tip="Match this preset's volume to another preset">
                      <PathIcon Data="{StaticResource Icon.VolumeEqual}" Width="14" Height="14"/>
                    </Button>
```

In `src/Namager.App/Views/ParameterEditorView.axaml.cs`, add the handler alongside the existing
`DownloadButton.Click` wiring — the view owns the `Window`, the view model owns the logic:

```csharp
    private async void OnMatchVolumeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.ParameterEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var presets = (owner.DataContext as ViewModels.MainWindowViewModel)?.Presets;
        if (presets is null) return;
        // async void event handler: nothing may escape to the UI thread. MatchVolumeAsync
        // already catches its own failures; this guards the picker itself.
        try { await vm.MatchVolumeAsync(() => MatchPresetDialog.ShowAsync(owner, presets.Items, vm.LoadedIndex)); }
        catch (Exception ex) { vm.ErrorMessage = $"Match failed: {ex.Message}"; }
    }
```

- [ ] **Step 8: Wire the readers in `MainWindowViewModel`**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, extend the editor construction at line 279:

```csharp
            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status,
                repo: _connection.Repository!, usage: usage, catalog: catalog,
                readAmpMetadata: (i, ct) => AmpListViewModel.ReadMetadataAsync(ampService, i, ct),
                navigator: this,
                readAmpBlob: (i, ct) => ampService.ReadAmpAsync(i, ct),
                readIrBlob: async (i, ct) => await irService.ReadIrAsync(i, ct),
                readPresetDoc: (i, ct) => _connection.Repository!.ReadPresetAsync(i, ct));
```

`irService` is currently constructed at line 296, *after* the editor. Move its construction above
the editor, next to `ampService` — the comment at line 275-277 already establishes that pattern for
`ampService`, so extend it to mention the IR reader too.

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 10: Try it on the pedal**

Run: `dotnet run --project src/Namager.App` (VoidX-Control CLOSED). Select a preset, press the
match button, pick a conspicuously louder preset. Confirm the proposed number has the right sign and
plausible magnitude, the unsaved-changes dot appears, and **nothing was written** until you press
Save.

- [ ] **Step 11: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterEditorViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs src/Namager.App/Views/MatchPresetDialog.axaml src/Namager.App/Views/MatchPresetDialog.axaml.cs src/Namager.App/Views/ParameterEditorView.axaml src/Namager.App/Views/ParameterEditorView.axaml.cs tests/Namager.App.Tests/MatchVolumeTests.cs
git commit -m "feat(app): match a preset's volume to another preset"
```

---

### Task 6: Documentation

**Files:**
- Create: `docs/HARDWARE-VALIDATION-preset-level.md`
- Modify: `docs/STATUS.md`
- Modify: `CLAUDE.md` (the "Protocol essentials" section)

- [ ] **Step 1: Write the hardware-validation checklist**

Create `docs/HARDWARE-VALIDATION-preset-level.md`, following the shape of
`docs/HARDWARE-VALIDATION-restore.md` (numbered checks, an explicit pass/fail line per row,
VoidX-Control closed throughout):

1. Connect, select a preset. **Expect:** `Level` is the top section, expanded, volume glyph
   rendered, slider spans −20…+20, value 0.0 dB.
2. Drag to −6 dB, press Save, select another preset, select the first again.
   **Expect:** slider reads −6.0 and the preset is audibly quieter.
3. `dotnet run --project tools/HwCheck` to dump the slot; diff against the pre-edit copy under
   `docs/backups/`. **Expect:** only the `root\app\output\pst\level` line differs.
4. Press the reset button. **Expect:** 0.0 dB, and the volume glyph in the header un-highlights.
5. Press match, choose a conspicuously louder preset. **Expect:** the proposal has the right sign
   and a plausible size; the status bar names any "check by ear" caveats; nothing is written.
6. Save, then A/B the two presets. **Expect:** the volume jump is gone or much reduced.
7. Repeat 5 with a preset that has the compressor or reverb on. **Expect:** the status bar flags it,
   and this is where the estimate is least accurate — record the by-ear error in dB.
8. Search the whole UI for "Preset TEMPO" / a BPM control. **Expect:** absent.
9. Press match a second time in the same session against the same target. **Expect:** noticeably
   faster — the amp blob is memoized per session, so the ~3 s 96-chunk read happens once. Record
   both durations.

- [ ] **Step 2: Update STATUS.md**

Add to `docs/STATUS.md`:

```markdown
- Preset Level SHIPPED: `root\app\output\pst\level` is now the top block of the parameter editor
  (slider + explanation), with "match volume to another preset" backed by an offline K-weighted
  estimate (`Sonulab.Distill.Loudness` / `LevelModel`). Hardware checks pending in
  `docs/HARDWARE-VALIDATION-preset-level.md`. Two deliberate gaps:
  - The spec's `AmpLoudnessCache` was **not built** — `LevelModel` needs the amp BLOB, not a
    scalar (the model is nonlinear), so a cached loudness cannot short-circuit the read. Amp blobs
    are memoized per session instead. If bulk normalize later wants persistence, the right shape
    is a preset-keyed estimate cache: `(deviceId, slot, presetName, hash of level-relevant values)`
    → `RelativeLufs`.
  - Bulk "normalize the whole bank" is deliberately NOT built — see the Deferred section of
    `docs/superpowers/specs/2026-08-03-preset-level-design.md`; when it is, the apply path should
    be byte-exact dwrite, not select+save.
  The `amp\vol` %→dB taper in `LevelModel.AmpVolGainDb` is an ASSUMPTION (50 % treated as unity)
  and is the first thing to calibrate against the device VU meters.
```

- [ ] **Step 3: Update CLAUDE.md**

In the "Protocol essentials" section, after the `dwrite` line, add:

```markdown
- **Per-preset output trim** = `root\app\output\pst\level` ("Preset Level", −20…+20 dB, def 0,
  post-everything, saved in the `.pst`). Surfaced as the editor's top `Level` block. `root\app\output`
  itself is the GLOBAL Master block and stays out of `Blocks_InScope`.
```

- [ ] **Step 4: Commit**

```bash
git add docs/HARDWARE-VALIDATION-preset-level.md docs/STATUS.md CLAUDE.md
git commit -m "docs: Preset Level hardware checklist, status, and protocol note"
```

---

## Done criteria

- `dotnet test` passes with roughly 526 tests (490 before, plus 7 Loudness + 17 LevelModel +
  5 editor + 7 match).
- `dotnet run --project src/Namager.App` shows `Level` as the top, expanded block with a working
  slider, explanation and match button.
- Matching against a louder preset proposes a sensible trim, leaves it unsaved, and Save applies it.
- `docs/HARDWARE-VALIDATION-preset-level.md` is filled in from a real pedal session.
- Merge to `main` fast-forward per the CLAUDE.md workflow.
