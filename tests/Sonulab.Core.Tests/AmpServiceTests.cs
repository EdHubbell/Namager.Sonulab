using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

public class AmpServiceTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"amp-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true); }

    private (AmpService svc, FakeAmpDevice dev) Make(bool corruptReadback = false)
    {
        var dev = corruptReadback ? new CorruptReadbackAmpDevice() : new FakeAmpDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        return (new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0), dev);
    }

    /// <summary>Commits fine but serves back a corrupted blob — exercises the verify path.</summary>
    private sealed class CorruptReadbackAmpDevice : FakeAmpDevice
    {
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            var r = await base.SendAsync(command, ct);
            if (command.StartsWith("dread") && command.Contains("\"chunk\":1}") && r.Length > 0)
                r = r.Replace("\"value\":\"" + r.Split("\"value\":\"")[1][..2], "\"value\":\"ff");   // flip first byte
            return r;
        }
    }

    private static byte[] Blob(byte fill) => Enumerable.Repeat(fill, 12288).ToArray();

    [Fact]
    public async Task List_pads_to_30_slots_with_empty_flags()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(0, "Clean", Blob(1));
        dev.SeedAmp(4, "Lead", Blob(2));
        var slots = await svc.ListAmpsAsync();
        Assert.Equal(30, slots.Count);
        Assert.Equal("Clean", slots[0].Name);
        Assert.False(slots[0].IsEmpty);
        Assert.True(slots[1].IsEmpty);
        Assert.Equal(4, slots[4].Index);
    }

    [Fact]
    public async Task ReadAmp_returns_the_12288_byte_blob()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(2, "X", Blob(0x33));
        var blob = await svc.ReadAmpAsync(2);
        Assert.Equal(Blob(0x33), blob);
    }

    [Fact]
    public async Task Delete_backs_up_then_clears()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(3, "Doomed", Blob(0x44));
        await svc.DeleteAmpAsync(3);
        Assert.Null(dev.SlotNames[3]);
        var backup = Directory.GetFiles(_backupDir, "amp-3-*-deleted.vxamp").Single();
        Assert.Equal(Blob(0x44), await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task Delete_empty_slot_is_a_noop_with_no_backup_and_no_dread()
    {
        var (svc, dev) = Make();
        await svc.DeleteAmpAsync(9);
        Assert.False(Directory.Exists(_backupDir) && Directory.GetFiles(_backupDir).Length > 0);
        Assert.DoesNotContain(dev.CommandLog, c => c.StartsWith("dread"));
        Assert.DoesNotContain(dev.CommandLog, c => c.StartsWith("dwrite"));
    }

    [Fact]
    public async Task Rename_writes_padded_name_at_chunk_minus1_and_keeps_blob()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(1, "Old Name", Blob(0x55));
        await svc.RenameAmpAsync(1, "  New Name  ");        // trims
        Assert.Equal("New Name", dev.SlotNames[1]);
        Assert.Equal(Blob(0x55), dev.SlotBlobs[1]);
    }

    [Fact]
    public async Task Rename_validates_name()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(1, "Old", Blob(1));
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.RenameAmpAsync(1, "   "));
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.RenameAmpAsync(1, "naïve"));   // non-ASCII
        await svc.RenameAmpAsync(1, new string('x', 40));   // long names truncate to 31
        Assert.Equal(new string('x', 31), dev.SlotNames[1]);
    }

    // ---- UploadAmpAsync (Task 3) ----

    [Fact]
    public async Task Upload_to_empty_slot_commits_and_skips_backup_dread()
    {
        var (svc, dev) = Make();
        var payload = Blob(0x77);
        var stages = new List<AmpUploadStage>();
        await svc.UploadAmpAsync(6, payload, "Fresh", new SyncProgress<AmpUploadProgress>(p => stages.Add(p.Stage)));
        Assert.Equal("Fresh", dev.SlotNames[6]);
        Assert.Equal(payload, dev.SlotBlobs[6]);
        // empty slot: NO dread before the write burst (the commit-killer), no backup file.
        // (The mandatory post-write verify readback legitimately issues dreads afterward.)
        var firstWrite = dev.CommandLog.ToList().FindIndex(c => c.StartsWith("dwrite"));
        Assert.True(firstWrite >= 0, "expected at least one dwrite in the command log");
        Assert.DoesNotContain(dev.CommandLog.Take(firstWrite), c => c.StartsWith("dread"));
        Assert.False(Directory.Exists(_backupDir) && Directory.GetFiles(_backupDir).Length > 0);
        Assert.Contains(AmpUploadStage.Writing, stages);
        Assert.Equal(AmpUploadStage.Done, stages[^1]);
        Assert.DoesNotContain(AmpUploadStage.BackingUp, stages);
    }

    [Fact]
    public async Task Upload_over_occupied_slot_backs_up_first()
    {
        var (svc, dev) = Make();
        dev.SeedAmp(2, "Old", Blob(0x11));
        await svc.UploadAmpAsync(2, Blob(0x99), "Replacement", null);
        Assert.Equal("Replacement", dev.SlotNames[2]);
        var backup = Directory.GetFiles(_backupDir, "amp-2-*.vxamp").Single();
        Assert.Equal(Blob(0x11), await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task Upload_validates_size_and_name()
    {
        var (svc, _) = Make();
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.UploadAmpAsync(0, new byte[100], "X", null));
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.UploadAmpAsync(0, Blob(1), "  ", null));
    }

    [Fact]
    public async Task Ack_mismatch_aborts_before_commit_leaving_slot_uncommitted()
    {
        var (svc, dev) = Make();
        dev.CorruptAckAtChunk = 10;
        var ex = await Assert.ThrowsAsync<AmpServiceException>(() => svc.UploadAmpAsync(4, Blob(0x42), "Never", null));
        Assert.Contains("ACK", ex.Message);
        Assert.Null(dev.SlotNames[4]);                     // commit never sent
        Assert.False(dev.CommitSeen);
    }

    [Fact]
    public async Task Verify_failure_clears_the_slot_and_reports_diagnostics()
    {
        var (svc, dev) = Make(corruptReadback: true);
        var ex = await Assert.ThrowsAsync<AmpServiceException>(() => svc.UploadAmpAsync(1, Blob(0x50), "Corrupt", null));
        Assert.Contains("verify", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("offset", ex.Message);             // first-diff diagnostic
        Assert.Null(dev.SlotNames[1]);                     // cleared: never leave a corrupt amp selectable
    }

    [Fact]
    public async Task Upload_reports_chunk_progress_out_of_98()
    {
        var (svc, dev) = Make();
        var writes = new List<AmpUploadProgress>();
        await svc.UploadAmpAsync(0, Blob(3), "P",
            new SyncProgress<AmpUploadProgress>(p => { if (p.Stage == AmpUploadStage.Writing) writes.Add(p); }));
        Assert.Equal(98, writes.Count);                    // name + 96 payload + commit
        Assert.All(writes, p => Assert.Equal(98, p.ChunksTotal));
        Assert.Equal(98, writes[^1].ChunksDone);
    }
}

file sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
