# Restore Snapshot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Restore Snapshot (all-or-nothing, exact-mirror, byte-exact writes with skip-if-identical resume), remove Import Snapshot, and add a pre-export explainer dialog.

**Architecture:** A new `SnapshotRestoreService` in `Sonulab.Core` plans a 90-slot action list (Write/Clear/Skip) from a `.namsnap` and executes it through three `SlotBlobService` instances (a new preset `SlotBlobKind` joins amp/IR), reusing the hardware-verified staged-write discipline. `MainWindowViewModel` gains plan/execute methods (with an optional safety snapshot whose blobs feed the skip-compare), loses `ImportSnapshotAsync`, and the views swap the Import menu item for a Restore flow with a consent dialog (backup checkbox) and a cancelable progress dialog.

**Tech Stack:** .NET 10, Avalonia 12 (built-in FluentTheme only), xUnit, existing fakes (`FakeSlotBlobDevice`, `FakePresetDevice`).

**Spec:** `docs/superpowers/specs/2026-08-03-restore-snapshot-design.md` — the requirements source of truth.

## Global Constraints

- Device-write conventions are non-negotiable: staged writes keep the exact ACK-verified sequence in `SlotBlobService.UploadCoreAsync` (chunk 0 name → 1..N payload → −1 name commit, read-back verify, clear-on-verify-mismatch). Do not alter the burst logic.
- **NEVER dread an empty slot** (one timeout per chunk; prime suspect for killing a following commit). Every read of pedal content must be gated on plan-time occupancy.
- Mid-slot writes/clears run with `CancellationToken.None` — cancellation is honored **between** slots only. A `DeviceDisconnectedException` still propagates (it comes from the link, not the token).
- Restore progress `Done` is operation-wide across all stages (never resets), mirroring `SnapshotCaptureProgress`; `Total` counts Write+Clear actions.
- Execution order: **IRs → Amps → Presets** (referenced content before the presets that name it).
- Avalonia 12 + built-in FluentTheme only; NO FluentAvalonia; no hex literals in .axaml — use `Sonulab.*` tokens that neighboring elements use.
- Tests never touch real `%APPDATA%` or real `Documents` — temp paths via ctor seams everywhere.
- `IProgress` callbacks may fire on worker threads; view code marshals via `Avalonia.Threading.Dispatcher.UIThread.Post`.
- Full `dotnet test` (5 projects, ~2.5 min; Distill is the slow one) must pass before every commit. Baseline at branch start: 984 tests green.
- Commit messages end with:

```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01MDiEqxcxQPnJMQuYzJcG42
```

## Existing seams (verified against the tree — do not re-derive)

- `SlotBlobService` (src/Sonulab.Core/Services/SlotBlobService.cs): `SlotBlobKind(ListPath, Chunks, SlotBytes, Noun, BackupPrefix, BackupExtension)` with static `Amp`/`Ir`; `ListAsync`, `ReadAsync`, `DeleteAsync` (empty-name chunk −1; no-op on empty slot), `UploadAsync(int slot, byte[] payload, string name, IProgress<SlotUploadProgress>?, CancellationToken)`, private `BackupSlotAsync(index, suffix, ct)` writing `{prefix}-{index}-{yyyyMMdd-HHmmss}{suffix}{ext}` under its backup dir.
- `SnapshotArchive.Read(Stream)` → `(SnapshotManifest, IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs)`; throws `SnapshotArchiveException` with exact reasons; validates sizes and SHAs.
- `SnapshotManifest.Slots`: `SnapshotSlot(SnapshotSlotKind Kind, int Index, string Name, string Sha, SnapshotT3k? T3k)`.
- `MainWindowViewModel.ExportSnapshotAsync(path)` (line ~476): temp-file + rename atomicity; builds `SnapshotService` from `Connection.Repository` + new `AmpService`/`IrService` over `Connection.Client!` with `AppPaths.SlotBackups`; never throws.
- `ImportSnapshotAsync` (line ~551) + `MainWindow.axaml.cs ImportSnapshotFlowAsync` (line ~101) + menu item `ImportSnapshotMenuItem` (MainWindow.axaml line ~30): all to be removed.
- `ConfirmDialog.ShowAsync(owner, title, message, confirmText, cancelText)` — no checkbox support; restore needs its own dialogs.
- VM connect-plumbing test pattern: `SnapshotExportImportTests.Connected<TDevice>()` (real `DeviceSession` + `ConnectionViewModel` over a fake `ISonuLink`).
- Core test fakes: `FakeSlotBlobDevice(listPath, chunks, slotBytes)` with `SeedSlot`, `CommandLog`, `SlotNames`, `SlotBlobs`, `CommitSeen`, `CorruptAckAtChunk`; `SnapshotServiceTests` has `AmpBlob(byte)`/`IrBlob(byte)` fill helpers.
- Real 8192-byte preset fixture: `tests/Sonulab.Core.Tests/Fixtures/QuadReverbSM57.pst`.
- `IPresetUsageService.Invalidate()`; `_usageService` field in MainWindowViewModel.
- `AppPaths.BackupRoot` = `Documents\NAMager Backups`, `AppPaths.SlotBackups` = `…\Replaced Slots`.

---

### Task 1: Preset `SlotBlobKind` + restore plumbing on `SlotBlobService`

**Files:**
- Modify: `src/Sonulab.Core/Services/SlotBlobService.cs`
- Modify: `src/Sonulab.Core/Services/AmpService.cs` + `src/Sonulab.Core/Services/IrService.cs` (call-site updates only, if positional args break)
- Test: `tests/Sonulab.Core.Tests/SlotBlobServiceTests.cs` (extend; if per-front test files exist instead, put the new tests in a new `PresetBlobKindTests.cs` following their construction pattern)

