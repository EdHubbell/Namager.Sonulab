using System.IO;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageMapTests
{
    // Build a PresetDocument from raw node lines (as the device returns them).
    private static PresetDocument Doc(params string[] lines)
    {
        var text = string.Join("\r\n", lines);
        var blob = new byte[PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return PresetDocument.Parse(blob);
    }

    // REAL device lines: dread/.pst documents carry ONLY {"value":…} — no "ref" field.
    // (The old fixtures injected a synthetic "ref" the firmware never sends; that let the
    // schema-ref matching bug pass 619 tests while highlighting nothing on hardware.)
    private static string Amp(string name) => $@"root\app\amp\amp:{{""value"":""{name}""}}";
    private static string Ir(string name)  => $@"root\app\ir\ir:{{""value"":""{name}""}}";
    private static string Ir2(string name) => $@"root\app\ir\ir2\ir:{{""value"":""{name}""}}";

    [Fact]
    public void Maps_amp_and_ir_names_to_the_presets_that_use_them()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "Lead",   Doc(Amp("Plexi"), Ir("V30"))),
            (6, "Rhythm", Doc(Amp("Plexi"), Ir("Greenback"))),
        });

        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(6, "Rhythm") }, map.PresetsUsingAmp("Plexi"));
        Assert.Equal(new[] { new PresetRef(0, "Lead") }, map.PresetsUsingIr("V30"));
        Assert.Equal(new[] { new PresetRef(6, "Rhythm") }, map.PresetsUsingIr("Greenback"));
        Assert.Empty(map.PresetsUsingAmp("Nonexistent"));
    }

    [Fact]
    public void Orders_presets_by_slot_ascending_regardless_of_input_order()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (11, "Solo",  Doc(Amp("Plexi"))),
            (2,  "Clean", Doc(Amp("Plexi"))),
            (6,  "Lead",  Doc(Amp("Plexi"))),
        });
        Assert.Equal(
            new[] { new PresetRef(2, "Clean"), new PresetRef(6, "Lead"), new PresetRef(11, "Solo") },
            map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public void Captures_primary_and_secondary_ir_refs_in_one_preset()
    {
        var map = PresetUsageMap.Build(new[] { (3, "Big", Doc(Ir("CabA"), Ir2("RoomB"))) });
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("CabA"));
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("RoomB"));
    }

    [Fact]
    public void Stub_lines_and_foreign_paths_are_not_references()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "P", Doc(
                @"root\app\amp:{""value"":""NotARef""}",        // amp block stub
                @"root\app\ir:{""value"":""NotARef""}",         // ir block stub
                @"root\app\ir\ir2:{""value"":""NotARef""}",     // ir2 stub (no trailing \ir)
                @"root\app\reverb\ir:{""value"":""NotARef""}")),// outside the ir block
        });
        Assert.Empty(map.PresetsUsingAmp("NotARef"));
        Assert.Empty(map.PresetsUsingIr("NotARef"));
    }

    [Fact]
    public void Dedupes_a_preset_that_references_the_same_amp_twice()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (4, "Dup", Doc(Amp("Plexi"), Amp("Plexi"))),
        });
        Assert.Equal(new[] { new PresetRef(4, "Dup") }, map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public void Skips_empty_values_and_non_ref_nodes()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "P", Doc(
                @"root\app\amp\amp:{""value"":""""}",           // empty value
                @"root\app\gate\threshold:{""value"":-60.0}")),  // non-ref path
        });
        Assert.Empty(map.PresetsUsingAmp(""));
    }

    [Fact]
    public void Name_match_is_exact_but_trims_whitespace()
    {
        var map = PresetUsageMap.Build(new[] { (0, "P", Doc(Amp("Plexi"))) });
        Assert.Equal(new[] { new PresetRef(0, "P") }, map.PresetsUsingAmp("Plexi "));   // trimmed
        Assert.Empty(map.PresetsUsingAmp("plexi"));                                     // case-sensitive
    }

    [Fact]
    public void Empty_map_reports_nothing_used()
    {
        Assert.Empty(PresetUsageMap.Empty.PresetsUsingAmp("Plexi"));
        Assert.Empty(PresetUsageMap.Empty.PresetsUsingIr("V30"));
    }

    [Fact]
    public void Builds_from_a_real_captured_preset_document()
    {
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        var map = PresetUsageMap.Build(new[] { (0, "Quad Reverb SM57", PresetDocument.Parse(blob)) });
        Assert.Equal(new[] { new PresetRef(0, "Quad Reverb SM57") },
                     map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Equal(new[] { new PresetRef(0, "Quad Reverb SM57") },
                     map.PresetsUsingIr("TWIN REVERB __ CLEAN"));
    }

    [Fact]
    public void HeadComplete_requires_all_three_reference_lines()
    {
        var text = File.ReadAllText(Path.Combine("Fixtures", "QuadReverbSM57.pst"))
                       .TrimEnd('\0');
        Assert.True(PresetUsageMap.HeadComplete(text));
        // Truncated before the ir2\ir line (byte ~2859): incomplete.
        Assert.False(PresetUsageMap.HeadComplete(text[..2000]));
        // Truncated mid-line (ir2\ir line present but its record still open): incomplete.
        int ir2 = text.IndexOf(@"root\app\ir\ir2\ir:{", StringComparison.Ordinal);
        Assert.False(PresetUsageMap.HeadComplete(text[..(ir2 + 10)]));
        Assert.False(PresetUsageMap.HeadComplete(""));
    }

    [Fact]
    public void SlotUsages_roundtrip_preserves_lookups()
    {
        var doc0 = Doc(Amp("Plexi"), Ir("Cab A"), Ir2("Cab B"));
        var doc1 = Doc(Amp("Plexi"), Ir("Cab A"));
        var map = PresetUsageMap.Build(new[] { (0, "Lead", doc0), (3, "Rhythm", doc1) });

        var rows = map.ToSlotUsages();
        var back = PresetUsageMap.FromSlotUsages(rows);

        Assert.Equal(map.PresetsUsingAmp("Plexi"), back.PresetsUsingAmp("Plexi"));
        Assert.Equal(map.PresetsUsingIr("Cab A"), back.PresetsUsingIr("Cab A"));
        Assert.Equal(map.PresetsUsingIr("Cab B"), back.PresetsUsingIr("Cab B"));
    }

    [Fact]
    public void ToSlotUsages_is_ordered_and_deterministic()
    {
        var doc = Doc(Amp("Plexi"), Ir("Zeta"), Ir2("Alpha"));
        var rows = PresetUsageMap.Build(new[] { (5, "Solo", doc), (2, "Clean", doc) }).ToSlotUsages();

        Assert.Equal(new[] { 2, 5 }, rows.Select(r => r.Index));
        Assert.Equal(new[] { "Alpha", "Zeta" }, rows[0].Irs);   // ordinal-sorted
        Assert.Equal("Plexi", rows[0].Amp);
        Assert.Equal("Clean", rows[0].PresetName);
    }

    [Fact]
    public void ExtractSlotUsage_matches_Build_and_handles_refless_docs()
    {
        var doc = Doc(Amp("Plexi"), Ir("Cab A"));
        var one = PresetUsageMap.ExtractSlotUsage(4, "Lead", doc);
        Assert.Equal(4, one.Index);
        Assert.Equal("Lead", one.PresetName);
        Assert.Equal("Plexi", one.Amp);
        Assert.Equal(new[] { "Cab A" }, one.Irs);

        var refless = PresetUsageMap.ExtractSlotUsage(7, "Empty-ish",
            Doc(@"root\app\eq\low:{""value"":0}"));
        Assert.Equal(7, refless.Index);
        Assert.Null(refless.Amp);
        Assert.Empty(refless.Irs);
    }

    [Fact]
    public void FromSlotUsages_dedupes_by_slot_and_drops_blank_names()
    {
        var back = PresetUsageMap.FromSlotUsages(new[]
        {
            new SlotUsage(1, "A", "Plexi", new[] { "Cab", "", " " }),
            new SlotUsage(1, "A-dupe", "Other", Array.Empty<string>()),
            new SlotUsage(2, "B", null, new[] { "Cab" }),
        });
        Assert.Equal(new[] { new PresetRef(1, "A") }, back.PresetsUsingAmp("Plexi"));
        Assert.Empty(back.PresetsUsingAmp("Other"));
        Assert.Equal(new[] { new PresetRef(1, "A"), new PresetRef(2, "B") }, back.PresetsUsingIr("Cab"));
        // Verify blank and whitespace-only IR names are dropped
        Assert.Empty(back.PresetsUsingIr(""));
        Assert.Empty(back.PresetsUsingIr(" "));
    }
}
