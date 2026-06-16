using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class BackupServiceTests
{
    [Fact] public async Task SnapshotAll_writes_one_pst_per_nonempty_slot()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Alpha", new[] { @"root\app\amp\amp:{""value"":""AmpA""}" });
        dev.SeedSlot(3, "Bravo", new[] { @"root\app\amp\amp:{""value"":""AmpB""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var backup = new BackupService(repo);

        var dir = Path.Combine(Path.GetTempPath(), "sonulab-bk-" + Guid.NewGuid().ToString("N"));
        int n = await backup.SnapshotAllAsync(dir);

        Assert.Equal(2, n);
        Assert.Equal(2, Directory.GetFiles(dir, "*.pst").Length);
        Assert.True(File.Exists(Path.Combine(dir, "00 - Alpha.pst")));
        Assert.Equal(8192, new FileInfo(Path.Combine(dir, "00 - Alpha.pst")).Length);
        Directory.Delete(dir, true);
    }

    [Fact] public async Task RestoreSlot_writes_pst_back_to_device()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Alpha", new[] { @"root\app\amp\amp:{""value"":""AmpA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var backup = new BackupService(repo);

        var dir = Path.Combine(Path.GetTempPath(), "sonulab-rs-" + Guid.NewGuid().ToString("N"));
        await backup.SnapshotAllAsync(dir);
        var pst = Path.Combine(dir, "00 - Alpha.pst");

        await repo.DeleteAsync(0);
        Assert.True((await repo.ListPresetsAsync())[0].IsEmpty);

        await backup.RestoreSlotAsync(0, pst);
        var restored = await repo.ReadPresetAsync(0);
        Assert.Equal("\"AmpA\"", restored.GetValueJson(@"root\app\amp\amp"));
        Directory.Delete(dir, true);
    }
}