**Interfaces:**
- Produces (later tasks consume verbatim):
  - `SlotBlobKind.Preset` — `new(@"root\presets", 64, 8192, "Preset", "preset", ".pst")`
  - `public Task<byte[]> ReadAndArchiveAsync(int index, string suffix = "", CancellationToken ct = default)` — the previously-private `BackupSlotAsync` made public (validated read + backup file + returns the blob). Caller must ensure the slot is occupied.
  - `UploadAsync` gains `bool skipBackup = false` BEFORE the `ct` parameter: `UploadAsync(int slot, byte[] payload, string name, IProgress<SlotUploadProgress>? progress = null, bool skipBackup = false, CancellationToken ct = default)`. When true, step 1 (the occupied-slot backup dread) is skipped — for callers who already read+archived the slot themselves. The name-list read guard stays only as part of the backup step (i.e. skipBackup skips the list read too).

- [ ] **Step 1: Write the failing tests.** Locate the existing SlotBlobService/AmpService test file(s) (Glob `tests/Sonulab.Core.Tests/*BlobService*Tests.cs` / `*AmpService*`), match their construction pattern (`new SlotBlobService(new SonuClient(fake, backgroundQuietMs: 0), kind, tempDir, msg => new InvalidOperationException(msg), paceMs: 0, settleMs: 0)` — pace/settle 0 for speed; check what existing tests pass). Add:

```csharp
[Fact]
public async Task Preset_kind_uploads_via_the_staged_sequence_and_roundtrips_a_real_pst()
{
    var dev = new FakeSlotBlobDevice(@"root\presets", 64, 8192);
    var svc = MakeService(dev, SlotBlobKind.Preset);          // this file's helper, adapted
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
```

Adapt `MakeService` to whatever the file's existing helper is (or add one). If existing upload tests call `UploadAsync(slot, payload, name, progress, ct)` POSITIONALLY with a ct in position 5, update those call sites to name the token (`ct: …`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~BlobKind|FullyQualifiedName~SlotBlobService" 2>&1 | tail -5`
Expected: FAIL — `SlotBlobKind.Preset` / `ReadAndArchiveAsync` / `skipBackup` don't exist.

- [ ] **Step 3: Implement.**

In `SlotBlobKind` add (with the existing doc comment extended to mention presets):

```csharp
    public static readonly SlotBlobKind Preset = new(@"root\presets", 64, 8192, "Preset", "preset", ".pst");
```

Rename `BackupSlotAsync` → public `ReadAndArchiveAsync` (keep the body, add the doc):

```csharp
    /// <summary>Dread the slot, save it under the backup dir, and return the blob. Public so the
    /// restore engine can use one validated read as BOTH its pre-overwrite backup and its
    /// skip-if-identical compare input. Callers must ensure the slot is OCCUPIED first — dreading
    /// an empty slot is one timeout per chunk and is the prime suspect for killing a following
    /// commit (see HwCheck upload notes).</summary>
    public async Task<byte[]> ReadAndArchiveAsync(int index, string suffix = "", CancellationToken ct = default)
```

(update the internal caller in `UploadCoreAsync`).

`UploadAsync`/`UploadCoreAsync` gain `bool skipBackup` (threaded through; default false). In `UploadCoreAsync`, wrap step 1:

```csharp
        // 1. Backup — ONLY if the name table says the slot is occupied, and only when the caller
        // hasn't already read+archived the slot itself (restore does: skipBackup avoids paying the
        // same full-slot dread twice per slot).
        if (!skipBackup)
        {
            var names = await _client.ReadListAsync(_kind.ListPath, ct);
            if (slot >= 0 && slot < names.Count && !string.IsNullOrEmpty(names[slot]))
            {
                progress?.Report(new(SlotUploadStage.BackingUp, 0, totalChunks));
                await ReadAndArchiveAsync(slot, "", ct);
            }
        }
```

Update `AmpService.UploadAmpAsync` / `IrService.UploadIrAsync` pass-throughs (name the ct argument).

- [ ] **Step 4: Run to verify pass** — same filter as Step 2, expect PASS including all pre-existing upload tests.

- [ ] **Step 5: Full suite, commit**

```bash
git add src/Sonulab.Core/Services/SlotBlobService.cs src/Sonulab.Core/Services/AmpService.cs src/Sonulab.Core/Services/IrService.cs tests/Sonulab.Core.Tests/
git commit -m "feat(restore): preset SlotBlobKind, public ReadAndArchiveAsync, skipBackup upload option"
```

---

### Task 2: `SnapshotRestoreService` — plan phase

**Files:**
- Create: `src/Sonulab.Core/Services/SnapshotRestoreService.cs`
- Test: `tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs` (new)

**Interfaces (produced, consumed verbatim by Tasks 3–6):**

```csharp
public sealed class SnapshotRestoreException(string message) : Exception(message);

public enum RestoreAction { Write, Clear }

/// <summary>One slot's planned operation. PedalOccupied is captured at plan time so execute
/// never dreads an empty slot.</summary>
public sealed record RestoreSlotAction(
    SnapshotSlotKind Kind, int Index, string Name, RestoreAction Action, bool PedalOccupied);

public sealed record SnapshotRestorePlan(
    SnapshotManifest Manifest,
    IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs,
    IReadOnlyList<RestoreSlotAction> Actions)
{
    public int WriteCount => Actions.Count(a => a.Action == RestoreAction.Write);
    public int ClearCount => Actions.Count(a => a.Action == RestoreAction.Clear);
}

public enum RestoreSlotPhase { Comparing, Writing, Clearing }

/// <summary>Done is operation-wide across all stages (never resets), like
/// SnapshotCaptureProgress; Total = WriteCount + ClearCount.</summary>
public sealed record SnapshotRestoreProgress(
    SnapshotSlotKind Stage, RestoreSlotPhase Phase, int Done, int Total, string SlotName);

public sealed record RestoreResult(int Written, int SkippedIdentical, int Cleared);

public sealed class SnapshotRestoreService(
    SlotBlobService presets, SlotBlobService amps, SlotBlobService irs)
{
    public async Task<SnapshotRestorePlan> PlanAsync(
        SnapshotManifest manifest,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs,
        CancellationToken ct = default);
    // ExecuteAsync arrives in Task 3.
}
```

