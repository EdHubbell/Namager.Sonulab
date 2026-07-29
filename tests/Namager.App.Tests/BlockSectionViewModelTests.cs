using System.Globalization;
using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Xunit;

public class BlockSectionViewModelTests
{
    static ParameterFieldViewModel Field(string path, double def, double value)
    {
        var json = $@"{{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0,""def"":{def.ToString(CultureInfo.InvariantCulture)}}}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r),
            value.ToString(CultureInfo.InvariantCulture));
    }

    static ParameterFieldViewModel FieldNoDefault(string path, double value)
    {
        var json = @"{""desc"":""D"",""value"":0.0,""type"":""float"",""min"":-12.0,""max"":12.0}";
        Assert.True(NodeRecord.TryParse(path + ":" + json, out var r));
        return new ParameterFieldViewModel(NodeSchema.FromRecord(r),
            value.ToString(CultureInfo.InvariantCulture));
    }

    [Fact] public void Block_with_every_field_at_its_default_is_not_active()
    {
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        b.Fields.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Fields.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void A_field_away_from_its_default_makes_the_block_active()
    {
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        b.Fields.Add(Field(@"root\app\eq\bass", def: 0.0, value: 0.0));
        b.Fields.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.9));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Nonzero_default_at_rest_is_not_active()
    {
        // The whole reason we use `def` and not literal zero: 0.5 here is FLAT, not a boost.
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        b.Fields.Add(Field(@"root\app\eq\mid", def: 0.5, value: 0.5));
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Missing_default_falls_back_to_zero_as_neutral()
    {
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        b.Fields.Add(FieldNoDefault(@"root\app\eq\bass", value: 0.0));
        Assert.False(b.IsEqActive);
        b.Fields.Add(FieldNoDefault(@"root\app\eq\treble", value: 2.0));
        Assert.True(b.IsEqActive);
    }

    [Fact] public void Editing_a_field_recomputes_activity_and_notifies()
    {
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        var bass = Field(@"root\app\eq\bass", def: 0.0, value: 0.0);
        b.Fields.Add(bass);
        Assert.False(b.IsEqActive);

        bool notified = false;
        b.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BlockSectionViewModel.IsEqActive)) notified = true; };

        bass.Number = 4.0;
        Assert.True(b.IsEqActive);
        Assert.True(notified);

        bass.Number = 0.0;
        Assert.False(b.IsEqActive);
    }

    [Fact] public void Fields_added_before_and_after_are_both_tracked()
    {
        var b = new BlockSectionViewModel("Equalizer") { ShowEqIcon = true };
        var late = Field(@"root\app\eq\treble", def: 0.0, value: 0.0);
        b.Fields.Add(late);
        late.Number = 3.0;
        Assert.True(b.IsEqActive);
    }
}
