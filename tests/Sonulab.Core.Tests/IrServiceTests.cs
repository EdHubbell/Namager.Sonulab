using Sonulab.Core;
using Sonulab.Core.Services;

public class IrServiceTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"ir-backups-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }

    private (IrService svc, FakeIrDevice dev) Make()
    {
        var dev = new FakeIrDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        return (new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0), dev);
    }

    private static byte[] Blob(byte fill) => Enumerable.Repeat(fill, 4096).ToArray();

    [Fact]
    public async Task List_pads_to_30()
    {
        var (svc, dev) = Make();
        dev.SeedIr(0, "Cab A", Blob(1));
        var slots = await svc.ListIrsAsync();
        Assert.Equal(30, slots.Count);
        Assert.Equal("Cab A", slots[0].Name);
        Assert.True(slots[1].IsEmpty);
    }

    [Fact]
    public async Task Upload_to_empty_slot_commits_34_acked_writes_and_skips_backup_dread()
    {
        var (svc, dev) = Make();
        var writes = new List<SlotUploadProgress>();
        await svc.UploadIrAsync(3, Blob(0x5A), "New Cab",
            new SyncIrProgress(p => { if (p.Stage == SlotUploadStage.Writing) writes.Add(p); }));
        Assert.Equal("New Cab", dev.SlotNames[3]);
        Assert.Equal(Blob(0x5A), dev.SlotBlobs[3]);
        Assert.Equal(34, writes.Count);                       // name + 32 payload + commit
        Assert.All(writes, p => Assert.Equal(34, p.ChunksTotal));
        int firstWrite = dev.CommandLog.ToList().FindIndex(c => c.StartsWith("dwrite"));
        Assert.DoesNotContain(dev.CommandLog.Take(firstWrite), c => c.StartsWith("dread"));
    }

    [Fact]
    public async Task Upload_validates_size_slot_and_name()
    {
        var (svc, _) = Make();
        await Assert.ThrowsAsync<IrServiceException>(() => svc.UploadIrAsync(0, new byte[100], "X", null));
        await Assert.ThrowsAsync<IrServiceException>(() => svc.UploadIrAsync(30, Blob(1), "X", null));
        var ex = await Assert.ThrowsAsync<IrServiceException>(() => svc.UploadIrAsync(0, Blob(1), "  ", null));
        Assert.Contains("IR", ex.Message);                    // kind-noun message
    }

    [Fact]
    public async Task Delete_backs_up_then_clears_and_rename_keeps_blob()
    {
        var (svc, dev) = Make();
        dev.SeedIr(2, "Doomed", Blob(0x11));
        await svc.DeleteIrAsync(2);
        Assert.Null(dev.SlotNames[2]);
        Assert.Single(Directory.GetFiles(_backupDir, "ir-2-*-deleted.irblob"));

        dev.SeedIr(5, "Old", Blob(0x22));
        await svc.RenameIrAsync(5, "New Name");
        Assert.Equal("New Name", dev.SlotNames[5]);
        Assert.Equal(Blob(0x22), dev.SlotBlobs[5]);
    }

    [Fact]
    public async Task Ack_mismatch_aborts_before_commit()
    {
        var (svc, dev) = Make();
        dev.CorruptAckAtChunk = 5;
        await Assert.ThrowsAsync<IrServiceException>(() => svc.UploadIrAsync(1, Blob(0x42), "Never", null));
        Assert.Null(dev.SlotNames[1]);
        Assert.False(dev.CommitSeen);
    }
}

file sealed class SyncIrProgress(Action<SlotUploadProgress> handler) : IProgress<SlotUploadProgress>
{
    public void Report(SlotUploadProgress value) => handler(value);
}
