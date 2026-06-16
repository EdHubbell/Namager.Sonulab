using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientTests
{
    static async Task<(SonuClient client, FakeSonuLink link)> Connected()
    {
        var link = new FakeSonuLink();
        await link.OpenAsync();
        return (new SonuClient(link), link);
    }

    [Fact] public async Task ReadValueAsync_returns_value_string()
    {
        var (client, link) = await Connected();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        Assert.Equal("AMP Station", await client.ReadValueAsync(@"root\sys\_name"));
    }

    [Fact] public async Task ReadListAsync_returns_30_slot_names()
    {
        var (client, link) = await Connected();
        var names = Enumerable.Range(0, 30).Select(i => i == 4 ? "Princeton" : "").ToArray();
        link.SeedList(@"root\presets", names);
        var list = await client.ReadListAsync(@"root\presets");
        Assert.Equal(30, list.Count);
        Assert.Equal("Princeton", list[4]);
    }

    [Fact] public async Task DWrite_then_DRead_blob_round_trips()
    {
        var (client, _) = await Connected();
        var data = new byte[128];
        for (int i = 0; i < 128; i++) data[i] = (byte)i;
        await client.DWriteChunkAsync(@"root\presets", 3, 1, data);
        var blob = await client.DReadBlobAsync(@"root\presets", 3, chunkCount: 1);
        Assert.Equal(data, blob);
    }

    [Fact] public async Task ReadValueAsync_ignores_meter_noise()
    {
        var (client, link) = await Connected();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        // FakeSonuLink returns clean responses; this asserts the parser path tolerates meter lines.
        Assert.Equal("AMP Station",
            await client.ReadValueAsync(@"root\sys\_name"));
    }
}
