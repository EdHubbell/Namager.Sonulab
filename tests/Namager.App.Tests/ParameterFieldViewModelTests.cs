using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Xunit;

public class ParameterFieldViewModelTests
{
    static NodeSchema Schema(string json, string path = @"root\app\amp\gain")
    {
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return NodeSchema.FromRecord(r);
    }

    [Fact] public void Float_field_exposes_range_and_round_trips_json()
    {
        var s = Schema(@"{""desc"":""Gain"",""value"":0.0,""type"":""float"",""min"":-20.0,""max"":20.0,""def"":0.0,""unit"":""dB""}");
        var f = new ParameterFieldViewModel(s, "3.5");
        Assert.Equal("float", f.Kind);
        Assert.Equal(-20.0, f.Min);
        Assert.Equal(20.0, f.Max);
        Assert.Equal(3.5, f.Number);
        f.Number = -6.0;
        Assert.Equal("-6", f.ToJsonValue());
    }

    [Fact] public void Enum_field_exposes_options_and_quotes_value()
    {
        var s = Schema(@"{""desc"":""Mode"",""value"":""ROOM"",""type"":""enum"",""options"":[""ROOM"",""HALL""]}", @"root\app\reverb\mode");
        var f = new ParameterFieldViewModel(s, "\"ROOM\"");
        Assert.Equal("enum", f.Kind);
        Assert.Equal(new[] { "ROOM", "HALL" }, f.Options);
        f.Text = "HALL";
        Assert.Equal("\"HALL\"", f.ToJsonValue());
    }

    // ---- ref-populated options (editor-polish Task 1) ----

    [Fact]
    public void RefOptions_fill_empty_plist_options()
    {
        var s = Schema(@"{""desc"":""Amp model"",""value"":""Lead"",""type"":""plist"",""ref"":""root\\amp""}", @"root\app\amp\amp");
        var f = new ParameterFieldViewModel(s, "\"Lead\"", new[] { "Clean", "Lead", "Rhythm" });
        Assert.Equal("plist", f.Kind);
        Assert.Equal(new[] { "Clean", "Lead", "Rhythm" }, f.Options);
        Assert.Equal("Lead", f.Text);
    }

    [Fact]
    public void Current_value_missing_from_ref_list_is_prepended()
    {
        var s = Schema(@"{""desc"":""Amp model"",""value"":""Deleted Amp"",""type"":""plist"",""ref"":""root\\amp""}", @"root\app\amp\amp");
        var f = new ParameterFieldViewModel(s, "\"Deleted Amp\"", new[] { "Clean", "Lead" });
        Assert.Equal(new[] { "Deleted Amp", "Clean", "Lead" }, f.Options);
    }

    [Fact]
    public void Item_kind_with_ref_options_becomes_plist()
    {
        var s = Schema(@"{""desc"":""IR file"",""value"":""Cab1"",""type"":""item"",""ref"":""root\\ir""}", @"root\app\ir\ir");
        var f = new ParameterFieldViewModel(s, "\"Cab1\"", new[] { "Cab1", "Cab2" });
        Assert.Equal("plist", f.Kind);
        Assert.Equal(new[] { "Cab1", "Cab2" }, f.Options);
    }

    [Fact]
    public void Schema_options_win_over_ref_options_and_float_ignores_them()
    {
        var enumS = Schema(@"{""desc"":""Enable"",""value"":""ON"",""type"":""enum"",""options"":[""ON"",""OFF""]}", @"root\app\enum\test");
        var e = new ParameterFieldViewModel(enumS, "\"ON\"", new[] { "ShouldNotAppear" });
        Assert.Equal(new[] { "ON", "OFF" }, e.Options);

        var floatS = Schema(@"{""desc"":""Gain"",""value"":0.0,""type"":""float"",""min"":-20.0,""max"":20.0}", @"root\app\gain\gain");
        var g = new ParameterFieldViewModel(floatS, "0.0", new[] { "ShouldNotAppear" });
        Assert.Equal("float", g.Kind);
        Assert.Empty(g.Options);
    }

    [Fact]
    public void Null_ref_options_keep_todays_behavior()
    {
        var s = Schema(@"{""desc"":""IR file"",""value"":""Cab1"",""type"":""item"",""ref"":""root\\ir""}", @"root\app\ir\ir");
        var f = new ParameterFieldViewModel(s, "\"Cab1\"");
        Assert.Equal("string", f.Kind);
        Assert.Empty(f.Options);
    }

    // ---- refreshable options (v0.9.7 Task 5) ----

