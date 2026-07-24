# dswap Reorder Engine + Targeted Usage-Map Maintenance (Core, Cycle 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild preset reorder on the atomic firmware `dswap` verb and replace the full usage-map rescan on reorder/rename/delete with cheap in-memory transforms.

**Architecture:** Add a block-agnostic `dswap` primitive (command → client → repository). Rewrite `ReorderService` so every move is a sequence of atomic adjacent swaps (deleting the select+save temp-name machinery). Add pure `PresetUsageMap` transforms and targeted `IPresetUsageService` notifications, and have `PresetListViewModel` call them on verified reorder/rename/delete success instead of `Invalidate()`.

**Tech Stack:** C# / .NET 10, xUnit, Avalonia MVVM (CommunityToolkit.Mvvm). Offline tests run against `FakePresetDevice` (Sonulab.Core.Tests) and `FakePresetUsageService` (Namager.App.Tests).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-24-dswap-reorder-core-design.md`. Source: `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md` #1 + #3.
- **Never send a non-numeric `dread`/`dswap` index** — firmware `abort()`s and the ESP32 reboots (PROTOCOL.md hazard). All indices are validated ints in `[0, DeviceRepository.SlotCount)` before any device I/O.
- **Cycle 1 is presets only.** The `dswap` primitive is written block-agnostic (takes a `path`) so Cycle 2 can reuse it for `root\amp` / `root\ir`, but only the preset path is wired here. No amp/IR reorder UI.
- **Duplicate and param-edits keep full `Invalidate()`** in Cycle 1 (single-slot targeted rescan is a deferred follow-up). Only reorder / rename / delete get targeted map maintenance.
- Full test suite must stay green (currently **648 tests**). Run `dotnet test` before every commit.
- Commit message trailers (per repo convention): end with
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` and the `Claude-Session:` line. (Trailers omitted from the sample commit commands below for brevity — include them.)

---

## Task 0 (GATE): On-device dswap probe — active-slot behavior + amp/IR paths

**This is a hardware-validation gate Ed runs, enabled by Task 1's code.** It does not block the
offline-testable engine (Tasks 2–6 are provably correct against `FakePresetDevice`), but its verdict
must be recorded in `PROTOCOL.md` **before merge**, and it decides whether a live-preset re-select
refinement is needed (a deferred follow-up if so — see Task 3 note).

What Ed runs after Task 1 lands (with a full-bank backup, per PROTOCOL.md discipline):
- `dotnet run --project tools/HwCheck -- --dswap-probe --path root\presets --active` — swap a slot that is the **currently selected** preset; confirm whether the live/working preset is disturbed.
- `dotnet run --project tools/HwCheck -- --dswap-probe --path root\amp` and `--path root\ir` — confirm `dswap` swaps name+content on those blocks with no crash (unlocks Cycle 2).

Record both findings in `PROTOCOL.md` (dswap amp/IR support; active-slot behavior). No code in this task.

---

## Task 1: Extend `--dswap-probe` with `--path` and an active-slot test

**Files:**
- Modify: `tools/HwCheck/Program.cs` (the `--dswap-probe` block, ~lines 494–560; the usage banner ~line 16)

**Interfaces:**
- Consumes: existing `session.Client!.SendRawAsync(string)`, the probe's existing backup/verify/restore scaffold.
- Produces: nothing consumed by later tasks (dev tool only). Enables Task 0's on-device run.

This is guarded dev-tool code with no unit tests; its verification is "builds clean + Ed runs it on-device" (Task 0). Keep the existing self-reversing backup/restore safety.

- [ ] **Step 1: Parameterize the swap path**

In the `--dswap-probe` block, replace the hardcoded path with an optional `--path` arg (default preserves today's behavior):

```csharp
// after: int dsp = Array.IndexOf(args, "--dswap-probe");
int dspPath = Array.IndexOf(args, "--path");
string swapPath = dspPath >= 0 && dspPath + 1 < args.Length ? args[dspPath + 1] : @"root\presets";
bool activeTest = Array.IndexOf(args, "--active") >= 0;
```

Then build the swap command from `swapPath` instead of the literal `root\presets`:

```csharp
var swapCmd = $"dswap {swapPath}:{{\"index\":{A},\"index2\":{B}}}";
```

- [ ] **Step 2: Add the active-slot sub-test**

When `--active` is passed and `swapPath == @"root\presets"`, before the swap, select slot A's preset so it is the live preset, capture the live name, do the swap, then re-read the live name and report whether it changed:

```csharp
if (activeTest && swapPath == @"root\presets")
{
    var nameA0 = (await sClient.SendRawAsync($"read root\\presets:{{}}")); // list, for context
    await sClient.SendRawAsync($"write root\\app\\preset:{{\"value\":\"{A_name}\"}}"); // select A live
    var liveBefore = await sClient.SendRawAsync("read root\\app\\preset");             // {"value":"<name>"}
    Console.WriteLine($"[active] live preset before swap: {liveBefore.Trim()}");
    // ... existing swap happens below ...
    // after swap:
    var liveAfter = await sClient.SendRawAsync("read root\\app\\preset");
    Console.WriteLine($"[active] live preset after swap:  {liveAfter.Trim()}");
    Console.WriteLine(liveBefore.Trim() == liveAfter.Trim()
        ? "   => ACTIVE-SLOT: live preset UNDISTURBED by dswap"
        : "   => ACTIVE-SLOT: live preset CHANGED by dswap — engine must re-select after a move touching the active slot");
}
```

(`A_name` is the name read from the pre-swap list for index A — reuse whatever the existing probe already captures for its name-swap verification.)

- [ ] **Step 3: Update the usage banner**

Extend the `--dswap-probe` usage comment (~line 16) to document `[--path <root\presets|root\amp|root\ir>] [--active]`.

- [ ] **Step 4: Build**

Run: `dotnet build tools/HwCheck`
Expected: build succeeds, no warnings introduced.

- [ ] **Step 5: Commit**

```bash
git add tools/HwCheck/Program.cs
git commit -m "feat(hwcheck): dswap-probe --path (amp/ir) + --active live-preset test"
```

---

## Task 2: `dswap` primitive — command, client, repository, fake device

**Files:**
- Modify: `src/Sonulab.Core/Protocol/SonuCommands.cs`
- Modify: `src/Sonulab.Core/SonuClient.cs`
- Modify: `src/Sonulab.Core/Services/DeviceRepository.cs`
- Modify: `tests/Sonulab.Core.Tests/FakePresetDevice.cs` (add `dswap` handling — test infra)
- Test: `tests/Sonulab.Core.Tests/DeviceRepositorySwapTests.cs` (new)

**Interfaces:**
- Produces:
  - `SonuCommands.DSwap(string path, int indexA, int indexB) : string`
  - `SonuClient.DSwapAsync(string path, int indexA, int indexB, CancellationToken) : Task<string>`
  - `DeviceRepository.SwapPresetSlotsAsync(int a, int b, CancellationToken) : Task` — swaps two preset slots (name+content) atomically; validates indices in `[0, SlotCount)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/DeviceRepositorySwapTests.cs`:

```csharp
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class DeviceRepositorySwapTests
{
    static FakePresetDevice Dev()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        return d;
    }
    static DeviceRepository Repo(FakePresetDevice d) => new(new SonuClient(d));

    [Fact] public async Task Swap_exchanges_name_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await r.SwapPresetSlotsAsync(0, 1);
        var names = (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
        Assert.Equal("B", names[0]);
        Assert.Equal("A", names[1]);
        Assert.Equal("\"mB\"", (await r.ReadPresetAsync(0)).GetValueJson(@"root\app\amp\amp"));
        Assert.Equal("\"mA\"", (await r.ReadPresetAsync(1)).GetValueJson(@"root\app\amp\amp"));
    }

    [Fact] public async Task Swap_with_empty_slot_moves_preset_and_empties_source()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await r.SwapPresetSlotsAsync(0, 5);   // slot 5 empty
        var names = (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
        Assert.Equal("", names[0]);
        Assert.Equal("A", names[5]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 30)]
    public async Task Swap_rejects_out_of_range_index(int a, int b)
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() => r.SwapPresetSlotsAsync(a, b));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter DeviceRepositorySwapTests`
Expected: FAIL — `SwapPresetSlotsAsync` / `DSwap` not defined (compile error).

- [ ] **Step 3: Add the command builder**

In `src/Sonulab.Core/Protocol/SonuCommands.cs`, add:

```csharp
    public static string DSwap(string path, int indexA, int indexB) =>
        $"dswap {path}:{{\"index\":{indexA},\"index2\":{indexB}}}";
