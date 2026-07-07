using Sonulab.Core.Protocol;
using Xunit;

public class ResponseParserTests
{
    const string Raw =
        "root\\sys\\_meters\\_in0:{\"value\":-100.0}\r\n" +
        "root\\usb\\_status:{\"value\":\"OFF\"}\r\n" +
        "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n";

    [Fact] public void Records_splits_and_drops_empty_and_nul()
    {
        var recs = ResponseParser.Records("a:{}\r\n \r\nb:{}\r\n").ToList();
        Assert.Equal(new[] { "a:{}", "b:{}" }, recs);
    }

    [Fact] public void IsMeter_detects_meter_and_status()
    {
        Assert.True(ResponseParser.IsMeter("root\\sys\\_meters\\_out0:{\"value\":-1.0}"));
        Assert.True(ResponseParser.IsMeter("root\\usb\\_status:{\"value\":\"OFF\"}"));
        Assert.False(ResponseParser.IsMeter("root\\sys\\_name:{\"value\":\"AMP Station\"}"));
    }

    [Fact] public void NonMeterRecords_filters_stream()
    {
        var recs = ResponseParser.NonMeterRecords(Raw).ToList();
        Assert.Single(recs);
        Assert.StartsWith("root\\sys\\_name", recs[0]);
    }

    [Fact] public void ChunkHex_extracts_value_for_chunk()
    {
        var raw = "root\\presets:{\"index\":4,\"chunk\":1,\"value\":\"4142\"}\r\n";
        Assert.Equal("4142", ResponseParser.ChunkHex(raw, 1));
        Assert.Null(ResponseParser.ChunkHex(raw, 2));
    }

    // Index-checked overload: a stale response for the SAME chunk number of a DIFFERENT
    // slot (orphaned by a cancelled read) must not be accepted (slot-26 incident hardening).
    [Fact] public void ChunkHex_with_index_rejects_other_slots_records()
    {
        var raw = "root\\amp:{\"index\":7,\"chunk\":3,\"value\":\"aa\"}\r\n" +
                  "root\\amp:{\"index\":2,\"chunk\":3,\"value\":\"bb\"}\r\n";
        Assert.Equal("bb", ResponseParser.ChunkHex(raw, index: 2, chunk: 3));
        Assert.Equal("aa", ResponseParser.ChunkHex(raw, index: 7, chunk: 3));
        Assert.Null(ResponseParser.ChunkHex(raw, index: 5, chunk: 3));
        Assert.Null(ResponseParser.ChunkHex(raw, index: 2, chunk: 4));
    }
}
