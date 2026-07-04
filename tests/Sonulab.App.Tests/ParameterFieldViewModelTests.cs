using Sonulab.App.ViewModels;
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
}