**Plan semantics:** for each of the three kinds (order **Ir, Amp, Preset** — the execution order), read the pedal's name list via the matching service's `ListAsync`, and for each slot index 0..29 emit: snapshot-has-content → `Write` (Name = the snapshot slot's name); snapshot empty + pedal occupied → `Clear` (Name = the pedal's current name, purely informational); both empty → no action. Actions are in execution order: all IR actions (index-ascending), then amps, then presets.

- [ ] **Step 1: Write the failing tests** (`SnapshotRestoreServiceTests.cs`). Test scaffolding builds a manifest+blobs dict directly (no archive file needed):

```csharp
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
```

NOTE: check `SnapshotManifest`'s actual constructor signature and `SnapshotArchive.ShaOf`'s accessibility (SnapshotServiceTests uses them — mirror what it does). Adjust the helper, not the assertions.

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SnapshotRestore" 2>&1 | tail -5` — compile errors expected.

- [ ] **Step 3: Implement** `SnapshotRestoreService.cs` with the records/enums above and:

```csharp
    public async Task<SnapshotRestorePlan> PlanAsync(
        SnapshotManifest manifest,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs,
        CancellationToken ct = default)
    {
        var actions = new List<RestoreSlotAction>();
        foreach (var (kind, svc) in ExecutionOrder())
        {
            ct.ThrowIfCancellationRequested();
            var pedal = await svc.ListAsync(ct);
            var inSnapshot = manifest.Slots.Where(s => s.Kind == kind)
                                           .ToDictionary(s => s.Index);
            for (int i = 0; i < SlotBlobService.SlotCount; i++)
            {
                bool occupied = !string.IsNullOrEmpty(pedal[i].Name);
                if (inSnapshot.TryGetValue(i, out var snap))
                    actions.Add(new RestoreSlotAction(kind, i, snap.Name, RestoreAction.Write, occupied));
                else if (occupied)
                    actions.Add(new RestoreSlotAction(kind, i, pedal[i].Name, RestoreAction.Clear, true));
            }
        }
        return new SnapshotRestorePlan(manifest, blobs, actions);
    }

    /// <summary>IRs → Amps → Presets: referenced content lands before the presets that name it,
    /// so an interrupted restore never leaves NEW presets pointing at not-yet-restored names.</summary>
    private IEnumerable<(SnapshotSlotKind Kind, SlotBlobService Svc)> ExecutionOrder()
    {
        yield return (SnapshotSlotKind.Ir, irs);
        yield return (SnapshotSlotKind.Amp, amps);
        yield return (SnapshotSlotKind.Preset, presets);
    }
```

Class doc comment: state the exact-mirror contract, the spec reference, and that the service never opens dialogs — consent is the caller's job.

- [ ] **Step 4: Run to verify pass.**

- [ ] **Step 5: Full suite, commit**

```bash
git add src/Sonulab.Core/Services/SnapshotRestoreService.cs tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs
git commit -m "feat(restore): SnapshotRestoreService plan phase — 90-slot write/clear action list"
```

---

### Task 3: `SnapshotRestoreService.ExecuteAsync` — mirror semantics

**Files:**
- Modify: `src/Sonulab.Core/Services/SnapshotRestoreService.cs`
- Test: `tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs` (extend)

**Interfaces (produced):**

```csharp
    public async Task<RestoreResult> ExecuteAsync(
        SnapshotRestorePlan plan,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]>? currentBlobs = null,
        IProgress<SnapshotRestoreProgress>? progress = null,
        CancellationToken ct = default);
```

**Semantics per action (in plan order):**
- `ct.ThrowIfCancellationRequested()` at the top of each action — cancellation lands BETWEEN slots.
- `Write`: obtain the pedal's current blob for compare: from `currentBlobs` if present (the safety snapshot already read it — no device I/O, no per-slot file), else — ONLY if `PedalOccupied` — report `Comparing` and `await svc.ReadAndArchiveAsync(index, "-prerestore", ct)`. If a current blob exists and `SequenceEqual`s the snapshot blob → count `SkippedIdentical` (no write). Else report `Writing` and `await svc.UploadAsync(index, blob, name, progress: null, skipBackup: true, ct: CancellationToken.None)` → count `Written`. `skipBackup: true` in BOTH branches of how current was obtained — the archive/backup duty is already discharged (per-slot file or safety snapshot).
- `Clear`: report `Clearing`; if `currentBlobs` lacks the slot, `await svc.ReadAndArchiveAsync(index, "-prerestore", ct)` (restore archives before clearing — unlike user-initiated deletes — because the mirror rule destroys content the user did not individually choose to delete); then `await svc.DeleteAsync(index, CancellationToken.None)` → count `Cleared`.
- Report progress with the operation-wide `done` counter after each action completes (all three outcomes increment it); also report the phase-change events before device I/O so the UI shows what is happening during a multi-second operation.

- [ ] **Step 1: Write the failing tests** (extend the rig; add to `SnapshotRestoreServiceTests.cs`):

```csharp
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
        Assert.Equal("", rig.Presets.SlotNames[4]);              // cleared
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
        Directory.Delete(rig.BackupDir, recursive: true);
    }
```

- [ ] **Step 2: Run to verify failure** (no `ExecuteAsync`).

- [ ] **Step 3: Implement** `ExecuteAsync` per the semantics block:

