using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

public class AmpServiceTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"amp-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true); }

    private (AmpService svc, FakeAmpDevice dev) Make()
    {
        var dev = new FakeAmpDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        return (new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0), dev);
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
}