    [Fact] public void SetRefOptions_replaces_a_device_supplied_list()
    {
        var schema = Schema(@"{""desc"":""Model"",""value"":""mA"",""type"":""item"",""ref"":""root\\amp""}", @"root\app\amp\amp");
        var f = new ParameterFieldViewModel(schema, "\"mA\"", new[] { "mA", "mB" });
        f.SetRefOptions(new[] { "mB", "mC" });
        Assert.Equal(new[] { "mA", "mB", "mC" }, f.Options);   // current value unioned in at the front
    }

    [Fact] public void SetRefOptions_does_not_duplicate_a_value_the_device_still_offers()
    {
        var schema = Schema(@"{""desc"":""Model"",""value"":""mA"",""type"":""item"",""ref"":""root\\amp""}", @"root\app\amp\amp");
        var f = new ParameterFieldViewModel(schema, "\"mA\"", new[] { "mA", "mB" });
        f.SetRefOptions(new[] { "mA", "mB", "mC" });
        Assert.Equal(new[] { "mA", "mB", "mC" }, f.Options);
    }

    [Fact] public void SetRefOptions_is_a_noop_for_a_schema_option_field()
    {
        var schema = Schema(@"{""desc"":""Enable"",""value"":""ON"",""type"":""enum"",""options"":[""ON"",""OFF""]}", @"root\app\amp\on_off");
        var f = new ParameterFieldViewModel(schema, "\"ON\"");
        Assert.Null(f.RefSource);
        f.SetRefOptions(new[] { "X" });
        Assert.Equal(new[] { "ON", "OFF" }, f.Options);
    }

    // ---- firmware default (`def`), used by the EQ activity icon ----