```csharp
    public async Task<RestoreResult> ExecuteAsync(
        SnapshotRestorePlan plan,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]>? currentBlobs = null,
        IProgress<SnapshotRestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        int total = plan.Actions.Count, done = 0;
        int written = 0, skipped = 0, cleared = 0;
        foreach (var a in plan.Actions)
        {
            // Cancellation lands BETWEEN slots: an abandoned staged burst mid-slot is the one
            // shape the write discipline can't make safe, so each action runs to completion
            // (device I/O below takes CancellationToken.None; link death still throws).
            ct.ThrowIfCancellationRequested();
            var svc = ServiceFor(a.Kind);
            if (a.Action == RestoreAction.Write)
            {
                var snapBlob = plan.Blobs[(a.Kind, a.Index)];
                byte[]? current = null;
                if (currentBlobs is not null) currentBlobs.TryGetValue((a.Kind, a.Index), out current);
                else if (a.PedalOccupied)
                {
                    progress?.Report(new(a.Kind, RestoreSlotPhase.Comparing, done, total, a.Name));
                    current = await svc.ReadAndArchiveAsync(a.Index, "-prerestore", CancellationToken.None);
                }
                if (current is not null && current.AsSpan().SequenceEqual(snapBlob))
                {
                    skipped++;
                }
                else
                {
                    progress?.Report(new(a.Kind, RestoreSlotPhase.Writing, done, total, a.Name));
                    await svc.UploadAsync(a.Index, snapBlob, a.Name,
                                          progress: null, skipBackup: true, ct: CancellationToken.None);
                    written++;
                }
            }
            else
            {
                progress?.Report(new(a.Kind, RestoreSlotPhase.Clearing, done, total, a.Name));
                if (currentBlobs is null || !currentBlobs.ContainsKey((a.Kind, a.Index)))
                    await svc.ReadAndArchiveAsync(a.Index, "-prerestore", CancellationToken.None);
                await svc.DeleteAsync(a.Index, CancellationToken.None);
                cleared++;
            }
            done++;
            progress?.Report(new(a.Kind,
                a.Action == RestoreAction.Clear ? RestoreSlotPhase.Clearing : RestoreSlotPhase.Writing,
                done, total, a.Name));
        }
        return new RestoreResult(written, skipped, cleared);
    }

    private SlotBlobService ServiceFor(SnapshotSlotKind kind) => kind switch
    {
        SnapshotSlotKind.Preset => presets,
        SnapshotSlotKind.Amp => amps,
        _ => irs,
    };
```

(If the compare read used `ct` it would cancel mid-action — keep every device call inside an action on `CancellationToken.None`, per the Global Constraint.)

- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Full suite, commit**

```bash
git add src/Sonulab.Core/Services/SnapshotRestoreService.cs tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs
git commit -m "feat(restore): ExecuteAsync — exact-mirror writes/clears with skip-if-identical"
```

---

### Task 4: ExecuteAsync robustness — cancellation, failure identity, progress contract

**Files:**
- Modify: `src/Sonulab.Core/Services/SnapshotRestoreService.cs` (only if a test exposes a gap — the Task 3 code may already satisfy several of these)
- Test: `tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs` (extend)

- [ ] **Step 1: Write the failing/pinning tests:**

```csharp
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
        Assert.Equal("", rig.Irs.SlotNames[1]);                // slot 1 never started

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
        Assert.Equal("", rig.Presets.SlotNames[0]);            // later actions never ran
        Directory.Delete(rig.BackupDir, recursive: true);
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
        Directory.Delete(rig.BackupDir, recursive: true);
    }
```

Notes for the implementer: `SyncProgress<T>` — copy the `file sealed class` from `SnapshotServiceTests.cs` (xUnit `file` classes are per-file; duplicate it here). `CorruptAckAtChunk` — verify the fake's actual member name/behavior in `FakeSlotBlobDevice.cs` and adapt (the intent: make the amp upload fail with the service's ACK-mismatch exception). If the fake's corruption fires differently (e.g. on verify), assert on whatever slot-identifying text the real exception carries — the REQUIREMENT is that the thrown message identifies the failing slot/chunk and that subsequent actions never run.

- [ ] **Step 2: Run** — some may already pass against Task 3's implementation (fine — they are pinning tests); any that fail reveal real gaps to fix minimally.

- [ ] **Step 3: Fix gaps if any** (e.g. if the progress `Done`-sequence assertion exposes double-counting). Do not restructure working code for style.

- [ ] **Step 4: Full suite, commit**

```bash
git add src/Sonulab.Core/Services/SnapshotRestoreService.cs tests/Sonulab.Core.Tests/SnapshotRestoreServiceTests.cs
git commit -m "test(restore): pin cancellation, failure-identity, and progress contracts"
```

---

### Task 5: MainWindowViewModel — plan/execute methods, safety snapshot, Import removal

**Files:**
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Namager.App.Tests/SnapshotExportImportTests.cs` → grow into the restore VM tests (rename file to `SnapshotVmTests.cs` ONLY if the repo's test files are named by class; otherwise keep the name and rename the class comment)

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces (Task 6's views call these exact members):

```csharp
    /// <summary>Reads + validates the .namsnap at <paramref name="path"/> and plans the restore
    /// against the connected pedal. Read-only. Throws SnapshotArchiveException (bad file) or
    /// InvalidOperationException (no connection) — the view surfaces both.</summary>
    public async Task<SnapshotRestorePlan> PlanRestoreAsync(string path);

    /// <summary>Executes a restore plan. When <paramref name="backupFirst"/> is true, first
    /// captures a safety .namsnap of the pedal's current state to Documents\NAMager Backups and
    /// feeds its blobs to the skip-compare. Returns the result plus the safety file's path (null
    /// when skipped). Throws on failure — the view shows the reason; completed slots stay
    /// verified and a re-run resumes via skip-if-identical.</summary>
    public async Task<(RestoreResult Result, string? SafetyPath)> ExecuteRestoreAsync(
        SnapshotRestorePlan plan, bool backupFirst,
        IProgress<SnapshotRestoreProgress>? progress = null, CancellationToken ct = default);
