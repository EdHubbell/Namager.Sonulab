using System.Globalization;
using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Xunit;

public class ParameterGroupViewModelTests
{
    static ParameterFieldViewModel Field(string path, double def, double value)
    {
        var json = $@"{{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0,""def"":{def.ToString(CultureInfo.InvariantCulture)}}}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), value.ToString(CultureInfo.InvariantCulture));
    }

    static ParameterFieldViewModel FieldNoDefault(string path, double value)
    {
        var json = @"{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), value.ToString(CultureInfo.InvariantCulture));
    }

    static ParameterFieldViewModel Enum(string path, string value)
    {
        var json = @"{""desc"":""Enable"",""value"":""ON"",""type"":""enum"",""options"":[""ON"",""OFF""]}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r), "\"" + value + "\"");
    }

    static ParameterGroupViewModel Group(string path = @"root\app\eq", string header = "Equalizer") =>
        new(header, path);

    // ---- ported from BlockSectionViewModelTests ----

    [Fact] public void Group_with_every_field_at_its_default_is_not_active()
    {
        var b = Group(); b.ShowEqIcon = true;
        b.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void A_field_away_from_its_default_makes_the_group_active()
    {
        var b = Group(); b.ShowEqIcon = true;
        b.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.9));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Nonzero_default_at_rest_is_not_active()
    {
        // The whole reason we use `def` and not literal zero: 0.5 here is FLAT, not a boost.
        var b = Group();
        b.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Missing_default_falls_back_to_zero_as_neutral()
    {
        var b = Group();
        b.Add(FieldNoDefault(@"root\app\eq\bass", value: 0.0));
        Assert.False(b.IsEqActive);
        b.Add(FieldNoDefault(@"root\app\eq\treble", value: 2.0));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Editing_a_field_recomputes_activity_and_notifies()
    {
        var b = Group();
        var bass = Field(@"root\app\eq\bass", def: 0.0, value: 0.0);
        b.Add(bass);
        Assert.False(b.IsEqActive);

        bool notified = false;
        b.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterGroupViewModel.IsEqActive)) notified = true; };

        bass.Number = 4.0;
        Assert.True(b.IsEqActive);
        Assert.True(notified);

        bass.Number = 0.0;
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Fields_added_before_and_after_are_both_tracked()
    {
        var b = Group();
        var late = Field(@"root\app\eq\treble", def: 0.0, value: 0.0);
        b.Add(late);
        late.Number = 3.0;
        Assert.True(b.IsEqActive);
    }

    // ---- new: one type at every depth ----

    [Fact] public void Fields_and_Groups_are_filtered_views_over_one_ordered_Items_collection()
    {
        var b = Group(@"root\app\mod", "Modulation");
        var mode = Enum(@"root\app\mod\mode", "ON");
        var rate = new ParameterGroupViewModel("Rate", @"root\app\mod\rate");
        var depth = Field(@"root\app\mod\dpth", def: 50.0, value: 50.0);
        b.Add(mode); b.Add(rate); b.Add(depth);

        // Display order is interleaved; the filtered views keep their own order.
        Assert.Equal(new object[] { mode, rate, depth }, b.Items.ToArray());
        Assert.Equal(new[] { mode, depth }, b.Fields.ToArray());
        Assert.Equal(new[] { rate }, b.Groups.ToArray());
    }

    [Fact] public void InsertFirst_puts_a_container_s_own_value_at_the_top_of_its_group()
    {
        var g = new ParameterGroupViewModel("Rate", @"root\app\mod\rate");
        g.Add(Field(@"root\app\mod\rate\rawdata", def: 1.0, value: 1.0));
        g.InsertFirst(Enum(@"root\app\mod\rate", "ON"));
        Assert.Equal(@"root\app\mod\rate", g.Fields.First().Path);
    }

    [Fact] public void AttachEnableField_adopts_this_group_s_own_on_off_and_tracks_it()
    {
        var g = new ParameterGroupViewModel("Tremolo", @"root\app\mod\trfolder");
        g.Add(Enum(@"root\app\mod\trfolder\on_off", "ON"));
        g.Add(Field(@"root\app\mod\trfolder\dpt", def: 25.0, value: 25.0));
        g.AttachEnableField();

        Assert.True(g.Enabled);
        bool raised = false;
        g.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterGroupViewModel.Enabled)) raised = true; };
        g.EnableField!.Text = "OFF";
        Assert.False(g.Enabled);
        Assert.True(raised);
    }

    [Fact] public void A_group_without_an_on_off_reports_null_Enabled()
    {
        var g = new ParameterGroupViewModel("Tone and Character", @"root\app\mod\tcfolder");
        g.Add(Field(@"root\app\mod\tcfolder\emp", def: 50.0, value: 50.0));
        g.AttachEnableField();
        Assert.Null(g.Enabled);
    }

    [Fact] public void A_nested_group_s_on_off_does_not_become_the_parent_s_enable_field()
    {
        // Tremolo's on_off must not be mistaken for Modulation's — AttachEnableField only ever
        // looks at THIS group's own fields, never into nested groups.
        var parent = new ParameterGroupViewModel("Modulation", @"root\app\mod");
        var tremolo = new ParameterGroupViewModel("Tremolo", @"root\app\mod\trfolder");
        tremolo.Add(Enum(@"root\app\mod\trfolder\on_off", "ON"));
        parent.Add(tremolo);
        parent.AttachEnableField();
        Assert.Null(parent.EnableField);
    }
}