```

- [ ] **Step 4: Add the client method**

In `src/Sonulab.Core/SonuClient.cs`, near `DWriteChunkAsync`:

```csharp
    public Task<string> DSwapAsync(string path, int indexA, int indexB, CancellationToken ct = default) =>
        SendAsync(SonuCommands.DSwap(path, indexA, indexB), ct);
```

- [ ] **Step 5: Add the repository method**

In `src/Sonulab.Core/Services/DeviceRepository.cs`, near `RenameAsync`:

```csharp
    /// <summary>Atomically swap two preset slots — name AND content — via the firmware `dswap`
    /// verb (~213 ms, byte-verified by firmware). No temp slot, no save-by-name, no name-uniqueness
    /// requirement. Indices must be in [0, SlotCount); a non-numeric index would crash the device.</summary>
    public Task SwapPresetSlotsAsync(int a, int b, CancellationToken ct = default)
    {
        if (a < 0 || a >= SlotCount) throw new ArgumentOutOfRangeException(nameof(a));
        if (b < 0 || b >= SlotCount) throw new ArgumentOutOfRangeException(nameof(b));
        return _client.DSwapAsync(PresetsList, a, b, ct);
    }
```

- [ ] **Step 6: Teach `FakePresetDevice` the `dswap` verb**

In `tests/Sonulab.Core.Tests/FakePresetDevice.cs`, add a regex beside the others:

```csharp
    static readonly Regex DSwapRx = new(@"^dswap (\S+):\{""index"":(-?\d+),""index2"":(-?\d+)\}$");