```

- `ImportSnapshotAsync` is DELETED.

- [ ] **Step 1: Write the failing tests** (extend `SnapshotExportImportTests.cs`, reusing `Connected<TDevice>()` and `IdentifyingEmptyDevice`):

```csharp
    [Fact]
    public async Task PlanRestoreAsync_plans_against_the_connected_pedal()
    {
        var (connVm, _) = Connected(new IdentifyingEmptyDevice());
        var vm = new MainWindowViewModel(settingsPath: null, irIndexPath: TempJson()) { Connection = connVm };
        var snapPath = WriteSnapshotFile(                       // helper below
            (SnapshotSlotKind.Ir, 0, "IrZero", FilledBlob(4096, 7)));
        try
        {
            var plan = await vm.PlanRestoreAsync(snapPath);
            Assert.Equal(1, plan.WriteCount);
            Assert.Equal(0, plan.ClearCount);                   // pedal is empty
        }
        finally { File.Delete(snapPath); }
    }

    [Fact]
    public async Task PlanRestoreAsync_throws_without_a_connection()
    {
        var vm = new MainWindowViewModel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.PlanRestoreAsync("x.namsnap"));
    }

    [Fact]
    public async Task PlanRestoreAsync_propagates_archive_validation_errors()
    {
        var (connVm, _) = Connected(new IdentifyingEmptyDevice());
        var vm = new MainWindowViewModel(settingsPath: null, irIndexPath: TempJson()) { Connection = connVm };
        var bad = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.namsnap");
        File.WriteAllText(bad, "not a zip");
        try
        {
            await Assert.ThrowsAsync<SnapshotArchiveException>(() => vm.PlanRestoreAsync(bad));
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void ImportSnapshotAsync_is_gone()
    {
        Assert.Null(typeof(MainWindowViewModel).GetMethod("ImportSnapshotAsync"));
    }
```

Helpers to add to the test class:

```csharp
    private static string TempJson() =>
        Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");

    private static byte[] FilledBlob(int size, byte fill) { var b = new byte[size]; Array.Fill(b, fill); return b; }

    private static string WriteSnapshotFile(params (SnapshotSlotKind Kind, int Index, string Name, byte[] Blob)[] slots)
    {
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        var manifest = new SnapshotManifest(SnapshotManifest.CurrentSchema, "2026-08-03T00:00:00Z",
            "test", new SnapshotDevice("StompStation", "2.5.1"),
            slots.Select(s => new SnapshotSlot(s.Kind, s.Index, s.Name,
                SnapshotArchive.ShaOf(s.Blob), null)).ToList());
        using var fs = File.Create(path);
        SnapshotArchive.Write(fs, manifest, slots.ToDictionary(s => (s.Kind, s.Index), s => s.Blob));
        return path;
    }
```

(Verify `SnapshotArchive.Write`'s exact signature and `ShaOf` accessibility against the source; mirror `SnapshotServiceTests`/`SnapshotArchiveTests` usage.)

**Why no full ExecuteRestoreAsync happy-path VM test:** executing a restore against `IdentifyingEmptyDevice` would need a fake implementing staged writes for all three lists on ONE link — none exists, and building one is out of scope; the execute path is fully covered at the service layer (Tasks 3–4) against the real per-list fakes. The VM method is composition glue, covered by: the plan tests above, the safety-abort test below, and compile-time.

```csharp
    [Fact]
    public async Task ExecuteRestoreAsync_aborts_before_writing_if_the_safety_backup_fails()
    {
        // FailingAmpListDevice kills the amp-list read INSIDE the safety capture — restore must
        // abort with the failure and never reach a device write.
        var (connVm, dev) = Connected(new FailingAmpListDevice());
        var vm = new MainWindowViewModel(settingsPath: null, irIndexPath: TempJson()) { Connection = connVm };
        var snapPath = WriteSnapshotFile((SnapshotSlotKind.Ir, 0, "IrZero", FilledBlob(4096, 7)));
        try
        {
            SnapshotRestorePlan plan;
            try { plan = await vm.PlanRestoreAsync(snapPath); }
            catch (IOException) { return; }   // if the plan itself trips the fault first, the guarantee holds trivially — but assert the write never happened below either way
            await Assert.ThrowsAnyAsync<Exception>(() => vm.ExecuteRestoreAsync(plan, backupFirst: true));
            Assert.DoesNotContain(dev.CommandLog ?? Enumerable.Empty<string>(),
                c => c.StartsWith("dwrite", StringComparison.Ordinal));
        }
        finally { File.Delete(snapPath); }
    }
```

NOTE: `FakePresetDevice` may not expose `CommandLog` — check; if not, subclass `FailingAmpListDevice` in this file to record sent commands and assert on that. The REQUIREMENT: a safety-capture failure aborts before any `dwrite` reaches the device.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.** In `MainWindowViewModel`:

(a) Extract the throwing capture core from `ExportSnapshotAsync` so restore can reuse it and abort on failure (export's swallow-to-Status behavior is preserved by keeping its existing try/catch around the new method):

```csharp
    /// <summary>The throwing capture core shared by Export Snapshot and the pre-restore safety
    /// backup: captures every occupied slot into a temp file beside <paramref name="path"/> and
    /// renames onto it only on full success (SnapshotArchive.Write is not atomic on its stream).
    /// Throws on any failure with the temp file cleaned up; the destination is never left
    /// half-written.</summary>
    private async Task CaptureSnapshotToFileAsync(string path, Action<string>? report, CancellationToken ct = default)
```

— body: the current `ExportSnapshotAsync` inner logic (temp path, `SnapshotService` construction, `CaptureAsync` with the existing progress switch routed through `report`, `File.Move`), minus the Status calls. `ExportSnapshotAsync` becomes a thin wrapper: guard connection → `using var op = Status.BeginOperation(...)` → `try { await CaptureSnapshotToFileAsync(path, op.Report); Status.Success(...); } catch { cleanup already done; Status.Failure(...); }`. Preserve the existing comments (atomicity, CancellationToken.None rationale) in the moved code.

(b) `PlanRestoreAsync`:

```csharp
    public async Task<SnapshotRestorePlan> PlanRestoreAsync(string path)
    {
        if (Connection.Client is null)
            throw new InvalidOperationException("Connect to the pedal first.");
        SnapshotManifest manifest;
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs;
        await using (var file = File.OpenRead(path))
            (manifest, blobs) = SnapshotArchive.Read(file);
        return await BuildRestoreService().PlanAsync(manifest, blobs);
    }

    private SnapshotRestoreService BuildRestoreService()
    {
        var backups = AppPaths.SlotBackups;
        SlotBlobService S(SlotBlobKind kind) => new(Connection.Client!, kind, backups,
            msg => new SnapshotRestoreException(msg));
        return new SnapshotRestoreService(S(SlotBlobKind.Preset), S(SlotBlobKind.Amp), S(SlotBlobKind.Ir));
    }
```

(c) `ExecuteRestoreAsync`:

```csharp
    public async Task<(RestoreResult Result, string? SafetyPath)> ExecuteRestoreAsync(
        SnapshotRestorePlan plan, bool backupFirst,
        IProgress<SnapshotRestoreProgress>? progress = null, CancellationToken ct = default)
    {
        FileOperationInFlight = true;
        try
        {
            using var op = Status.BeginOperation("Restoring snapshot…");
            string? safetyPath = null;
            IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]>? currentBlobs = null;
            if (backupFirst)
            {
                safetyPath = Path.Combine(AppPaths.BackupRoot,
                    $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.namsnap");
                // A failed safety backup ABORTS the restore — the one thing this feature must
                // never do is destroy the only copy of the pedal's state while failing to save it.
                await CaptureSnapshotToFileAsync(safetyPath, op.Report, ct);
                await using var f = File.OpenRead(safetyPath);
                currentBlobs = SnapshotArchive.Read(f).Blobs;   // feeds skip-compare: no re-reads
            }

            var result = await BuildRestoreService().ExecuteAsync(plan, currentBlobs,
                new Progress<SnapshotRestoreProgress>(p => op.Report(FormatRestoreProgress(p))), ct);

            // The pedal's content just changed wholesale under the usage map and the amp detail
            // cache — invalidate so the background scan rebuilds from the restored truth.
            _usageService?.Invalidate();

            RecordRestoredIrIdentities(plan);
            Status.Success($"Restore complete — {result.Written} written, " +
                           $"{result.SkippedIdentical} already identical, {result.Cleared} cleared.");
            return (result, safetyPath);
        }
        finally { FileOperationInFlight = false; }
    }

    internal static string FormatRestoreProgress(SnapshotRestoreProgress p) => p.Phase switch
    {
        RestoreSlotPhase.Clearing => $"Clearing slot not in snapshot — #{p.Done + 1} of {p.Total} total",
        RestoreSlotPhase.Comparing => $"Checking '{p.SlotName}' — #{p.Done + 1} of {p.Total} total files",
        _ => p.Stage switch
        {
            SnapshotSlotKind.Ir => $"Restoring IR files to pedal first — #{Math.Min(p.Done + 1, p.Total)} of {p.Total} total files",
            SnapshotSlotKind.Amp => $"Restoring Amp files to pedal next — #{Math.Min(p.Done + 1, p.Total)} of {p.Total} total files",
            _ => $"Restoring Preset files to pedal last — #{Math.Min(p.Done + 1, p.Total)} of {p.Total} total files",
        },
    };

    /// <summary>Restore replaces Import's one useful behavior: any restored IR that carries a
    /// Tone3000 identity in the manifest is recorded in the local index, keyed by blob content.</summary>
    private void RecordRestoredIrIdentities(SnapshotRestorePlan plan)
    {
        var index = IrIndex.Load(_irIndexPath);
        int learned = 0;
        foreach (var slot in plan.Manifest.Slots.Where(s => s.Kind == SnapshotSlotKind.Ir && s.T3k is not null))
        {
            var blob = plan.Blobs[(SnapshotSlotKind.Ir, slot.Index)];
            index = index.Record(new IrIndexEntry(IrIndex.ShaOf(blob), slot.T3k!.ToneId, slot.T3k.ModelId, Title: null));
            learned++;
        }
        if (learned > 0) index.Save(_irIndexPath);
    }
```

(d) DELETE `ImportSnapshotAsync` and its doc comment.

- [ ] **Step 4: Run to verify pass** — `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~Snapshot" 2>&1 | tail -3`. The old import tests (if any assert `ImportSnapshotAsync` behavior) are deleted in the same commit — check the file and remove them.

- [ ] **Step 5: Full suite, commit**

```bash
git add src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/SnapshotExportImportTests.cs
git commit -m "feat(restore): VM plan/execute with optional safety snapshot; remove ImportSnapshotAsync"
```

---

### Task 6: Views — menu swap, export explainer, restore consent + progress dialogs

**Files:**
- Modify: `src/Namager.App/Views/MainWindow.axaml` (menu item)
- Modify: `src/Namager.App/Views/MainWindow.axaml.cs` (flows)
- Create: `src/Namager.App/Views/RestoreConfirmDialog.axaml` + `.axaml.cs`
- Create: `src/Namager.App/Views/RestoreProgressDialog.axaml` + `.axaml.cs`
- Test: compile + existing suite only (repo has no axaml-code-behind unit tests; behavior lives in the VM, already tested)

- [ ] **Step 1: Menu.** In `MainWindow.axaml` replace the Import item:

```xml
        <MenuItem x:Name="RestoreSnapshotMenuItem" Header="_Restore Snapshot…"/>
```

- [ ] **Step 2: Export explainer.** In `MainWindow.axaml.cs`, at the top of the export flow (before its save-file picker):

```csharp
        var proceed = await ConfirmDialog.ShowAsync(this, "Export Snapshot",
            "Exports all the presets, amps and IR files so you can restore them to this pedal " +
            "or another pedal at a later date.\n\nReading the pedal takes about 3 minutes.",
            confirmText: "Continue", cancelText: "Cancel");
        if (!proceed) return;
```

- [ ] **Step 3: `RestoreConfirmDialog`.** Follow `ConfirmDialog.axaml`'s structure/styles exactly (read it first; reuse its spacing and button row). Content: a message `TextBlock` (`x:Name="MessageText"`, TextWrapping="Wrap"), a `CheckBox` (`x:Name="BackupCheck"`, Content="Back up current pedal state first (about 3 minutes)", IsChecked="True"), buttons Restore (`x:Name="RestoreButton"`) / Cancel. Code-behind:

```csharp
public partial class RestoreConfirmDialog : Window
{
    private bool _confirmed;

    public RestoreConfirmDialog() => InitializeComponent();

    /// <summary>Returns (confirmed, backupFirst). The message is pre-formatted by the caller.</summary>
    public static async Task<(bool Confirmed, bool BackupFirst)> ShowAsync(Window owner, string message)
    {
        var dlg = new RestoreConfirmDialog();
        dlg.MessageText.Text = message;
        await dlg.ShowDialog(owner);
        return (dlg._confirmed, dlg.BackupCheck.IsChecked == true);
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e) { _confirmed = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 4: `RestoreProgressDialog`.** Modal window: message `TextBlock` (`x:Name="ProgressText"`), `ProgressBar` (`x:Name="Bar"`, Minimum 0), Cancel button. No close box dismissal mid-run (set `CanResize="False"`; intercept `Closing` to route through the same cancel path while running). Code-behind exposes:

```csharp
public partial class RestoreProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    public CancellationToken Token => _cts.Token;

    public RestoreProgressDialog() { InitializeComponent(); Closing += (_, e) => { if (!_done) { e.Cancel = true; RequestCancel(); } }; }

    private bool _done;

    public void Report(string message, int done, int total) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        { ProgressText.Text = message; Bar.Maximum = Math.Max(1, total); Bar.Value = done; });

    /// <summary>Cancellation is honored between slots — the in-flight slot completes first, so
    /// the button flips to "Canceling…" rather than closing immediately.</summary>
    private void RequestCancel()
    { _cts.Cancel(); CancelButton.Content = "Canceling…"; CancelButton.IsEnabled = false; }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => RequestCancel();

    public void Finish() { _done = true; Close(); }
}
```

- [ ] **Step 5: Restore flow** in `MainWindow.axaml.cs` (replaces `ImportSnapshotFlowAsync`; keep the `SnapshotArchiveException` → exact-reason dialog behavior):

```csharp
    /// <summary>File ▸ Restore Snapshot… — pick a .namsnap, plan it against the connected pedal,
    /// get explicit consent (with the safety-backup checkbox), then execute with a cancelable
    /// progress dialog. Restore is the app's most destructive operation: the consent dialog is
    /// the device-write gate, and every overwritten/cleared slot is archived first (safety
    /// snapshot and/or per-slot -prerestore files).</summary>
    private async System.Threading.Tasks.Task RestoreSnapshotFlowAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Restore Snapshot",
                AllowMultiple = false,
                FileTypeFilter = new[] { NamsnapFileType },
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;

            var plan = await vm.PlanRestoreAsync(path);
            var m = plan.Manifest;
            var mismatch = vm.Connection.FirmwareVersion is { } fw && fw != m.Device.Fw
                ? $"\n\nNOTE: the snapshot was taken on firmware {m.Device.Fw}; this pedal runs {fw}."
                : "";
            var (confirmed, backupFirst) = await RestoreConfirmDialog.ShowAsync(this,
                $"Snapshot of a {m.Device.Model} (firmware {m.Device.Fw}), captured {m.CreatedUtc}.\n\n" +
                $"Restoring will make this pedal EXACTLY match the snapshot: " +
                $"{plan.WriteCount} file{(plan.WriteCount == 1 ? "" : "s")} will be written and " +
                $"{plan.ClearCount} slot{(plan.ClearCount == 1 ? "" : "s")} not in the snapshot will be cleared. " +
                $"This takes roughly {(plan.WriteCount * 8 + 59) / 60 + 1} minutes; slots already " +
                "identical to the snapshot are skipped, so re-running after an interruption is fast." +
                mismatch);
            if (!confirmed) return;

            var dlg = new RestoreProgressDialog();
            var run = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    return await vm.ExecuteRestoreAsync(plan, backupFirst,
                        new Progress<SnapshotRestoreProgress>(p => dlg.Report(
                            MainWindowViewModel.FormatRestoreProgress(p), p.Done, p.Total)),
                        dlg.Token);
                }
                finally { Avalonia.Threading.Dispatcher.UIThread.Post(dlg.Finish); }
            });
            await dlg.ShowDialog(this);
            try
            {
                var (result, safetyPath) = await run;
                await ConfirmDialog.ShowAsync(this, "Restore complete",
                    $"{result.Written} file{(result.Written == 1 ? "" : "s")} written, " +
                    $"{result.SkippedIdentical} already identical, {result.Cleared} cleared." +
                    (safetyPath is null ? "" : $"\n\nSafety backup: {safetyPath}"),
                    confirmText: null, cancelText: "Close");
            }
            catch (OperationCanceledException)
            {
                await ConfirmDialog.ShowAsync(this, "Restore canceled",
                    "Restore stopped between files — every file already written was verified. " +
                    "Run Restore Snapshot again with the same file to finish; already-restored " +
                    "files are skipped automatically.",
                    confirmText: null, cancelText: "Close");
            }
        }
        catch (SnapshotArchiveException ex)
        {
            vm.Status.Failure($"Restore failed: {ex.Message}");
            await ConfirmDialog.ShowAsync(this, "Restore failed",
                $"This file isn't a usable .namsnap snapshot:\n\n{ex.Message}",
                confirmText: null, cancelText: "Close");
        }
        catch (Exception ex)
        {
            vm.Status.Failure($"Restore failed: {ex.Message}");
            await ConfirmDialog.ShowAsync(this, "Restore failed",
                $"{ex.Message}\n\nEvery file already written was verified. Run Restore Snapshot " +
                "again with the same file to resume; already-restored files are skipped.",
                confirmText: null, cancelText: "Close");
        }
    }
