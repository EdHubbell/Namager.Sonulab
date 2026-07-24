using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageMapTransformTests
{
    static PresetUsageMap Map(params (int Slot, string Name, string Amp)[] p)
    {
        var docs = p.Select(x =>
        {
            var blob = new byte[PresetDocument.BlobSize];
            System.Text.Encoding.ASCII.GetBytes($@"root\app\amp\amp:{{""value"":""{x.Amp}""}}").CopyTo(blob, 0);
            return (x.Slot, x.Name, PresetDocument.Parse(blob));
        }).ToList();
        return PresetUsageMap.Build(docs);
    }
    static (int, string)[] Refs(PresetUsageMap m, string amp) =>
        m.PresetsUsingAmp(amp).Select(r => (r.Index, r.Name)).ToArray();

    [Fact] public void WithMovedSlot_up_rotates_ref_indices()
    {
        // slots 1,2,3 use amp "x"; move slot 3 -> 1 (up). Expect indices 1,2,3 -> the preset from 3
        // lands at 1, the others shift to 2,3; names ride along.
        var m = Map((1, "P1", "x"), (2, "P2", "x"), (3, "P3", "x")).WithMovedSlot(3, 1);
        Assert.Equal(new[] { (1, "P3"), (2, "P1"), (3, "P2") }, Refs(m, "x"));
    }

    [Fact] public void WithMovedSlot_down_rotates_ref_indices()
    {
        // slots 0,1,2 use amp "x"; move slot 0 -> 2 (down). The preset from 0 lands at 2, the others
        // shift up to 0,1 — the in-range "shift" branch for from<to. Mirrors the engine's
        // MoveAsync(0,2): A,B,C -> B,C,A.
        var m = Map((0, "P0", "x"), (1, "P1", "x"), (2, "P2", "x")).WithMovedSlot(0, 2);
        Assert.Equal(new[] { (0, "P1"), (1, "P2"), (2, "P0") }, Refs(m, "x"));
    }

    [Fact] public void WithMovedSlot_leaves_out_of_range_refs_untouched()
    {
        var m = Map((0, "P0", "x"), (5, "P5", "x")).WithMovedSlot(1, 3);   // range [1,3] excludes 0 and 5
        Assert.Equal(new[] { (0, "P0"), (5, "P5") }, Refs(m, "x"));
    }

    [Fact] public void WithRenamedPreset_updates_name_at_index_only()
    {
        var m = Map((2, "Old", "x"), (4, "Keep", "x")).WithRenamedPreset(2, "New");
        Assert.Equal(new[] { (2, "New"), (4, "Keep") }, Refs(m, "x"));
    }

    [Fact] public void WithoutSlot_drops_refs_at_index()
    {
        var m = Map((2, "Gone", "x"), (4, "Keep", "x")).WithoutSlot(2);
        Assert.Equal(new[] { (4, "Keep") }, Refs(m, "x"));
    }

    [Fact] public void WithoutSlot_dropping_last_ref_removes_key()
    {
        var m = Map((2, "Only", "x")).WithoutSlot(2);
        Assert.Empty(m.PresetsUsingAmp("x"));
    }
}
