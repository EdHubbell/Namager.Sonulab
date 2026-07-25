using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageMapUpdateTests
{
    static PresetDocument Doc(params string[] lines)
    {
        var blob = new byte[PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n", lines)).CopyTo(blob, 0);
        return PresetDocument.Parse(blob);
    }

    static string Amp(string n) => $@"root\app\amp\amp:{{""value"":""{n}""}}";
    static string Ir(string n) => $@"root\app\ir\ir:{{""value"":""{n}""}}";
    static string Ir2(string n) => $@"root\app\ir\ir2\ir:{{""value"":""{n}""}}";

    static PresetUsageMap Base() => PresetUsageMap.Build(new[]
    {
        (0, "One",   Doc(Amp("ampA"), Ir("irA"))),
        (1, "Two",   Doc(Amp("ampA"), Ir("irB"))),
        (2, "Three", Doc(Amp("ampB"), Ir("irB"))),
    });

    [Fact] public void Swapping_the_amp_moves_the_ref_and_leaves_others_alone()
    {
        var m = Base().WithUpdatedPreset(1, "Two", Doc(Amp("ampB"), Ir("irB")));
        Assert.Equal(new[] { 0 }, m.PresetsUsingAmp("ampA").Select(r => r.Index));
        Assert.Equal(new[] { 1, 2 }, m.PresetsUsingAmp("ampB").Select(r => r.Index));
    }

    [Fact] public void An_amp_that_loses_its_last_ref_disappears_from_the_map()
    {
        var m = Base().WithUpdatedPreset(2, "Three", Doc(Amp("ampA"), Ir("irB")));
        Assert.Empty(m.PresetsUsingAmp("ampB"));
    }

    [Fact] public void An_empty_value_clears_the_ref_without_adding_a_key()
    {
        var m = Base().WithUpdatedPreset(0, "One", Doc(Amp(""), Ir("irA")));
        Assert.Empty(m.PresetsUsingAmp("ampA").Where(r => r.Index == 0));
        Assert.Empty(m.PresetsUsingAmp(""));
    }

    [Fact] public void A_second_ir_reference_is_picked_up()
    {
        var m = Base().WithUpdatedPreset(0, "One", Doc(Amp("ampA"), Ir("irA"), Ir2("irB")));
        Assert.Contains(m.PresetsUsingIr("irB"), r => r.Index == 0);
        Assert.Contains(m.PresetsUsingIr("irA"), r => r.Index == 0);
    }

    [Fact] public void Refs_stay_sorted_by_slot_after_an_update()
    {
        var m = Base().WithUpdatedPreset(0, "One", Doc(Amp("ampB"), Ir("irA")));
        Assert.Equal(new[] { 0, 2 }, m.PresetsUsingAmp("ampB").Select(r => r.Index));
    }

    [Fact] public void A_renamed_preset_carries_its_new_name_into_the_refs()
    {
        var m = Base().WithUpdatedPreset(1, "Two Renamed", Doc(Amp("ampA"), Ir("irB")));
        Assert.Equal("Two Renamed", m.PresetsUsingAmp("ampA").Single(r => r.Index == 1).Name);
    }

    [Fact] public void Updating_a_slot_with_no_prior_refs_just_adds_them()
    {
        var m = Base().WithUpdatedPreset(7, "Seven", Doc(Amp("ampC")));
        Assert.Equal(new[] { 7 }, m.PresetsUsingAmp("ampC").Select(r => r.Index));
        Assert.Equal(new[] { 0, 1 }, m.PresetsUsingAmp("ampA").Select(r => r.Index));
    }
}
