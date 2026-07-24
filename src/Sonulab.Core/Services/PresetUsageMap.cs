using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

/// <summary>One preset that references an amp/IR file. <see cref="Index"/> is the 0-based slot.</summary>
public readonly record struct PresetRef(int Index, string Name);

/// <summary>Which presets reference each amp / IR file, by NAME. Built once from the set of
/// occupied preset documents. Pure — no device I/O. A preset stores its amp/IR selection as a
/// plain node line — real dread/.pst documents carry only <c>{"value":…}</c>, never a schema
/// <c>ref</c> field (that only appears in <c>browse</c> responses) — so references are matched
/// by node PATH: <see cref="AmpNodePath"/> for the amp, <see cref="IsIrRefPath"/> for the IR(s).
/// Each result list is ordered by slot index ascending, distinct by slot.</summary>
public sealed class PresetUsageMap
{
    /// <summary>The amp reference node: its value is the amp file name.</summary>
    public const string AmpNodePath = @"root\app\amp\amp";

    /// <summary>An IR reference node = an `ir` leaf inside the `root\app\ir` block —
    /// `root\app\ir\ir` (primary) and `root\app\ir\ir2\ir` (secondary/dual), and any future
    /// `…\ir3\ir`. Excludes the block stubs (`root\app\ir`, `root\app\ir\ir2`) by requiring
    /// the `root\app\ir\` prefix AND a `\ir` leaf.</summary>
    public static bool IsIrRefPath(string path) =>
        path.StartsWith(@"root\app\ir\", StringComparison.Ordinal) &&
        path.EndsWith(@"\ir", StringComparison.Ordinal);

    /// <summary>True when <paramref name="documentText"/> already contains COMPLETE lines for
    /// all three reference nodes (amp, primary IR, secondary IR) — the windowed head read stops
    /// here. "Complete" = the path prefix is present and its JSON object is closed.</summary>
    public static bool HeadComplete(string documentText)
    {
        return LineComplete(documentText, AmpNodePath)
            && LineComplete(documentText, @"root\app\ir\ir")
            && LineComplete(documentText, @"root\app\ir\ir2\ir");

        static bool LineComplete(string text, string path)
        {
            int i = text.IndexOf(path + ":{", StringComparison.Ordinal);
            return i >= 0 && text.IndexOf('}', i) >= 0;
        }
    }

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
                // Real dread/.pst documents carry only {"value":…} lines — match by node PATH.
                // (The schema "ref" field exists only in `browse` responses; keying off it here
                // is the bug that made every on-device map come back empty.)
                var target = rec.Path == AmpNodePath ? amp
                           : IsIrRefPath(rec.Path) ? ir
                           : null;
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