```

Wire `RestoreSnapshotMenuItem.Click += async (_, _) => await RestoreSnapshotFlowAsync();` where the import wiring was; delete `ImportSnapshotFlowAsync`. Gate the menu item's `IsEnabled` the same way other write actions are gated if the existing menu does so (check how Export/other items bind enablement; mirror it, additionally requiring `Connection.WritesAllowed`).

- [ ] **Step 6: Build + full suite**

Run: `dotnet build src/Namager.App 2>&1 | tail -3` then `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`
Expected: clean build, all green.

- [ ] **Step 7: Commit**

```bash
git add src/Namager.App/Views/ src/Namager.App/ViewModels/
git commit -m "feat(restore): Restore Snapshot flow — consent + progress dialogs; export explainer; Import removed"
```

---

### Task 7: Docs — hardware checklist, STATUS

**Files:**
- Create: `docs/HARDWARE-VALIDATION-restore.md`
- Modify: `docs/STATUS.md`

- [ ] **Step 1: `docs/HARDWARE-VALIDATION-restore.md`** (all items involve DEVICE WRITES — for Ed):

```markdown
# Hardware validation — Restore Snapshot

Restore writes byte-exact slot content via the staged dwrite sequence. Presets use this path
IN-APP FOR THE FIRST TIME here (HwCheck --preset-dwrite-probe proved the sequence on 2026-07-04;
PROTOCOL.md VERDICT). Run top to bottom; stop on any failure.

