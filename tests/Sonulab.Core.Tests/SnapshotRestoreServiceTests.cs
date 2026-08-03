using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class SnapshotRestoreServiceTests
{
    private static byte[] Blob(int size, byte fill) { var b = new byte[size]; Array.Fill(b, fill); return b; }

    private sealed record Rig(
        SnapshotRestoreService Svc,
        FakeSlotBlobDevice Presets, FakeSlotBlobDevice Amps, FakeSlotBlobDevice Irs,
        string BackupDir);

    private static Rig MakeRig()
    {
        var presets = new FakeSlotBlobDevice(@"root\presets", 64, 8192);
        var amps = new FakeSlotBlobDevice(@"root\amp", 96, 12288);
        var irs = new FakeSlotBlobDevice(@"root\ir", 32, 4096);
        foreach (var d in new[] { presets, amps, irs }) d.OpenAsync().GetAwaiter().GetResult();
        var dir = Path.Combine(Path.GetTempPath(), $"nmgr-restore-{Guid.NewGuid():N}");
        SlotBlobService S(FakeSlotBlobDevice dev, SlotBlobKind kind) =>
            new(new SonuClient(dev, backgroundQuietMs: 0), kind, dir,
                msg => new SnapshotRestoreException(msg), paceMs: 0, settleMs: 0);
        return new Rig(
            new SnapshotRestoreService(S(presets, SlotBlobKind.Preset),
                                       S(amps, SlotBlobKind.Amp), S(irs, SlotBlobKind.Ir)),
            presets, amps, irs, dir);
    }

    private static (SnapshotManifest, Dictionary<(SnapshotSlotKind, int), byte[]>) Snap(
        params (SnapshotSlotKind Kind, int Index, string Name, byte[] Blob)[] slots)
    {
        var manifest = new SnapshotManifest(SnapshotManifest.CurrentSchema, "2026-08-03T00:00:00Z",
            "test", new SnapshotDevice("StompStation", "2.5.1"),
            slots.Select(s => new SnapshotSlot(s.Kind, s.Index, s.Name,
                SnapshotArchive.ShaOf(s.Blob), null)).ToList());
        return (manifest, slots.ToDictionary(s => (s.Kind, s.Index), s => s.Blob));
    }

    [Fact]
    public async Task Plan_emits_write_clear_and_nothing_in_execution_order()
    {
        var rig = MakeRig();
        rig.Presets.SeedSlot(0, "KeepMe", Blob(8192, 1));       // snapshot also has slot 0 → Write
        rig.Presets.SeedSlot(4, "Doomed", Blob(8192, 2));       // snapshot empty at 4 → Clear
        rig.Irs.SeedSlot(2, "OldIr", Blob(4096, 3));            // snapshot empty at 2 → Clear
        var (manifest, blobs) = Snap(
            (SnapshotSlotKind.Preset, 0, "NewName", Blob(8192, 9)),
            (SnapshotSlotKind.Amp, 1, "AmpOne", Blob(12288, 8)),
            (SnapshotSlotKind.Ir, 0, "IrZero", Blob(4096, 7)));

        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        Assert.Equal(3, plan.WriteCount);
        Assert.Equal(2, plan.ClearCount);
        // Execution order: IRs first, then amps, then presets; writes and clears interleaved by index.
        Assert.Equal(new[]
        {
            (SnapshotSlotKind.Ir, 0, RestoreAction.Write),
            (SnapshotSlotKind.Ir, 2, RestoreAction.Clear),
            (SnapshotSlotKind.Amp, 1, RestoreAction.Write),
            (SnapshotSlotKind.Preset, 0, RestoreAction.Write),
            (SnapshotSlotKind.Preset, 4, RestoreAction.Clear),
        }, plan.Actions.Select(a => (a.Kind, a.Index, a.Action)));
        // Occupancy captured at plan time; Write to an occupied slot says so.
        Assert.True(plan.Actions.Single(a => a.Kind == SnapshotSlotKind.Preset && a.Index == 0).PedalOccupied);
        Assert.False(plan.Actions.Single(a => a.Kind == SnapshotSlotKind.Amp).PedalOccupied);
        // Clear actions carry the pedal's current name (informational).
        Assert.Equal("Doomed", plan.Actions.Single(a => a.Action == RestoreAction.Clear
                                                     && a.Kind == SnapshotSlotKind.Preset).Name);
    }

    [Fact]
    public async Task Plan_of_matching_pedal_and_snapshot_is_writes_only()
    {
        var rig = MakeRig();
        var blob = Blob(4096, 5);
        rig.Irs.SeedSlot(0, "Same", blob);
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "Same", blob));

        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        Assert.Equal(1, plan.WriteCount);
        Assert.Equal(0, plan.ClearCount);
    }
}
