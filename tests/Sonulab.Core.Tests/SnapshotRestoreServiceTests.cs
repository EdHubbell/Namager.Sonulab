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

    [Fact]
    public async Task Execute_mirrors_the_snapshot_writes_clears_and_skips_identical()
    {
        var rig = MakeRig();
        var identical = Blob(4096, 5);
        rig.Irs.SeedSlot(0, "SameIr", identical);               // identical → skip
        rig.Amps.SeedSlot(1, "OldAmp", Blob(12288, 1));         // differs → write
        rig.Presets.SeedSlot(4, "Doomed", Blob(8192, 2));       // not in snapshot → clear
        var (manifest, blobs) = Snap(
            (SnapshotSlotKind.Ir, 0, "SameIr", identical),
            (SnapshotSlotKind.Amp, 1, "NewAmp", Blob(12288, 9)),
            (SnapshotSlotKind.Preset, 0, "NewPreset", Blob(8192, 8)));

        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        var result = await rig.Svc.ExecuteAsync(plan);

        Assert.Equal(new RestoreResult(Written: 2, SkippedIdentical: 1, Cleared: 1), result);
        Assert.Equal(identical, rig.Irs.SlotBlobs[0]);           // untouched
        Assert.Equal(Blob(12288, 9), rig.Amps.SlotBlobs[1]);     // overwritten
        Assert.Equal("NewAmp", rig.Amps.SlotNames[1]);
        Assert.Equal(Blob(8192, 8), rig.Presets.SlotBlobs[0]);   // written to empty slot
        Assert.Null(rig.Presets.SlotNames[4]);                   // cleared
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task Execute_skip_identical_costs_zero_staged_writes()
    {
        var rig = MakeRig();
        var same = Blob(4096, 5);
        rig.Irs.SeedSlot(0, "Same", same);
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "Same", same));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        await rig.Svc.ExecuteAsync(plan);

        Assert.DoesNotContain(rig.Irs.CommandLog, c => c.StartsWith("dwrite", StringComparison.Ordinal));
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task Execute_reuses_provided_current_blobs_no_compare_read_no_backup_file()
    {
        var rig = MakeRig();
        var pedalBlob = Blob(4096, 1);
        rig.Irs.SeedSlot(0, "Old", pedalBlob);
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "New", Blob(4096, 9)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        int dreadsAfterPlan = rig.Irs.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal));

        await rig.Svc.ExecuteAsync(plan,
            currentBlobs: new Dictionary<(SnapshotSlotKind, int), byte[]> { [(SnapshotSlotKind.Ir, 0)] = pedalBlob });

        // Only the upload's verify read-back (32 chunks) — no compare dread, no backup file.
        int dreads = rig.Irs.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal)) - dreadsAfterPlan;
        Assert.Equal(32, dreads);
        Assert.False(Directory.Exists(rig.BackupDir) &&
                     Directory.GetFiles(rig.BackupDir, "*-prerestore*").Length > 0);
    }

    [Fact]
    public async Task Execute_writes_prerestore_backups_when_reading_itself()
    {
        var rig = MakeRig();
        rig.Irs.SeedSlot(0, "Old", Blob(4096, 1));               // differs → compare read + backup
        rig.Presets.SeedSlot(4, "Doomed", Blob(8192, 2));        // clear → backup
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "New", Blob(4096, 9)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        await rig.Svc.ExecuteAsync(plan);

        Assert.Single(Directory.GetFiles(rig.BackupDir, "ir-0-*-prerestore.irblob"));
        Assert.Single(Directory.GetFiles(rig.BackupDir, "preset-4-*-prerestore.pst"));
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task Execute_never_dreads_an_empty_slot()
    {
        var rig = MakeRig();
        var (manifest, blobs) = Snap((SnapshotSlotKind.Amp, 2, "IntoEmpty", Blob(12288, 9)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        int dreadsAfterPlan = rig.Amps.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal));

        await rig.Svc.ExecuteAsync(plan);

        // Upload verify (96) only; no compare dread of the empty target.
        Assert.Equal(96, rig.Amps.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal)) - dreadsAfterPlan);
        // No compare read means no backup dir was ever created — nothing to clean up.
        Assert.False(Directory.Exists(rig.BackupDir));
    }

    // CRITICAL — rename on this device is a name-table-only write, so "bytes equal, name
    // differs" is reachable, and presets reference amps/IRs BY NAME: a stale name here would
    // silently orphan every restored preset that names this slot.
    [Fact]
    public async Task Execute_renames_slot_when_content_matches_but_name_differs()
    {
        var rig = MakeRig();
        var same = Blob(4096, 5);
        rig.Irs.SeedSlot(0, "OldName", same);
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "NewName", same));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        int dwritesAfterPlan = rig.Irs.CommandLog.Count(c => c.StartsWith("dwrite", StringComparison.Ordinal));

        var result = await rig.Svc.ExecuteAsync(plan);

        Assert.Equal("NewName", rig.Irs.SlotNames[0]);
        Assert.Equal(same, rig.Irs.SlotBlobs[0]);              // content untouched
        Assert.Equal(new RestoreResult(Written: 0, SkippedIdentical: 1, Cleared: 0), result);
        // Only the chunk:-1 rename — no content chunks re-staged.
        Assert.Equal(1, rig.Irs.CommandLog.Count(c => c.StartsWith("dwrite", StringComparison.Ordinal)) - dwritesAfterPlan);
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    // IMPORTANT 1 — plan-time PedalOccupied can go stale on a multi-minute restore (a
    // front-panel edit mid-run); a since-emptied slot must never be dreaded.
    [Fact]
    public async Task Execute_reverifies_occupancy_before_self_reading_a_slot_emptied_after_planning()
    {
        var rig = MakeRig();
        rig.Irs.SeedSlot(0, "Old", Blob(4096, 1));
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "New", Blob(4096, 9)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        Assert.True(plan.Actions.Single().PedalOccupied);          // true at plan time...

        rig.Irs.ClearSlot(0);                                      // ...but emptied before execute
        int dreadsBeforeExecute = rig.Irs.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal));

        var result = await rig.Svc.ExecuteAsync(plan);

        int dreads = rig.Irs.CommandLog.Count(c => c.StartsWith("dread", StringComparison.Ordinal)) - dreadsBeforeExecute;
        Assert.Equal(32, dreads);              // upload verify read-back only; no compare dread
        Assert.Equal(new RestoreResult(Written: 1, SkippedIdentical: 0, Cleared: 0), result);
        Assert.False(Directory.Exists(rig.BackupDir) &&
                     Directory.GetFiles(rig.BackupDir, "*-prerestore*").Length > 0);
    }

    // IMPORTANT 2 — a partial currentBlobs dict missing a key for an occupied Write slot must
    // still fall back to a self-read, so the archive duty is never silently dropped.
    [Fact]
    public async Task Execute_falls_back_to_self_read_when_currentBlobs_is_partial()
    {
        var rig = MakeRig();
        rig.Irs.SeedSlot(0, "Old", Blob(4096, 1));
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "New", Blob(4096, 9)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        // currentBlobs is provided (not null) but does NOT cover this occupied slot.
        var result = await rig.Svc.ExecuteAsync(plan,
            currentBlobs: new Dictionary<(SnapshotSlotKind, int), byte[]>());

        Assert.Single(Directory.GetFiles(rig.BackupDir, "ir-0-*-prerestore.irblob"));
        Assert.Equal(new RestoreResult(Written: 1, SkippedIdentical: 0, Cleared: 0), result);
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    // IMPORTANT (re-review) — the rename-on-skip decision must use the FRESH name-table read on
    // the self-read path, not the plan-time PedalName: a slot renamed out-of-band between
    // PlanAsync and this action, where the plan-time name happened to already match the
    // snapshot's, must still be corrected.
    [Fact]
    public async Task Execute_uses_fresh_name_for_rename_decision_after_out_of_band_rename()
    {
        var rig = MakeRig();
        var same = Blob(4096, 5);
        rig.Irs.SeedSlot(0, "Match", same);
        var (manifest, blobs) = Snap((SnapshotSlotKind.Ir, 0, "Match", same));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        Assert.Equal("Match", plan.Actions.Single().PedalName);      // plan-time name already matches...

        rig.Irs.SeedSlot(0, "RenamedOOB", same);                     // ...but renamed out-of-band before execute (content unchanged)

        var result = await rig.Svc.ExecuteAsync(plan);

        Assert.Equal("Match", rig.Irs.SlotNames[0]);                 // fresh-name compare caught the stale plan-time name
        Assert.Equal(new RestoreResult(Written: 0, SkippedIdentical: 1, Cleared: 0), result);
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task Cancellation_lands_between_slots_and_a_rerun_resumes_via_skip()
    {
        var rig = MakeRig();
        var (manifest, blobs) = Snap(
            (SnapshotSlotKind.Ir, 0, "IrA", Blob(4096, 1)),
            (SnapshotSlotKind.Ir, 1, "IrB", Blob(4096, 2)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        using var cts = new CancellationTokenSource();
        var seen = new List<SnapshotRestoreProgress>();
        var progress = new SyncProgress<SnapshotRestoreProgress>(p =>
        {
            seen.Add(p);
            if (p.Done == 1) cts.Cancel();                     // cancel after the first slot completes
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Svc.ExecuteAsync(plan, progress: progress, ct: cts.Token));

        Assert.Equal("IrA", rig.Irs.SlotNames[0]);             // slot 0 fully committed
        Assert.Null(rig.Irs.SlotNames[1]);                     // slot 1 never started

        // Re-run: slot 0 now matches the snapshot → skipped; only slot 1 is written.
        var plan2 = await rig.Svc.PlanAsync(manifest, blobs);
        var result = await rig.Svc.ExecuteAsync(plan2);
        Assert.Equal(new RestoreResult(Written: 1, SkippedIdentical: 1, Cleared: 0), result);
        Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task A_verify_failure_stops_the_run_and_names_the_slot()
    {
        var rig = MakeRig();
        rig.Amps.CorruptAckAtChunk = 5;                        // fake: ACK mismatch at chunk 5
        var (manifest, blobs) = Snap(
            (SnapshotSlotKind.Amp, 3, "BadAmp", Blob(12288, 9)),
            (SnapshotSlotKind.Preset, 0, "NeverReached", Blob(8192, 8)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);

        var ex = await Assert.ThrowsAsync<SnapshotRestoreException>(() => rig.Svc.ExecuteAsync(plan));

        Assert.Contains("chunk 5", ex.Message);                // the upload's own slot-naming message
        Assert.Null(rig.Presets.SlotNames[0]);                 // later actions never ran
        // Both slots were empty (no compare-read backup), so no backup dir was ever created.
        if (Directory.Exists(rig.BackupDir)) Directory.Delete(rig.BackupDir, recursive: true);
    }

    [Fact]
    public async Task Progress_counter_is_operation_wide_and_stages_run_ir_amp_preset()
    {
        var rig = MakeRig();
        var (manifest, blobs) = Snap(
            (SnapshotSlotKind.Preset, 0, "P", Blob(8192, 1)),
            (SnapshotSlotKind.Amp, 0, "A", Blob(12288, 2)),
            (SnapshotSlotKind.Ir, 0, "I", Blob(4096, 3)));
        var plan = await rig.Svc.PlanAsync(manifest, blobs);
        var seen = new List<SnapshotRestoreProgress>();

        await rig.Svc.ExecuteAsync(plan, progress: new SyncProgress<SnapshotRestoreProgress>(seen.Add));

        Assert.All(seen, p => Assert.Equal(3, p.Total));
        Assert.Equal(new[] { 1, 2, 3 }, seen.GroupBy(p => p.Done).Select(g => g.Key).Where(d => d > 0));
        Assert.Equal(new[] { SnapshotSlotKind.Ir, SnapshotSlotKind.Amp, SnapshotSlotKind.Preset },
                     seen.Select(p => p.Stage).Distinct());
        // All three slots were empty (no compare-read backup), so no backup dir was ever created.
        if (Directory.Exists(rig.BackupDir)) Directory.Delete(rig.BackupDir, recursive: true);
    }
}

/// <summary>Reports synchronously on the calling thread — Progress&lt;T&gt; posts to a
/// SynchronizationContext (thread-pool queued when none is ambient, as in xUnit tests), which races
/// with assertions made right after the awaited call returns. Mirrors AmpServiceTests' SyncProgress.</summary>
file sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