```

and a handler at the top of `SendAsync` (before the `DWriteRx` branch):

```csharp
        if ((m = DSwapRx.Match(command)).Success)
        {
            int a = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
            (_slots[a], _slots[b]) = (_slots[b], _slots[a]);   // swap name AND content atomically
            return Task.FromResult("");
        }
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Sonulab.Core.Tests --filter DeviceRepositorySwapTests`
Expected: PASS (all 4 cases).

- [ ] **Step 8: Commit**

```bash
git add src/Sonulab.Core/Protocol/SonuCommands.cs src/Sonulab.Core/SonuClient.cs \
        src/Sonulab.Core/Services/DeviceRepository.cs tests/Sonulab.Core.Tests/FakePresetDevice.cs \
        tests/Sonulab.Core.Tests/DeviceRepositorySwapTests.cs
git commit -m "feat(core): atomic dswap preset-slot swap primitive"
```

---

## Task 3: Rewrite `ReorderService` on `dswap` (delete the select+save machinery)

**Files:**
- Modify (replace body): `src/Sonulab.Core/Services/ReorderService.cs`
- Modify: `tests/Sonulab.Core.Tests/ReorderServiceTests.cs` (keep behavioral tests, remove internals-coupled tests, add verify-failure test)

**Interfaces:**
- Consumes: `DeviceRepository.SwapPresetSlotsAsync`, `ListPresetsAsync`, `PresetSlot.Name/IsEmpty`.
- Produces (unchanged public API): `ReorderService.MoveAsync(int from, int to, IProgress<ReorderProgress>?, CancellationToken)` and `MoveStepAsync(int from, bool up, IProgress<ReorderProgress>?, CancellationToken)`; `ReorderProgress` record unchanged.

**Design:** A move `from`→`to` is `|from−to|` adjacent `dswap`s (bubble). Each swap moves name+content atomically. After each swap, re-list and verify the two affected slot names match the locally-tracked expectation; on mismatch (or exception) STOP and throw — slots are never corrupted (`dswap` is atomic per firmware), so a stopped multi-swap move is a *valid partial order* the VM resyncs from. **No reverse-swap / no param-replay fallback / no temp slot / no `__sstmp_` guard.**

> Active-slot note: per Task 0's verdict. If the probe shows `dswap` disturbs the live preset, add a follow-up that records the live preset before a move and re-selects it after — a UX nicety, NOT required for slot correctness (reorder always leaves valid, uncorrupted slots). Not built in Cycle 1 unless the probe says it's needed.

- [ ] **Step 1: Write the new failing tests**

Replace the internals-coupled tests. First, ADD a verify-failure test and a multi-step-progress test. Append to `ReorderServiceTests.cs`:

```csharp
    // A device whose dswap silently does nothing — simulates misbehaving firmware so the engine's
    // post-swap name verify must catch it and throw without corrupting slots.
    sealed class NoOpSwapDevice : FakePresetDevice
    {
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
            => command.StartsWith("dswap ", System.StringComparison.Ordinal)
               ? Task.FromResult("")           // pretend it ran; slots unchanged
               : base.SendAsync(command, ct);
    }

    [Fact] public async Task Move_that_fails_verify_throws_and_leaves_slots_intact()
    {
        var d = new NoOpSwapDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => new ReorderService(r).MoveStepAsync(from: 0, up: false));
        Assert.Equal(new[] { "A", "B" }, (await Names(r))[..2]);   // uncorrupted
    }

    [Fact] public async Task MoveStep_into_empty_neighbor_moves_via_single_swap()
    {
        var d = Dev(used: 2); await d.OpenAsync(); var r = Repo(d);   // slots 0,1 full; 2 empty
        await new ReorderService(r).MoveStepAsync(from: 1, up: false); // B -> slot 2 (empty)
        var names = await Names(r);
        Assert.Equal("", names[1]);
        Assert.Equal("B", names[2]);
    }
