using Sonulab.Core.Model;
using Xunit;

public class NodeSchemaTests
{
    [Fact] public void Float_schema_reads_range_and_unit()
    {
        NodeRecord.TryParse(@"root\app\gate\threshold:{""desc"":""Threshold"",""value"":-60.0,""type"":""float"",""min"":-100.0,""max"":-20.0,""def"":-60.0,""unit"":""dB"",""shape"":0.5,""inv"":0}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("float", s.Type);
        Assert.Equal(-100.0, s.Min);
        Assert.Equal(-20.0, s.Max);
        Assert.Equal("dB", s.Unit);
        Assert.Empty(s.Options);
    }

    [Fact] public void Enum_schema_reads_options()
    {
        NodeRecord.TryParse(@"root\app\reverb\mode:{""desc"":""Mode"",""value"":""ROOM"",""type"":""enum"",""def"":""ROOM"",""options"":[""ROOM"",""HALL"",""GALAXY""]}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("enum", s.Type);
        Assert.Equal(new[] { "ROOM", "HALL", "GALAXY" }, s.Options);
    }

    [Fact] public void Plist_schema_reads_ref()
    {
        NodeRecord.TryParse(@"root\app\amp\amp:{""desc"":""Model"",""value"":""Pano-Verb"",""type"":""plist"",""ref"":""root\\amp""}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("plist", s.Type);
        Assert.Equal(@"root\amp", s.Ref);
    }
}