- [ ] Baseline: Export Snapshot of the current pedal (keep this file — it is the day's backup).
- [ ] Single-preset probe first: restore a snapshot onto the SAME pedal unchanged — every slot
      should report "already identical" (skip), zero writes. Proves the compare path end to end.
- [ ] Change ONE preset's amp selection on the pedal, re-run the same restore: exactly 1 file
      written (that preset), everything else skipped; preset sounds/looks correct afterward.
- [ ] ACTIVE-SLOT probe: make the pedal's live preset one that the restore will overwrite;
      re-run a restore that writes it. Watch for audio glitches, wrong live state, or a wedged
      device. If the pedal misbehaves, STOP — mitigation (select another preset before writing
      the active slot) is a known follow-up; note findings here.
- [ ] Full mirror restore onto this pedal from a snapshot with deliberate differences
      (one renamed preset, one deleted amp, one extra IR): writes+clears match the confirm
      dialog's counts; pedal content matches the snapshot afterward (spot-check via VoidX or
      HwCheck --list-amps / --list-irs / no-arg preset list).
- [ ] Cancel mid-restore (during the amp stage): dialog says canceled-between-files; re-run
      resumes and finishes with the early slots skipped.
- [ ] Safety backup: confirm pre-restore-<timestamp>.namsnap lands in Documents\NAMager Backups
      and re-restoring FROM it returns the pedal to its pre-restore state.
