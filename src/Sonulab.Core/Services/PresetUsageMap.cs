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

    /// <summary>Return a new map with the effect of moving a preset from slot <paramref name="from"/>
    /// to slot <paramref name="to"/>: every ref index inside the affected range is rotated (the moved
    /// preset takes <paramref name="to"/>; the others shift by one). Names ride along with content
    /// (dswap moves both), so only indices change. The map-side mirror of the reorder engine.</summary>
    public PresetUsageMap WithMovedSlot(int from, int to)
    {
        if (from == to) return this;
        int min = Math.Min(from, to), max = Math.Max(from, to), step = from < to ? -1 : 1;
        return Rebuild(r =>
        {
            if (r.Index < min || r.Index > max) return r;
            int ni = r.Index == from ? to : r.Index + step;
            return r with { Index = ni };
        });
    }

    /// <summary>Return a new map with the preset at <paramref name="index"/> renamed.</summary>
    public PresetUsageMap WithRenamedPreset(int index, string newName) =>
        Rebuild(r => r.Index == index ? r with { Name = newName } : r);

    /// <summary>Return a new map with all refs to the preset at <paramref name="index"/> removed.</summary>
    public PresetUsageMap WithoutSlot(int index) =>
        Rebuild(r => r.Index == index ? (PresetRef?)null : r);

    private PresetUsageMap Rebuild(Func<PresetRef, PresetRef?> f) =>
        new(Map(_amp, f), Map(_ir, f));

    private static IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> Map(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> src, Func<PresetRef, PresetRef?> f)
    {
        var result = new Dictionary<string, IReadOnlyList<PresetRef>>(src.Count);
        foreach (var (key, list) in src)
        {
            var next = list.Select(f).Where(r => r.HasValue).Select(r => r!.Value)
                           .OrderBy(r => r.Index).ToList();
            if (next.Count > 0) result[key] = next;   // drop keys that lost all refs
        }
        return result;
    }

    /// <summary>Return a new map reflecting the CURRENT contents of the preset at
    /// <paramref name="index"/>: every existing ref at that slot is dropped, then the refs parsed
    /// from <paramref name="doc"/> are added under <paramref name="name"/>. The targeted
    /// counterpart of a full rescan — used after an in-place edit (parameter-editor save) or a
    /// single-slot write (preset upload / restore).</summary>
    public PresetUsageMap WithUpdatedPreset(int index, string name, PresetDocument doc)
    {
        var amp = Thaw(_amp, index);
        var ir = Thaw(_ir, index);
        Collect(amp, ir, index, name, doc);
        return new PresetUsageMap(Freeze(amp), Freeze(ir));
    }

    /// <summary>Mutable copy of a frozen side of the map with every ref at
    /// <paramref name="dropIndex"/> removed. Keys that lose all their refs are dropped, matching
    /// <see cref="Map"/>'s behaviour.</summary>
    private static Dictionary<string, List<PresetRef>> Thaw(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> src, int dropIndex)
    {
        var result = new Dictionary<string, List<PresetRef>>(src.Count);
        foreach (var (key, list) in src)
        {
            var kept = list.Where(r => r.Index != dropIndex).ToList();
            if (kept.Count > 0) result[key] = kept;
        }
        return result;
    }

    public static PresetUsageMap Build(IEnumerable<(int SlotIndex, string PresetName, PresetDocument Doc)> occupiedPresets)
    {
        var amp = new Dictionary<string, List<PresetRef>>();
        var ir = new Dictionary<string, List<PresetRef>>();
        foreach (var (slotIndex, presetName, doc) in occupiedPresets)
            Collect(amp, ir, slotIndex, presetName, doc);
        return new PresetUsageMap(Freeze(amp), Freeze(ir));
    }

    /// <summary>Parse one preset document's amp/IR reference lines into the two accumulators.
    /// Shared by <see cref="Build"/> and <see cref="WithUpdatedPreset"/> so the two can never
    /// disagree about what counts as a reference.</summary>
    private static void Collect(Dictionary<string, List<PresetRef>> amp,
                                Dictionary<string, List<PresetRef>> ir,
                                int slotIndex, string presetName, PresetDocument doc)
    {
        var entry = new PresetRef(slotIndex, presetName);
        foreach (var line in doc.Lines)
        {
            if (!NodeRecord.TryParse(line, out var rec)) continue;
            // Real dread/.pst documents carry only {"value":…} lines — match by node PATH.
            // (The schema "ref" field exists only in `browse` responses.)
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
