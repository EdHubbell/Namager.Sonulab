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

        // DriveSignal is 16000 samples (~363 ms) — under Loudness.IntegratedLufs's 400 ms
        // sliding-window block. That method measures a signal this short as a single block
        // spanning the whole thing rather than refusing it, so this reads a real number here
        // with no splicing needed on this side.
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