```

Requires `FakePresetDevice.SendAsync` to be `virtual` (it already is — line 38) and `NoOpSwapDevice` to override it. (`Dev` and `Repo` helpers already exist in this file.)

- [ ] **Step 2: Remove internals-coupled tests**

Delete these tests (they assert removed behavior — temp slots, param-replay `ParamWrites`, `__sstmp_` names, relocate/save-failure rollback specifics):
`MoveStep_down_into_empty_relocates_with_one_copy`, `MoveStep_up_into_empty_relocates`,
`MoveStep_relocate_rolls_back_on_save_failure`, `MoveStep_relocate_rolls_back_on_final_rename_failure`,
`MoveStep_relocate_reads_no_preset_content`, `MoveStep_relocate_up_rolls_back_safely_when_verify_fails`,
and the two full-device "replay fallback" tests (~lines 53, 360). Also delete the now-unused
**test-file-local** helper classes they reference (e.g. `FailOnceOnSave` and any other fail-injection
subclasses defined inside `ReorderServiceTests.cs`). Do NOT touch shared members on `FakePresetDevice`
(e.g. a `ParamWrites` counter) — other test files may use them; leaving an unused shared member is
harmless, removing a still-referenced one breaks the build.
KEEP the pure behavioral tests: `Move_up_rotates_order_and_content`,
`Move_down_rotates_order_and_content`, `Same_index_is_noop`, and (rename it)
`Fallback_when_no_empty_temp_slot_still_reorders` → `Move_on_full_device_still_reorders` (dswap needs
no temp slot, so a full device reorders fine — the assertions stay valid).

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter ReorderServiceTests`
Expected: FAIL to compile / fail asserts (old machinery still present; new tests reference new behavior).

- [ ] **Step 4: Replace `ReorderService` with the dswap engine**

Replace the entire body of `src/Sonulab.Core/Services/ReorderService.cs` with:

```csharp
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

/// <summary>Reorders preset slots using the atomic firmware `dswap` verb: a move from→to is a
/// sequence of |from-to| adjacent swaps, each moving name AND content atomically (~213 ms).
/// After each swap the two affected slot names are read back and verified against the expected
/// order; on mismatch the move stops and throws. Because `dswap` is atomic per firmware, a stopped
/// move leaves a VALID partial order (no torn/corrupted slot) — the caller resyncs from the device.
/// No temp slot, no save-by-name, no name-uniqueness precondition.</summary>
public sealed class ReorderService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = slots.Select(s => s.Name).ToArray();
        int step = from < to ? 1 : -1;
        int total = Math.Abs(to - from), done = 0;
        for (int i = from; i != to; i += step)
            await SwapVerifiedAsync(i, i + step, expected, progress, ++done, total, ct);
        Log.Info("MoveAsync from={0} to={1} completed in {2} swap(s)", from, to, total);
    }

    public async Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        int to = up ? from - 1 : from + 1;
        if (to < 0 || to >= slots.Count) return;                  // at a boundary: nothing to do
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = slots.Select(s => s.Name).ToArray();
        await SwapVerifiedAsync(from, to, expected, progress, 1, 1, ct);
    }

    // One atomic swap + read-back name verify. Mutates `expected` to track the post-swap order.
    private async Task SwapVerifiedAsync(int a, int b, string[] expected,
        IProgress<ReorderProgress>? progress, int done, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _repo.SwapPresetSlotsAsync(a, b, ct);
        (expected[a], expected[b]) = (expected[b], expected[a]);
        var back = await _repo.ListPresetsAsync(ct);
        if (back[a].Name != expected[a] || back[b].Name != expected[b])
            throw new InvalidOperationException(
                $"Reorder verify failed after swapping slots {a + 1}/{b + 1}: device shows " +
                $"'{back[a].Name}'/'{back[b].Name}', expected '{expected[a]}'/'{expected[b]}'.");
        progress?.Report(new ReorderProgress(done, total, $"slots {a + 1}/{b + 1}"));
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter ReorderServiceTests`
Expected: PASS (behavioral + new verify-failure + empty-neighbor tests).

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests`
Expected: PASS (no other test depended on the deleted internals).

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/Services/ReorderService.cs tests/Sonulab.Core.Tests/ReorderServiceTests.cs
git commit -m "refactor(core): dswap-based ReorderService; delete select+save machinery"
```

