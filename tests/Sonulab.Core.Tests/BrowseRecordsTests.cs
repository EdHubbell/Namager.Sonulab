using System.Text.Json;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class BrowseRecordsTests
{
    [Fact] public async Task BrowseRecordsAsync_exposes_list_metadata()
    {
        var link = new FakeSonuLink();
        link.SeedBrowse(@"root\presets",
            "root\\presets:{\"value\":[\"A\"],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}");
        await link.OpenAsync();
        var client = new SonuClient(link);

        var recs = await client.BrowseRecordsAsync(@"root\presets");
        var rec = Assert.Single(recs);
        Assert.Equal(@"root\presets", rec.Path);
        Assert.Equal(30, rec.Json.GetProperty("count").GetInt32());
        Assert.Equal(128, rec.Json.GetProperty("chunk").GetInt32());
        Assert.Equal("pst_pst", rec.Json.GetProperty("item_type").GetString());
    }
}
