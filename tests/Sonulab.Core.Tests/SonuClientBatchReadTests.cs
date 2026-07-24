using Sonulab.Core;
using Xunit;

public class SonuClientBatchReadTests
{
    private const string Path = @"root\presets";

    private static (SonuClient client, BatchLinkDouble link, byte[] blob) Make(int chunks = 8)
    {
        var blob = BatchLinkDouble.MakeBlob(chunks);
        var link = new BatchLinkDouble(blob, index: 0);
        return (new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0), link, blob);
    }

    [Fact]
    public async Task Multi_chunk_read_uses_a_single_batch_and_no_lockstep_commands()
    {
        var (client, link, blob) = Make();
        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        Assert.Equal(8, link.BatchCommands.Count);
        Assert.Empty(link.LockstepCommands);
    }

    [Fact]
    public async Task A_chunk_the_batch_dropped_is_repaired_lockstep()
    {
        var (client, link, blob) = Make();
        link.DropInBatch.Add(3);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);                                  // complete despite the drop
        Assert.Single(link.LockstepCommands);
        Assert.Contains("\"chunk\":3", link.LockstepCommands[0]);   // ONLY the missing chunk re-read
    }

    [Fact]
    public async Task A_torn_value_is_treated_as_missing_and_repaired()
    {
        var (client, link, blob) = Make();
        link.TearInBatch.Add(5);                                    // odd-length hex

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        Assert.Single(link.LockstepCommands);
        Assert.Contains("\"chunk\":5", link.LockstepCommands[0]);
    }

    [Fact]
    public async Task Shifted_windows_never_mis_attribute_chunk_data()
    {
        // An unsolicited record plus a dropped response shift every position. If the client
        // trusted window[i] it would silently return the WRONG chunk's bytes here.
        var (client, link, blob) = Make();
        link.InjectExtraWindow = true;
        link.DropInBatch.Add(2);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        for (int c = 1; c <= 8; c++)
            Assert.All(Enumerable.Range(0, 128), i => Assert.Equal((byte)c, bytes[(c - 1) * 128 + i]));
    }

    [Fact]
    public async Task An_unrecoverable_chunk_contributes_zero_bytes_after_two_repair_attempts()
    {
        // Today's permissive contract: the short buffer reaches SlotBlobService's validated
        // wrappers, which fail loudly. The batch path must not change that.
        var (client, link, _) = Make();
        link.DropEverywhere.Add(4);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(7 * 128, bytes.Length);
        Assert.Equal(2, link.LockstepCommands.Count(c => c.Contains("\"chunk\":4")));
    }

    [Fact]
    public async Task Single_chunk_read_stays_on_the_lockstep_path()
    {
        var (client, link, blob) = Make();
        var bytes = await client.DReadChunkRangeAsync(Path, 0, 3, 1);

        Assert.Equal(blob.Skip(2 * 128).Take(128).ToArray(), bytes);
        Assert.Empty(link.BatchCommands);
        Assert.Single(link.LockstepCommands);
    }

    [Fact]
    public async Task Zero_count_returns_empty_without_touching_the_link()
    {
        var (client, link, _) = Make();
        Assert.Empty(await client.DReadChunkRangeAsync(Path, 0, 1, 0));
        Assert.Empty(link.BatchCommands);
        Assert.Empty(link.LockstepCommands);
    }

    [Fact]
    public async Task DReadBlobAsync_reads_a_full_64_chunk_slot_through_the_batch_path()
    {
        var (client, link, blob) = Make(chunks: 64);
        var bytes = await client.DReadBlobAsync(Path, 0, 64);

        Assert.Equal(blob, bytes);
        Assert.Equal(64, link.BatchCommands.Count);
    }
}