---

## Task 4: `PresetUsageMap` pure transforms

**Files:**
- Modify: `src/Sonulab.Core/Services/PresetUsageMap.cs`
- Test: `tests/Sonulab.Core.Tests/PresetUsageMapTransformTests.cs` (new)

**Interfaces:**
- Produces (instance methods returning new maps):
  - `PresetUsageMap.WithMovedSlot(int from, int to)`
  - `PresetUsageMap.WithRenamedPreset(int index, string newName)`
  - `PresetUsageMap.WithoutSlot(int index)`

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/PresetUsageMapTransformTests.cs`:

```csharp
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageMapTransformTests
{
    static PresetUsageMap Map(params (int Slot, string Name, string Amp)[] p)
    {
        var docs = p.Select(x =>
        {
            var blob = new byte[PresetDocument.BlobSize];
            System.Text.Encoding.ASCII.GetBytes($@"root\app\amp\amp:{{""value"":""{x.Amp}""}}").CopyTo(blob, 0);
            return (x.Slot, x.Name, PresetDocument.Parse(blob));
        }).ToList();
        return PresetUsageMap.Build(docs);
    }
    static (int, string)[] Refs(PresetUsageMap m, string amp) =>
        m.PresetsUsingAmp(amp).Select(r => (r.Index, r.Name)).ToArray();

    [Fact] public void WithMovedSlot_up_rotates_ref_indices()
    {
        // slots 1,2,3 use amp "x"; move slot 3 -> 1 (up). Expect indices 1,2,3 -> the preset from 3
        // lands at 1, the others shift to 2,3; names ride along.
        var m = Map((1, "P1", "x"), (2, "P2", "x"), (3, "P3", "x")).WithMovedSlot(3, 1);
        Assert.Equal(new[] { (1, "P3"), (2, "P1"), (3, "P2") }, Refs(m, "x"));
    }

    [Fact] public void WithMovedSlot_leaves_out_of_range_refs_untouched()
    {
        var m = Map((0, "P0", "x"), (5, "P5", "x")).WithMovedSlot(1, 3);   // range [1,3] excludes 0 and 5
        Assert.Equal(new[] { (0, "P0"), (5, "P5") }, Refs(m, "x"));
    }

    [Fact] public void WithRenamedPreset_updates_name_at_index_only()
    {
        var m = Map((2, "Old", "x"), (4, "Keep", "x")).WithRenamedPreset(2, "New");
        Assert.Equal(new[] { (2, "New"), (4, "Keep") }, Refs(m, "x"));
    }

    [Fact] public void WithoutSlot_drops_refs_at_index()
    {
        var m = Map((2, "Gone", "x"), (4, "Keep", "x")).WithoutSlot(2);
        Assert.Equal(new[] { (4, "Keep") }, Refs(m, "x"));
    }

    [Fact] public void WithoutSlot_dropping_last_ref_removes_key()
    {
        var m = Map((2, "Only", "x")).WithoutSlot(2);
        Assert.Empty(m.PresetsUsingAmp("x"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetUsageMapTransformTests`
Expected: FAIL — `WithMovedSlot`/`WithRenamedPreset`/`WithoutSlot` not defined.

- [ ] **Step 3: Implement the transforms**

In `src/Sonulab.Core/Services/PresetUsageMap.cs`, add these members to the class (after the `Lookup` method):

```csharp
    /// <summary>Return a new map with the effect of moving a preset from slot <paramref name="from"/>
    /// to slot <paramref name="to"/>: every ref index inside the affected range is rotated (the moved
    /// preset takes <paramref name="to"/>; the others shift by one). Names ride along with content
    /// (dswap moves both), so only indices change. The map-side mirror of the reorder engine.</summary>
    public PresetUsageMap WithMovedSlot(int from, int to)
    {
        if (from == to) return this;
        int min = Math.Min(from, to), max = Math.Max(from, to), step = from < to ? -1 : 1;
        return Rebuild(r =>
        {
            if (r.Index < min || r.Index > max) return r;
            int ni = r.Index == from ? to : r.Index + step;
            return r with { Index = ni };
        });
    }

    /// <summary>Return a new map with the preset at <paramref name="index"/> renamed.</summary>
    public PresetUsageMap WithRenamedPreset(int index, string newName) =>
        Rebuild(r => r.Index == index ? r with { Name = newName } : r);

    /// <summary>Return a new map with all refs to the preset at <paramref name="index"/> removed.</summary>
    public PresetUsageMap WithoutSlot(int index) =>
        Rebuild(r => r.Index == index ? (PresetRef?)null : r);

    private PresetUsageMap Rebuild(Func<PresetRef, PresetRef?> f) =>
        new(Map(_amp, f), Map(_ir, f));

    private static IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> Map(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> src, Func<PresetRef, PresetRef?> f)
    {
        var result = new Dictionary<string, IReadOnlyList<PresetRef>>(src.Count);
        foreach (var (key, list) in src)
        {
            var next = list.Select(f).Where(r => r.HasValue).Select(r => r!.Value)
                           .OrderBy(r => r.Index).ToList();
            if (next.Count > 0) result[key] = next;   // drop keys that lost all refs
        }
        return result;
    }
```

(`PresetRef` is a `readonly record struct`, so `r with { Index = ... }` and the `PresetRef?` cast compile. `_amp`/`_ir` and the private constructor already exist.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetUsageMapTransformTests`
Expected: PASS (all 5 cases).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/PresetUsageMap.cs tests/Sonulab.Core.Tests/PresetUsageMapTransformTests.cs
git commit -m "feat(core): pure PresetUsageMap transforms (moved/renamed/without slot)"
```

---

## Task 5: `IPresetUsageService` targeted notifications

**Files:**
- Modify: `src/Namager.App/Services/PresetUsageService.cs` (interface + `PresetUsageService` + `NullPresetUsageService`)
- Modify: `tests/Namager.App.Tests/FakePresetUsageService.cs` (add notify counters — test infra)
- Test: `tests/Namager.App.Tests/PresetUsageServiceTests.cs` (add targeted-notify cases)

**Interfaces:**
- Produces on `IPresetUsageService`:
  - `void NotifyPresetMoved(int from, int to)`
  - `void NotifyPresetRenamed(int index, string newName)`
  - `void NotifyPresetDeleted(int index)`
  Each transforms `Current` via the Task 4 map methods, **keeps `IsComplete` unchanged**, and raises `MapUpdated`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Namager.App.Tests/PresetUsageServiceTests.cs` (uses the real `PresetUsageService` over a `FakePresetDevice`; follow the file's existing construction pattern for `MakeService`/repo — mirror whatever helper the file already uses to build a completed scan, then):

```csharp
    [Fact] public async Task NotifyPresetMoved_remaps_current_and_keeps_complete()
    {
        var (svc, _) = await CompletedService(   // helper that returns a service whose scan is complete
            (0, "A", "mA"), (1, "B", "mB"), (2, "C", "mC"));
        Assert.True(svc.IsComplete);
        int updates = 0; svc.MapUpdated += () => updates++;

        svc.NotifyPresetMoved(0, 2);   // A moves 0 -> 2

        Assert.True(svc.IsComplete);   // targeted maintenance does NOT drop completeness
        Assert.Equal(1, updates);
        Assert.Equal(2, svc.Current.PresetsUsingAmp("mA")[0].Index);
    }

    [Fact] public async Task NotifyPresetDeleted_drops_refs_and_keeps_complete()
    {
        var (svc, _) = await CompletedService((0, "A", "mA"), (1, "B", "mB"));
        svc.NotifyPresetDeleted(0);
        Assert.True(svc.IsComplete);
        Assert.Empty(svc.Current.PresetsUsingAmp("mA"));
    }

    [Fact] public async Task NotifyPresetRenamed_updates_ref_name()
    {
        var (svc, _) = await CompletedService((0, "A", "mA"));
        svc.NotifyPresetRenamed(0, "A2");
        Assert.Equal("A2", svc.Current.PresetsUsingAmp("mA")[0].Name);
    }
```

If the test file has no `CompletedService` helper, add one that seeds the device, constructs the
`PresetUsageService`, calls `await svc.EnsureCompleteAsync()`, and returns `(svc, repo)`. Seed lines
via `$@"root\app\amp\amp:{{""value"":""{amp}""}}"`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter PresetUsageServiceTests`
Expected: FAIL — the three `Notify…` methods are not defined.

- [ ] **Step 3: Add the interface members**

In `src/Namager.App/Services/PresetUsageService.cs`, add to `interface IPresetUsageService` (after `Invalidate`):

```csharp
    /// <summary>A verified reorder happened: remap Current in place (no rescan), keep IsComplete,
    /// raise MapUpdated. Callers must invoke ONLY on verified success; on failure use Invalidate().</summary>
    void NotifyPresetMoved(int from, int to);

    /// <summary>A verified rename happened: update the ref name at <paramref name="index"/> in place.</summary>
    void NotifyPresetRenamed(int index, string newName);

    /// <summary>A verified delete happened: drop refs at <paramref name="index"/> in place.</summary>
    void NotifyPresetDeleted(int index);
```

- [ ] **Step 4: Implement on `PresetUsageService`**

Add (after `Invalidate()`):

```csharp
    public void NotifyPresetMoved(int from, int to) => Apply(m => m.WithMovedSlot(from, to));
    public void NotifyPresetRenamed(int index, string newName) => Apply(m => m.WithRenamedPreset(index, newName));
    public void NotifyPresetDeleted(int index) => Apply(m => m.WithoutSlot(index));

    // Transform Current in place and notify. IsComplete is intentionally UNTOUCHED: if the map was
    // complete it stays complete (targeted maintenance); if a rescan is mid-flight it stays
    // incomplete and that scan re-derives the truth. Safe to call from the UI thread post-mutation.
    private void Apply(Func<PresetUsageMap, PresetUsageMap> transform)
    {
        _current = transform(_current);
        MapUpdated?.Invoke();
    }
```

- [ ] **Step 5: Implement on `NullPresetUsageService`**

Add no-ops:

```csharp
    public void NotifyPresetMoved(int from, int to) { }
    public void NotifyPresetRenamed(int index, string newName) { }
    public void NotifyPresetDeleted(int index) { }
```

- [ ] **Step 6: Add counters to `FakePresetUsageService`**

In `tests/Namager.App.Tests/FakePresetUsageService.cs`, add fields and interface members so VM tests (Task 6) can assert targeted calls without a real device:

```csharp
    public int MovedCount { get; private set; }
    public (int From, int To)? LastMoved { get; private set; }
    public int RenamedCount { get; private set; }
    public int DeletedCount { get; private set; }

    public void NotifyPresetMoved(int from, int to) { MovedCount++; LastMoved = (from, to); Map = Map.WithMovedSlot(from, to); RaiseMapUpdated(); }
    public void NotifyPresetRenamed(int index, string newName) { RenamedCount++; Map = Map.WithRenamedPreset(index, newName); RaiseMapUpdated(); }
    public void NotifyPresetDeleted(int index) { DeletedCount++; Map = Map.WithoutSlot(index); RaiseMapUpdated(); }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter PresetUsageServiceTests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namager.App/Services/PresetUsageService.cs tests/Namager.App.Tests/FakePresetUsageService.cs \
        tests/Namager.App.Tests/PresetUsageServiceTests.cs
git commit -m "feat(app): targeted usage-service notifications (moved/renamed/deleted)"
```

---

## Task 6: Rewire `PresetListViewModel` to use targeted notifications

**Files:**
- Modify: `src/Namager.App/ViewModels/PresetListViewModel.cs`
- Test: `tests/Namager.App.Tests/PresetListViewModelTests.cs` (add targeted-notify assertions)

**Interfaces:**
- Consumes: `IPresetUsageService.NotifyPresetMoved/Renamed/Deleted/Invalidate` (Task 5).
- Produces: behavior only — reorder/rename/delete success calls the matching targeted notify; duplicate and any failure call `Invalidate()`.

- [ ] **Step 1: Write the failing test**

The existing `Make()` helper builds the VM with the default `NullPresetUsageService`. Add an overload
that injects a `FakePresetUsageService`, then assert targeted calls. Append to
`PresetListViewModelTests.cs`:

```csharp
    static (PresetListViewModel vm, FakePresetDevice dev, FakePresetUsageService usage) MakeWithUsage()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        var usage = new FakePresetUsageService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, usage: usage);
        return (vm, dev, usage);
    }

    [Fact] public async Task MoveDown_notifies_moved_not_invalidate()
    {
        var (vm, _, usage) = MakeWithUsage();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                       // A at slot 0
        await vm.MoveDownCommand.ExecuteAsync(null);     // -> slot 1
        Assert.Equal(1, usage.MovedCount);
        Assert.Equal((0, 1), usage.LastMoved);
        Assert.Equal(0, usage.InvalidateCount);
    }

    [Fact] public async Task Delete_notifies_deleted_not_invalidate()
    {
        var (vm, _, usage) = MakeWithUsage();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[2];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, usage.DeletedCount);
        Assert.Equal(0, usage.InvalidateCount);
    }

    [Fact] public async Task Duplicate_still_invalidates()
    {
        var (vm, _, usage) = MakeWithUsage();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal(1, usage.InvalidateCount);
        Assert.Equal(0, usage.MovedCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter PresetListViewModelTests`
Expected: FAIL — `RunAsync` still calls `Invalidate()` for every op (MovedCount stays 0).

- [ ] **Step 3: Thread a map-maintenance callback through `RunAsync`**

In `src/Namager.App/ViewModels/PresetListViewModel.cs`, change `RunAsync` to accept an optional
success callback, defaulting to `Invalidate`, and call `Invalidate()` on failure:

```csharp
    private async Task<bool> RunAsync(string message, string success, Func<Task> work,
                                      Action? onSuccessMapUpdate = null)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try
        {
            await work();
            if (onSuccessMapUpdate is not null) onSuccessMapUpdate();   // targeted map maintenance
            else _usage.Invalidate();                                   // default: full rescan
            await ReloadAsync();
            _status.Success(success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "preset operation failed: {0}", message);
            _usage.Invalidate();   // device state uncertain after a failed mutation → force rescan
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            try { await ReloadAsync(); }
            catch (Exception reloadEx) { Log.Warn(reloadEx, "reload after a failed operation also failed"); }
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

- [ ] **Step 4: Pass targeted callbacks from each command**

Update the reorder/delete/rename call sites to pass the matching notification. `dest` is already the
destination slot in each move command:

- `MoveUpAsync`: `() => _reorder.MoveStepAsync(s.Index, up: true)` → add `, () => _usage.NotifyPresetMoved(s.Index, dest)`
- `MoveDownAsync`: add `, () => _usage.NotifyPresetMoved(s.Index, dest)`
- `MoveItemUpAsync`: add `, () => _usage.NotifyPresetMoved(s.Index, dest)`
- `MoveItemDownAsync`: add `, () => _usage.NotifyPresetMoved(s.Index, dest)`
- `DeleteAsync`: add `, () => _usage.NotifyPresetDeleted(s.Index)`
- `CommitRenameAsync`: add `, () => _usage.NotifyPresetRenamed(s.Index, name)`

Leave `DuplicateAsync` unchanged (no 4th arg → `Invalidate()`).

Example (MoveDownAsync):

```csharp
            if (await RunAsync($"Moving slot {s.DisplaySlot} down…", $"Moved '{s.Name}' down",
                    () => _reorder.MoveStepAsync(s.Index, up: false),
                    () => _usage.NotifyPresetMoved(s.Index, dest)) && dest < Items.Count)
                Selected = Items[dest];
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter PresetListViewModelTests`
Expected: PASS (targeted-notify + duplicate-still-invalidates).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS — all tests green (≥ prior 648, adjusted for the reorder tests removed in Task 3 and the new tests added here).

- [ ] **Step 7: Commit**

```bash
git add src/Namager.App/ViewModels/PresetListViewModel.cs tests/Namager.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(app): targeted usage-map maintenance on reorder/rename/delete"
```

---

## Hardware validation (before merge to main)

Run on-device (VoidX-Control CLOSED), then tick `docs/HARDWARE-VALIDATION-*.md`:

- [ ] Task 0 probe run complete; verdict (active-slot behavior + amp/IR dswap support) recorded in `PROTOCOL.md`.
- [ ] Single-step preset move up/down: order + name + content correct, ~213 ms/step.
- [ ] Multi-slot move (e.g. slot 6 → slot 1): final order matches expectation, content follows names.
- [ ] Reorder/rename/delete no longer trigger a full usage rescan — amp/IR "used" highlights update instantly with no flicker, and remain correct.
- [ ] If the Task 0 verdict was "live preset disturbed": open a follow-up for the re-select refinement (not required for slot correctness).
- [ ] Confirm reorder still works with a full (30/30) bank (dswap needs no temp slot).

## Self-review notes (traceability to spec)

- Spec C1 (dswap primitive, block-agnostic) → Task 2. C2 (rewritten engine, full replacement) → Task 3. C3 (map transforms) → Task 4. C4 (targeted notifications) → Task 5. C5 (VM rewiring) → Task 6. C0 (probe) → Task 1 + Task 0 gate.
- Non-goals honored: amp/IR reorder UI not touched; duplicate + param-edits keep `Invalidate()` (Task 6 Step 4); pipelining/#5/dmove excluded.
- Type consistency: `SwapPresetSlotsAsync` / `DSwapAsync` / `DSwap` / `WithMovedSlot` / `WithRenamedPreset` / `WithoutSlot` / `NotifyPresetMoved` / `NotifyPresetRenamed` / `NotifyPresetDeleted` names are used identically across producing and consuming tasks.
