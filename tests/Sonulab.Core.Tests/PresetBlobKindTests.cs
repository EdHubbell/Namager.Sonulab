using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

/// <summary>Covers the restore-snapshot plumbing added to SlotBlobService: the preset
/// SlotBlobKind, the now-public ReadAndArchiveAsync, and UploadAsync's skipBackup bypass —
/// following SlotBlobReorderTests' direct-construction pattern (SlotBlobService is exercised
/// straight, not through an AmpService/IrService front).</summary>
public class PresetBlobKindTests
{
    private static SlotBlobService MakeService(FakeSlotBlobDevice dev, SlotBlobKind kind, string? backupDir = null)
    {
        dev.OpenAsync().GetAwaiter().GetResult();
        return new SlotBlobService(new SonuClient(dev, backgroundQuietMs: 0), kind,
            backupDir ?? "backups", msg => new InvalidOperationException(msg), paceMs: 0, settleMs: 0);
    }

    [Fact]
    public async Task Preset_kind_uploads_via_the_staged_sequence_and_roundtrips_a_real_pst()
    {
        var dev = new FakeSlotBlobDevice(@"root\presets", 64, 8192);
        var svc = MakeService(dev, SlotBlobKind.Preset);
        var payload = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));

        await svc.UploadAsync(3, payload, "Quad Reverb SM57");

        Assert.True(dev.CommitSeen);
        Assert.Equal("Quad Reverb SM57", dev.SlotNames[3]);
        Assert.Equal(payload, dev.SlotBlobs[3]);
        var back = await svc.ReadAsync(3);
        Assert.Equal(payload, back);
    }

    [Fact]
    public async Task SkipBackup_true_skips_the_pre_write_dread_of_an_occupied_slot()
    {
        var dev = new FakeSlotBlobDevice(@"root\presets", 64, 8192);
        dev.SeedSlot(3, "Old", new byte[8192]);
        var svc = MakeService(dev, SlotBlobKind.Preset);
        var payload = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));

        int dreadsBefore = dev.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal));
        await svc.UploadAsync(3, payload, "New", progress: null, skipBackup: true);
        // The only dreads after the write must be the read-back verify (64 chunks), not a backup pass.
        int dreads = dev.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal)) - dreadsBefore;
        Assert.Equal(64, dreads);
    }

    [Fact]
    public async Task ReadAndArchiveAsync_writes_the_backup_file_and_returns_the_blob()
    {
        var dev = new FakeSlotBlobDevice(@"root\presets", 64, 8192);
        var seeded = new byte[8192]; seeded[0] = 0x42;
        dev.SeedSlot(5, "Keep", seeded);
        var dir = Path.Combine(Path.GetTempPath(), $"nmgr-arch-{Guid.NewGuid():N}");
        var svc = MakeService(dev, SlotBlobKind.Preset, dir);

        var blob = await svc.ReadAndArchiveAsync(5, "-prerestore");

        Assert.Equal(seeded, blob);
        var file = Assert.Single(Directory.GetFiles(dir, "preset-5-*-prerestore.pst"));
        Assert.Equal(seeded, File.ReadAllBytes(file));
        Directory.Delete(dir, recursive: true);
    }
}
