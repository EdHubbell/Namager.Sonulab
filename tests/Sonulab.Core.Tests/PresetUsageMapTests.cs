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

    private const string AmpLine = @"root\app\amp\amp:{{""desc"":""Amp model"",""value"":""{0}"",""type"":""plist"",""ref"":""root\\amp""}}";
    private const string IrLine  = @"root\app\ir\ir:{{""desc"":""Cab IR"",""value"":""{0}"",""type"":""plist"",""ref"":""root\\ir""}}";

    private static string Amp(string name) => string.Format(AmpLine, name);
    private static string Ir(string name) => string.Format(IrLine, name);

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
    public void Captures_multiple_ir_nodes_in_one_preset()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (3, "Big", Doc(
                @"root\app\ir\ir:{""value"":""CabA"",""ref"":""root\\ir""}",
                @"root\app\reverb\ir:{""value"":""RoomB"",""ref"":""root\\ir""}")),
        });
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("CabA"));
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("RoomB"));
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
                @"root\app\amp\amp:{""value"":"""",""ref"":""root\\amp""}",   // empty value
                @"root\app\gain\gain:{""value"":""5"",""type"":""num""}")),    // no ref
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
}