- [ ] Cross-pedal (if second unit available): restore pedal A's snapshot onto pedal B; firmware
      mismatch note appears if applicable; pedal B matches A's content.
- [ ] Timing note: record full-restore wall time here for the docs: ______ min for __ files.
```

- [ ] **Step 2: `docs/STATUS.md`** — update the snapshot line (it currently doesn't mention restore): add after the warm-start bullet:

```markdown
- Restore Snapshot SHIPPED (exact-mirror, byte-exact staged writes incl. presets, skip-if-identical
  resume, safety backup checkbox); Import Snapshot removed. On-device checks pending in
  `docs/HARDWARE-VALIDATION-restore.md` — the active-slot-write probe is the open risk.
```

- [ ] **Step 3: Commit**

```bash
git add docs/HARDWARE-VALIDATION-restore.md docs/STATUS.md
git commit -m "docs(restore): hardware validation checklist + STATUS"
```

---

## Verification (whole feature)

1. `dotnet test` — all 5 projects green (baseline 984 + new tests).
2. `dotnet build` — no new warnings in touched files.
3. Read-only smoke on the bench pedal: `dotnet run --project tools/HwCheck` (connect + preset list). NO write modes without Ed.
4. On-device: `docs/HARDWARE-VALIDATION-restore.md` — requires Ed at the pedal (every item writes).
5. Manual UI walkthrough (Ed): export explainer appears; Restore menu replaces Import; consent dialog counts match; cancel path message correct.

## Risks

- **First in-app byte-exact preset write** — sequence is PROTOCOL-verified and shares the amp/IR code path, but the hardware checklist's single-preset probe runs before any full restore.
- **Active-slot write behavior unknown** — explicitly probed by the checklist; mitigation identified (pre-select another preset) but NOT built in v1.
- **`UploadAsync` signature change** (`skipBackup` before `ct`) — Task 1 must fix any positional `ct` call sites; the full suite catches stragglers at compile time.
- **Restore duration** (~10–15 min full) — no pause; cancel-and-rerun is the escape hatch, stated in the cancel dialog.