    [Fact] public void Float_field_exposes_the_schema_default()
    {
        var s = Schema(@"{""desc"":""Bass"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0,""def"":0.0}",
                       @"root\app\eq\bass");
        var f = new ParameterFieldViewModel(s, "3.5");
        Assert.Equal(0.0, f.Default);
    }

    [Fact] public void Field_without_a_schema_default_exposes_null()
    {
        var s = Schema(@"{""desc"":""Bass"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0}",
                       @"root\app\eq\bass");
        var f = new ParameterFieldViewModel(s, "3.5");
        Assert.Null(f.Default);
    }

    [Fact] public void Nonzero_schema_default_is_preserved_not_coerced()
    {
        var s = Schema(@"{""desc"":""Mid"",""value"":0.5,""type"":""float"",""min"":0.0,""max"":1.0,""def"":0.5}",
                       @"root\app\eq\mid");
        var f = new ParameterFieldViewModel(s, "0.5");
        Assert.Equal(0.5, f.Default);
    }

    // ---- per-slider reset (EQ sliders are hard to land on 0 by hand) ----

    [Fact] public void Reset_snaps_the_value_back_to_the_firmware_default()
    {
        // fw 2.5.1 publishes def 0.0 for every EQ band (see HARDWARE-VALIDATION-ui-issues-9-14),
        // so reset-to-default and reset-to-zero coincide there; this asserts the general rule.
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0,""def"":0.0}",
                       @"root\app\eq\low");
        var f = new ParameterFieldViewModel(s, "3.6");
        Assert.Equal(3.6, f.Number);

        f.ResetCommand.Execute(null);

        Assert.Equal(0.0, f.Number);
    }

    [Fact] public void Reset_honours_a_nonzero_firmware_default()
    {
        var s = Schema(@"{""desc"":""Mix"",""value"":0.5,""type"":""float"",""min"":0.0,""max"":1.0,""def"":0.5}",
                       @"root\app\delay\mix");
        var f = new ParameterFieldViewModel(s, "0.9");

        f.ResetCommand.Execute(null);

        Assert.Equal(0.5, f.Number);
    }

    [Fact] public void Reset_falls_back_to_zero_without_a_firmware_default()
    {
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0}",
                       @"root\app\eq\low");
        var f = new ParameterFieldViewModel(s, "3.6");

        f.ResetCommand.Execute(null);

        Assert.Equal(0.0, f.Number);
    }

    [Fact] public void Reset_dirties_the_field_so_the_change_can_be_saved()
    {
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0,""def"":0.0}",
                       @"root\app\eq\low");
        var f = new ParameterFieldViewModel(s, "3.6");
        Assert.False(f.IsDirty);         // construction captures the loaded value as the baseline

        f.ResetCommand.Execute(null);

        Assert.True(f.IsDirty);          // otherwise the reset could never be saved to the pedal
    }

    // ---- changed-from-default highlight on the reset button ----

    [Fact] public void A_field_at_its_default_is_not_flagged_as_changed()
    {
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0,""def"":0.0}",
                       @"root\app\eq\low");
        Assert.False(new ParameterFieldViewModel(s, "0").IsChangedFromDefault);
    }

    [Fact] public void A_field_away_from_a_nonzero_default_is_flagged_as_changed()
    {
        var s = Schema(@"{""desc"":""Release"",""value"":400.0,""type"":""float"",""min"":100.0,""max"":2000.0,""def"":400.0,""unit"":""ms""}",
                       @"root\app\comp\release");
        var f = new ParameterFieldViewModel(s, "800");
        Assert.True(f.IsChangedFromDefault);
        // ...and sitting ON a non-zero default is NOT changed — the whole point of using `def`.
        Assert.False(new ParameterFieldViewModel(s, "400").IsChangedFromDefault);
    }

    [Fact] public void Changed_flag_tracks_edits_and_notifies()
    {
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0,""def"":0.0}",
                       @"root\app\eq\low");
        var f = new ParameterFieldViewModel(s, "0");
        int notifications = 0;
        f.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ParameterFieldViewModel.IsChangedFromDefault)) notifications++; };

        f.Number = 4.0;
        Assert.True(f.IsChangedFromDefault);
        Assert.True(notifications > 0);

        f.ResetCommand.Execute(null);
        Assert.False(f.IsChangedFromDefault);   // reset must clear its own highlight
    }

    [Fact] public void A_non_float_field_is_never_flagged_as_changed()
    {
        var s = Schema(@"{""desc"":""Mode"",""value"":""ROOM"",""type"":""enum"",""options"":[""ROOM"",""HALL""]}",
                       @"root\app\reverb\mode");
        var f = new ParameterFieldViewModel(s, "\"ROOM\"");
        f.Text = "HALL";
        Assert.False(f.IsChangedFromDefault);
    }

    // The tooltip must name the ACTUAL default: only the EQ bands default to 0, and 58 of the
    // pedal's 86 float params do not (gate threshold -60 dB, comp release 400 ms, ir lo_cut 20 Hz).

    [Fact] public void Reset_tooltip_names_the_default_with_its_unit_and_precision()
    {
        var s = Schema(@"{""desc"":""Release"",""value"":400.0,""type"":""float"",""min"":100.0,""max"":2000.0,""def"":400.0,""unit"":""ms"",""dec"":0}",
                       @"root\app\comp\release");
        var f = new ParameterFieldViewModel(s, "800");
        Assert.Equal("Reset to default (400 ms)", f.ResetTooltip);
    }

    [Fact] public void Reset_tooltip_honours_a_negative_default_and_decimal_places()
    {
        var s = Schema(@"{""desc"":""Threshold"",""value"":-60.0,""type"":""float"",""min"":-100.0,""max"":-20.0,""def"":-60.0,""unit"":""dB"",""dec"":1}",
                       @"root\app\gate\threshold");
        var f = new ParameterFieldViewModel(s, "-40");
        Assert.Equal("Reset to default (-60.0 dB)", f.ResetTooltip);
    }

    [Fact] public void Reset_tooltip_omits_an_absent_unit()
    {
        var s = Schema(@"{""desc"":""Sensitivity"",""value"":0.5,""type"":""float"",""min"":0.0,""max"":1.0,""def"":0.5,""dec"":2}",
                       @"root\app\comp\sensitivity");
        var f = new ParameterFieldViewModel(s, "0.9");
        Assert.Equal("Reset to default (0.50)", f.ResetTooltip);
    }

    [Fact] public void Reset_tooltip_does_not_space_a_percent_sign()
    {
        var s = Schema(@"{""desc"":""Vol"",""value"":50.0,""type"":""float"",""min"":0.0,""max"":100.0,""def"":50.0,""unit"":""%"",""dec"":0}",
                       @"root\app\amp\vol");
        var f = new ParameterFieldViewModel(s, "80");
        Assert.Equal("Reset to default (50%)", f.ResetTooltip);
    }

    [Fact] public void Reset_tooltip_falls_back_when_the_schema_omits_dec()
    {
        var s = Schema(@"{""desc"":""Depth"",""value"":5.0,""type"":""float"",""min"":0.0,""max"":10.0,""def"":5.0}",
                       @"root\app\amp\depth");
        var f = new ParameterFieldViewModel(s, "7");
        Assert.Equal("Reset to default (5)", f.ResetTooltip);
    }

    [Fact] public void Reset_is_a_noop_for_a_field_already_at_its_default()
    {
        var s = Schema(@"{""desc"":""Low"",""value"":0.0,""type"":""float"",""min"":-15.0,""max"":15.0,""def"":0.0}",
                       @"root\app\eq\low");
        var f = new ParameterFieldViewModel(s, "0");
        f.MarkClean();

        f.ResetCommand.Execute(null);

        Assert.Equal(0.0, f.Number);
        Assert.False(f.IsDirty);
    }
}
