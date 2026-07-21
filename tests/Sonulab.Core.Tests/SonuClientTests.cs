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

    [Fact] public async Task WriteAsync_updates_scalar()
    {
        var (client, link) = await Connected();
        link.SeedScalar(@"root\app\amp\on_off", "\"ON\"");
        await client.WriteAsync(@"root\app\amp\on_off", "\"OFF\"");
        Assert.Equal("OFF", await client.ReadValueAsync(@"root\app\amp\on_off"));
    }

    [Fact] public async Task SaveAsync_writes_name_to_preset_node()
    {
        var (client, link) = await Connected();
        await client.SaveAsync(@"root\app\preset", "Test");
        Assert.Equal("Test", await client.ReadValueAsync(@"root\app\preset"));
    }

    // ISonuLink returning a scripted sequence of raw responses (ignores the command), to model the
    // WiFi empty-record quirk. Counts sends.
    private sealed class ScriptedLink : ISonuLink
    {
        private readonly Queue<string> _resp;
        public int Sends { get; private set; }
        public ScriptedLink(params string[] responses) => _resp = new Queue<string>(responses);
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        { Sends++; return Task.FromResult(_resp.Count > 0 ? _resp.Dequeue() : ""); }
    }

    [Fact] public async Task ReadList_retries_past_an_empty_record_response()
    {
        // WiFi quirk: the pedal intermittently answers with an empty record instead of the real
        // response. The read must retry, not silently return an empty list (that surfaced as
        // "no presets" and reorder verify failures over WiFi).
        var link = new ScriptedLink("\r\n\0", "root\\presets:{\"value\":[\"A\",\"B\"]}\r\n\0");
        var client = new SonuClient(link, readRetryAttempts: 4, readRetryDelayMs: 0);
        var list = await client.ReadListAsync(@"root\presets");
        Assert.Equal(new[] { "A", "B" }, list);
        Assert.Equal(2, link.Sends);   // one empty, then the real one
    }

    [Fact] public async Task ReadValue_retries_past_an_empty_record_response()
    {
        var link = new ScriptedLink("\r\n\0", "\r\n\0", "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n\0");
        var client = new SonuClient(link, readRetryAttempts: 4, readRetryDelayMs: 0);
        Assert.Equal("AMP Station", await client.ReadValueAsync(@"root\sys\_name"));
        Assert.Equal(3, link.Sends);
    }

    [Fact] public async Task Read_gives_up_and_returns_empty_after_the_retry_budget()
    {
        var link = new ScriptedLink("\r\n\0", "\r\n\0", "\r\n\0", "\r\n\0", "\r\n\0");
        var client = new SonuClient(link, readRetryAttempts: 3, readRetryDelayMs: 0);
        var list = await client.ReadListAsync(@"root\presets");
        Assert.Empty(list);
        Assert.Equal(3, link.Sends);   // exactly the budget, then stop
    }

    [Fact] public async Task ReadList_retries_past_a_stale_ack_shaped_record()
    {
        // WiFi desync (wire capture 2026-07-21): a late rename ACK — a root\presets record with NO
        // value array — can arrive in place of the real list. It parses as a record, so an
        // any-record retry predicate is satisfied and the read silently returns an empty list
        // ("0/30 presets", reorder verify failures). The retry must demand the EXPECTED record.
        var link = new ScriptedLink(
            "dwrite root\\presets:{\"index\":23,\"chunk\":-1}\r\n\0",         // stale ACK, wrong shape
            "root\\presets:{\"value\":[\"A\",\"B\"]}\r\n\0");                  // the real list
        var client = new SonuClient(link, readRetryAttempts: 4, readRetryDelayMs: 0);
        var list = await client.ReadListAsync(@"root\presets");
        Assert.Equal(new[] { "A", "B" }, list);
        Assert.Equal(2, link.Sends);
    }

    [Fact] public async Task ReadValue_retries_past_a_record_for_a_different_path()
    {
        var link = new ScriptedLink(
            "root\\presets:{\"value\":[\"A\"]}\r\n\0",                        // stale response, wrong path
            "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n\0");
        var client = new SonuClient(link, readRetryAttempts: 4, readRetryDelayMs: 0);
        Assert.Equal("AMP Station", await client.ReadValueAsync(@"root\sys\_name"));
        Assert.Equal(2, link.Sends);
    }
}
