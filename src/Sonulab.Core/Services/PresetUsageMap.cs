using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

/// <summary>One preset that references an amp/IR file. <see cref="Index"/> is the 0-based slot.</summary>
public readonly record struct PresetRef(int Index, string Name);

/// <summary>Which presets reference each amp / IR file, by NAME. Built once from the set of
/// occupied preset documents. Pure — no device I/O. A preset stores its amp/IR selection as a
/// node line whose schema <c>ref</c> is <c>root\amp</c> / <c>root\ir</c> and whose <c>value</c>
/// is the file name. Each result list is ordered by slot index ascending, distinct by slot.</summary>
public sealed class PresetUsageMap
{
    private const string AmpRef = @"root\amp";
    private const string IrRef = @"root\ir";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> _amp;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> _ir;

    private PresetUsageMap(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> amp,
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> ir)
    { _amp = amp; _ir = ir; }

    /// <summary>Nothing is used — the default before any scan.</summary>
    public static readonly PresetUsageMap Empty = new(
        new Dictionary<string, IReadOnlyList<PresetRef>>(),
        new Dictionary<string, IReadOnlyList<PresetRef>>());

    public IReadOnlyList<PresetRef> PresetsUsingAmp(string ampName) => Lookup(_amp, ampName);
    public IReadOnlyList<PresetRef> PresetsUsingIr(string irName) => Lookup(_ir, irName);

    private static IReadOnlyList<PresetRef> Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> map, string name) =>
        map.TryGetValue(name.Trim(), out var list) ? list : Array.Empty<PresetRef>();

    public static PresetUsageMap Build(IEnumerable<(int SlotIndex, string PresetName, PresetDocument Doc)> occupiedPresets)
    {
        var amp = new Dictionary<string, List<PresetRef>>();
        var ir = new Dictionary<string, List<PresetRef>>();

        foreach (var (slotIndex, presetName, doc) in occupiedPresets)
        {
            var entry = new PresetRef(slotIndex, presetName);
            foreach (var line in doc.Lines)
            {
                if (!NodeRecord.TryParse(line, out var rec)) continue;
                var reference = NodeSchema.FromRecord(rec).Ref;
                var target = reference switch { AmpRef => amp, IrRef => ir, _ => (Dictionary<string, List<PresetRef>>?)null };
                if (target is null) continue;

                var value = rec.ValueString?.Trim();
                if (string.IsNullOrEmpty(value)) continue;

                if (!target.TryGetValue(value, out var list)) target[value] = list = new List<PresetRef>();
                if (!list.Any(r => r.Index == slotIndex)) list.Add(entry);   // dedupe by slot
            }
        }

        return new PresetUsageMap(Freeze(amp), Freeze(ir));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> Freeze(Dictionary<string, List<PresetRef>> src)
    {
        var result = new Dictionary<string, IReadOnlyList<PresetRef>>(src.Count);
        foreach (var (k, v) in src)
        {
            v.Sort((a, b) => a.Index.CompareTo(b.Index));   // ascending by slot
            result[k] = v;
        }
        return result;
    }
}
