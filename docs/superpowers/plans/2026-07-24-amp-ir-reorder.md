# Amp/IR Slot Reorder (Cycle 2) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user reorder amp and IR slots from their tabs via up/down buttons (mirroring the Presets tab), built on the hardware-confirmed atomic `dswap` verb.

**Architecture:** Extract the Cycle-1 bubble-swap engine into a shared `SlotBubbleReorder` helper that both preset reorder (over `DeviceRepository`) and amp/IR reorder (over `SlotBlobService`) delegate to. Add a `dswap`-based `SwapAsync` + `MoveStepAsync` to `SlotBlobService`, thin `MoveAmpStepAsync`/`MoveIrStepAsync` fronts on `AmpService`/`IrService`, mirror `PresetListViewModel`'s reorder commands in the amp/IR VMs, and copy the preset view's reorder buttons.

**Tech Stack:** C# / .NET 10, xUnit, Avalonia MVVM (CommunityToolkit.Mvvm). Offline tests use `FakeSlotBlobDevice` (Core) and `FakePresetUsageService` (App).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-24-amp-ir-reorder-design.md`. Prereq Cycle 1 merged (`dswap` confirmed on `root\amp` ~235 ms / `root\ir` ~120 ms — PROTOCOL.md).
- **Never send a non-numeric `dswap` index** — firmware `abort()`s / ESP32 reboots. Validate `[0, SlotCount)` before any I/O.
- **Amp/IR reorder touches `_usage` NOT AT ALL:** presets reference amps/IRs by NAME, and `dswap` preserves names, so reordering never changes a preset reference. No `Invalidate()`, no rescan, no targeted notify. (The amp/IR `RunAsync` already never invalidated — its `ReloadAsync` re-applies name-keyed highlights and `EnsureScanning()` is a no-op on a complete map.)
- **No "used by preset" guard on reorder.** Delete/rename block a referenced amp/IR (`ResolveUsageAsync`/`BlockUsed`); reorder must NOT — it preserves the name, so a referenced amp/IR reorders freely.
- UI mirrors presets: up/down buttons only (no drag).
- COMMITS: stage the listed files by explicit path — **NEVER** `git add -A`/`-u` (untracked `NAMFiles/`, `tools/mdns2.py`, `.superpowers/` must stay untracked).
- Full suite green before every commit (`dotnet test`). Prior baseline: 653.
- Commit trailers (every commit): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_015XvrwAWVh3xWVSwqk1GM54`.

---

## Task 1: Extract `SlotBubbleReorder` shared engine; refactor `ReorderService` to delegate

**Files:**
- Create: `src/Sonulab.Core/Services/SlotBubbleReorder.cs`
- Modify: `src/Sonulab.Core/Services/ReorderService.cs`
- Test: `tests/Sonulab.Core.Tests/SlotBubbleReorderTests.cs` (new)

**Interfaces:**
- Produces:
  - `public static class SlotBubbleReorder` with
    `MoveAsync(int from, int to, Func<CancellationToken, Task<IReadOnlyList<string>>> readNames, Func<int, int, CancellationToken, Task> swap, IProgress<ReorderProgress>?, CancellationToken)`
    and `MoveStepAsync(int from, bool up, …same delegates…)`.
  - `ReorderProgress` record stays in `ReorderService.cs` (same namespace; the helper references it).
- Consumes: `DeviceRepository.ListPresetsAsync`, `DeviceRepository.SwapPresetSlotsAsync` (unchanged).

- [ ] **Step 1: Write the failing engine test**

Create `tests/Sonulab.Core.Tests/SlotBubbleReorderTests.cs`. It drives the engine with pure in-memory delegates (no device fake) — a `List<string>` state, a `swap` lambda, and a `readNames` lambda:

```csharp
using Sonulab.Core.Services;
using Xunit;

public class SlotBubbleReorderTests
{
    // In-memory block: names + an atomic swap, mirroring what dswap does on the device.
    sealed class Block
    {
        public readonly List<string> Names;
        public int SwapCount;
        public int? NoOpSwapAt;                    // when set, the Nth swap silently does nothing
        public Block(params string[] names) => Names = names.ToList();
        public Task<IReadOnlyList<string>> Read(System.Threading.CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Names.ToArray());
        public Task Swap(int a, int b, System.Threading.CancellationToken ct)
        {
            if (++SwapCount == NoOpSwapAt) return Task.CompletedTask;   // simulate a swap that didn't apply
            (Names[a], Names[b]) = (Names[b], Names[a]);
            return Task.CompletedTask;
        }
    }

    [Fact] public async Task MoveStep_down_swaps_once()
    {
        var b = new Block("A", "B", "C");
        await SlotBubbleReorder.MoveStepAsync(0, up: false, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "B", "A", "C" }, b.Names);
        Assert.Equal(1, b.SwapCount);
    }

    [Fact] public async Task MoveStep_into_empty_neighbor_moves_via_single_swap()
    {
        var b = new Block("A", "");            // slot 1 empty
        await SlotBubbleReorder.MoveStepAsync(0, up: false, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "", "A" }, b.Names);
    }

    [Fact] public async Task MoveAsync_up_bubbles_to_remove_insert_order()
    {
        var b = new Block("A", "B", "C", "D");
        await SlotBubbleReorder.MoveAsync(3, 1, b.Read, b.Swap, null, default);   // D -> slot 1
        Assert.Equal(new[] { "A", "D", "B", "C" }, b.Names);
        Assert.Equal(2, b.SwapCount);
    }

    [Fact] public async Task MoveAsync_over_interior_empty_bubbles_the_empty_too()
    {
        var b = new Block("A", "", "C", "D");
        await SlotBubbleReorder.MoveAsync(3, 0, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "D", "A", "", "C" }, b.Names);
    }

    [Fact] public async Task MoveAsync_empty_source_throws()
    {
        var b = new Block("A", "", "C");
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => SlotBubbleReorder.MoveAsync(1, 2, b.Read, b.Swap, null, default));
    }

    [Fact] public async Task MoveAsync_midway_verify_failure_throws_and_leaves_valid_partial_order()
    {
        var b = new Block("A", "B", "C", "D") { NoOpSwapAt = 2 };   // 2nd swap of MoveAsync(0,3) no-ops
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => SlotBubbleReorder.MoveAsync(0, 3, b.Read, b.Swap, null, default));
        Assert.Equal(new[] { "B", "A", "C", "D" }, b.Names);        // only swap 1 applied — valid order
    }

    [Fact] public async Task MoveStep_at_boundary_is_noop()
    {
        var b = new Block("A", "B");
        await SlotBubbleReorder.MoveStepAsync(0, up: true, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "A", "B" }, b.Names);
        Assert.Equal(0, b.SwapCount);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBubbleReorderTests`
Expected: FAIL — `SlotBubbleReorder` not defined (compile error).

- [ ] **Step 3: Create the shared engine**

Create `src/Sonulab.Core/Services/SlotBubbleReorder.cs`:

```csharp
namespace Sonulab.Core.Services;

/// <summary>The block-agnostic bubble-swap reorder engine: a move from→to is |from-to| adjacent
/// atomic swaps, each verified by reading the block's names back and comparing the two affected
/// slots against a locally-tracked expected order; on mismatch it throws. Because each swap is
/// atomic (firmware `dswap`), a stopped multi-swap move leaves a VALID partial order — the caller
/// resyncs. Shared by preset reorder (over DeviceRepository) and amp/IR reorder (over
/// SlotBlobService); each supplies its own readNames + swap delegates. No temp slot, no
/// name-uniqueness precondition.</summary>
public static class SlotBubbleReorder
{
    public static async Task MoveAsync(int from, int to,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var names = await readNames(ct);
        if (from < 0 || from >= names.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= names.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (string.IsNullOrEmpty(names[from])) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = names.ToArray();
        int step = from < to ? 1 : -1;
        int total = Math.Abs(to - from), done = 0;
        for (int i = from; i != to; i += step)
            await SwapVerifiedAsync(i, i + step, expected, readNames, swap, progress, ++done, total, ct);
    }

    public static async Task MoveStepAsync(int from, bool up,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var names = await readNames(ct);
        if (from < 0 || from >= names.Count) throw new ArgumentOutOfRangeException(nameof(from));
        int to = up ? from - 1 : from + 1;
        if (to < 0 || to >= names.Count) return;                  // at a boundary: nothing to do
        if (string.IsNullOrEmpty(names[from])) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = names.ToArray();
        await SwapVerifiedAsync(from, to, expected, readNames, swap, progress, 1, 1, ct);
    }

    // One atomic swap + read-back name verify. Mutates `expected` to track the post-swap order.
    private static async Task SwapVerifiedAsync(int a, int b, string[] expected,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, int done, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await swap(a, b, ct);
        (expected[a], expected[b]) = (expected[b], expected[a]);
        var back = await readNames(ct);
        if (back[a] != expected[a] || back[b] != expected[b])
            throw new InvalidOperationException(
                $"Reorder verify failed after swapping slots {a + 1}/{b + 1}: device shows " +
                $"'{back[a]}'/'{back[b]}', expected '{expected[a]}'/'{expected[b]}'.");
        progress?.Report(new ReorderProgress(done, total, $"slots {a + 1}/{b + 1}"));
    }
}
```

- [ ] **Step 4: Refactor `ReorderService` to delegate**

Replace the body of `src/Sonulab.Core/Services/ReorderService.cs` (keep the `ReorderProgress` record and namespace):

```csharp
namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

/// <summary>Reorders PRESET slots via the shared <see cref="SlotBubbleReorder"/> engine over the
/// atomic firmware `dswap` verb (see that type for the algorithm + safety rationale).</summary>
public sealed class ReorderService
{
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default) =>
        SlotBubbleReorder.MoveAsync(from, to, ReadNamesAsync, _repo.SwapPresetSlotsAsync, progress, ct);

    public Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default) =>
        SlotBubbleReorder.MoveStepAsync(from, up, ReadNamesAsync, _repo.SwapPresetSlotsAsync, progress, ct);

    private async Task<IReadOnlyList<string>> ReadNamesAsync(CancellationToken ct) =>
        (await _repo.ListPresetsAsync(ct)).Select(s => s.Name).ToArray();
}
```

(`_repo.SwapPresetSlotsAsync` — signature `Task SwapPresetSlotsAsync(int, int, CancellationToken)` — binds to the `Func<int,int,CancellationToken,Task>` delegate as a method group.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBubbleReorderTests`
Expected: PASS (all 8).
Then the existing preset engine tests still pass through delegation:
Run: `dotnet test tests/Sonulab.Core.Tests --filter ReorderServiceTests`
Expected: PASS (unchanged).

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/Services/SlotBubbleReorder.cs src/Sonulab.Core/Services/ReorderService.cs \
        tests/Sonulab.Core.Tests/SlotBubbleReorderTests.cs
git commit -m "refactor(core): extract shared SlotBubbleReorder engine; ReorderService delegates"
```

---

## Task 2: `SlotBlobService.SwapAsync` + `MoveStepAsync`; teach `FakeSlotBlobDevice` `dswap`

**Files:**
- Modify: `src/Sonulab.Core/Services/SlotBlobService.cs`
- Modify: `tests/Sonulab.Core.Tests/FakeSlotBlobDevice.cs`
- Test: `tests/Sonulab.Core.Tests/SlotBlobReorderTests.cs` (new)

**Interfaces:**
- Consumes: `SonuClient.DSwapAsync(path, a, b, ct)` (Cycle 1, block-agnostic); `SlotBubbleReorder` (Task 1); `SlotBlobService.ListAsync`.
- Produces on `SlotBlobService`:
  - `Task SwapAsync(int a, int b, CancellationToken ct = default)` — validates `[0,SlotCount)`, emits `dswap` on `_kind.ListPath`.
  - `Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)` — delegates to `SlotBubbleReorder`.
  (No `MoveAsync` on `SlotBlobService`: the amp/IR UI only single-steps; the multi-slot engine lives on `SlotBubbleReorder` and is used by presets via HwCheck.)

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/SlotBlobReorderTests.cs`:

```csharp
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class SlotBlobReorderTests
{
    // Amp-shaped blob device (96 chunks / 12288 B). A 1-byte "blob" content marker per slot is
    // enough to prove content travels with the swap — seed a distinct first byte per slot.
    static (SlotBlobService svc, FakeSlotBlobDevice dev) Amp()
    {
        var dev = new FakeSlotBlobDevice(@"root\amp", 96, 12288);
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new SlotBlobService(new SonuClient(dev), SlotBlobKind.Amp, "backups",
                                      msg => new System.InvalidOperationException(msg));
        return (svc, dev);
    }
    static byte[] Blob(byte marker) { var b = new byte[12288]; b[0] = marker; return b; }

    [Fact] public async Task Swap_exchanges_name_and_content()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        dev.SeedSlot(1, "B", Blob(0xB0));
        await svc.SwapAsync(0, 1);
        Assert.Equal(new[] { "B", "A" }, new[] { dev.SlotNames[0], dev.SlotNames[1] });
        Assert.Equal(0xB0, dev.SlotBlobs[0]![0]);
        Assert.Equal(0xA0, dev.SlotBlobs[1]![0]);
    }

    [Fact] public async Task Swap_with_empty_slot_moves_and_empties_source()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        await svc.SwapAsync(0, 5);
        Assert.Null(dev.SlotNames[0]);
        Assert.Equal("A", dev.SlotNames[5]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 30)]
    public async Task Swap_rejects_out_of_range(int a, int b)
    {
        var (svc, _) = Amp();
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => svc.SwapAsync(a, b));
    }

    [Fact] public async Task MoveStep_down_reorders_via_swap()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        dev.SeedSlot(1, "B", Blob(0xB0));
        await svc.MoveStepAsync(0, up: false);
        Assert.Equal(new[] { "B", "A" }, new[] { dev.SlotNames[0], dev.SlotNames[1] });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBlobReorderTests`
Expected: FAIL — `SwapAsync`/`MoveStepAsync` not defined AND the fake ignores `dswap` (the swap tests would fail even once the methods exist, until the fake handles `dswap`).

- [ ] **Step 3: Teach `FakeSlotBlobDevice` the `dswap` verb**

In `tests/Sonulab.Core.Tests/FakeSlotBlobDevice.cs`, add the regex beside `DWriteRx`/`DReadRx`:

```csharp
    private static readonly Regex DSwapRx = new(@"^dswap (\S+):\{""index"":(-?\d+),""index2"":(-?\d+)\}$");
```

and add this branch at the top of `SendAsync` (right after `_log.Add(command);` and `Match m;`, before the `DWriteRx` branch):

```csharp
        if ((m = DSwapRx.Match(command)).Success && m.Groups[1].Value == _listPath)
        {
            int a = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
            (_slots[a], _slots[b]) = (_slots[b], _slots[a]);   // swap whole slot (name + blob) atomically
            return Task.FromResult($"dswap {_listPath}:{{\"index\":{a},\"index2\":{b}}}\r\n");
        }
```

- [ ] **Step 4: Add `SwapAsync` + `MoveStepAsync` to `SlotBlobService`**

In `src/Sonulab.Core/Services/SlotBlobService.cs`, add (near `RenameAsync`):

```csharp
    /// <summary>Atomically swap two slots — name AND content — via the firmware `dswap` verb
    /// (~120–235 ms, byte-verified, self-inverse; PROTOCOL.md). No temp slot, no name-uniqueness
    /// requirement. Indices must be in [0, SlotCount); a non-numeric index would crash the device.</summary>
    public Task SwapAsync(int a, int b, CancellationToken ct = default)
    {
        if (a is < 0 or >= SlotCount) throw _raise($"Slot must be 0..{SlotCount - 1}, got {a}.");
        if (b is < 0 or >= SlotCount) throw _raise($"Slot must be 0..{SlotCount - 1}, got {b}.");
        return _client.DSwapAsync(_kind.ListPath, a, b, ct);
    }

    /// <summary>Move one slot up/down one position via the shared bubble-swap engine. Reordering
    /// never changes any preset's amp/IR reference (presets reference by name; dswap preserves the
    /// name), so this needs no usage rescan.</summary>
    public Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default) =>
        SlotBubbleReorder.MoveStepAsync(from, up, ReadNamesAsync, SwapAsync, progress, ct);

    private async Task<IReadOnlyList<string>> ReadNamesAsync(CancellationToken ct) =>
        (await ListAsync(ct)).Select(s => s.Name).ToArray();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBlobReorderTests`
Expected: PASS (all cases).

- [ ] **Step 6: Full Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/Services/SlotBlobService.cs tests/Sonulab.Core.Tests/FakeSlotBlobDevice.cs \
        tests/Sonulab.Core.Tests/SlotBlobReorderTests.cs
git commit -m "feat(core): SlotBlobService dswap SwapAsync + MoveStepAsync (amp/ir reorder engine)"
```

---

## Task 3: Amp tab reorder — service front, item flags, VM commands, view buttons

**Files:**
- Modify: `src/Sonulab.Core/Services/AmpService.cs`
- Modify: `src/Namager.App/ViewModels/AmpItemViewModel.cs`
- Modify: `src/Namager.App/ViewModels/AmpListViewModel.cs`
- Modify: `src/Namager.App/Views/AmpListView.axaml`
- Modify: `docs/HARDWARE-VALIDATION-amps-tab.md`
- Test: `tests/Namager.App.Tests/AmpListViewModelTests.cs`

**Interfaces:**
- Consumes: `SlotBlobService.MoveStepAsync` (Task 2), `AmpService._inner`.
- Produces: `AmpService.MoveAmpStepAsync(int from, bool up, CancellationToken ct = default)`; `AmpItemViewModel.CanMoveUp`/`CanMoveDown`; `AmpListViewModel.MoveUpCommand`/`MoveDownCommand`/`MoveItemUpCommand`/`MoveItemDownCommand`.

- [ ] **Step 1: Write the failing VM tests**

Read the existing `AmpListViewModelTests.cs` construction helper and mirror it. Add tests (adapt the helper name to whatever the file uses — it already builds an `AmpListViewModel` over a `FakeSlotBlobDevice`/`AmpService`, with an optional `FakePresetUsageService`):

```csharp
    [Fact] public async Task MoveDown_reorders_items_and_touches_usage_never()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                       // "A" at slot 0
        await vm.MoveDownCommand.ExecuteAsync(null);     // -> slot 1
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, usage.InvalidateCount);          // reorder must NOT rescan
        Assert.Equal(0, usage.MovedCount);               // nor targeted-notify (that's presets only)
    }

    [Fact] public async Task Reorder_is_allowed_on_a_referenced_amp()
    {
        // amp "A" is used by a preset; delete/rename would be blocked, but reorder must succeed.
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        usage.Map = FakePresetUsageService.MapFor((0, "P0", new[] { FakePresetUsageService.AmpLine("A") }));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal("A", vm.Items[1].Name);             // moved, not blocked
        Assert.Null(vm.ErrorMessage);
    }
```

If the test file lacks a `MakeWithUsage` returning `(vm, dev, usage)` with a seed list, add one that: builds a `FakeSlotBlobDevice(@"root\amp", 96, 12288)`, `SeedSlot`s each `(name, markerByte)` with a 12288-B blob whose first byte is the marker, opens it, constructs `AmpService` over `new SonuClient(dev)`, and constructs `AmpListViewModel` with a `FakePresetUsageService`. Mirror the existing file's construction.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter AmpListViewModelTests`
Expected: FAIL — `MoveDownCommand` etc. not defined.

- [ ] **Step 3: Add the `AmpService` front**

In `src/Sonulab.Core/Services/AmpService.cs`, near `RenameAmpAsync`:

```csharp
    /// <summary>Move an amp slot up/down one position (atomic dswap; no usage impact — see
    /// SlotBlobService.MoveStepAsync).</summary>
    public Task MoveAmpStepAsync(int from, bool up, CancellationToken ct = default) =>
        _inner.MoveStepAsync(from, up, null, ct);
```

- [ ] **Step 4: Add `CanMoveUp`/`CanMoveDown` to `AmpItemViewModel`**

In `src/Namager.App/ViewModels/AmpItemViewModel.cs`, after `IsEmpty`:

```csharp
    public bool CanMoveUp => !IsEmpty && Index > 0;
    public bool CanMoveDown => !IsEmpty && Index < Sonulab.Core.Services.SlotBlobService.SlotCount - 1;
```

(Items are rebuilt on every reload, so these fixed-per-item getters need no change notification — same as `PresetItemViewModel`.)

- [ ] **Step 5: Add the reorder commands to `AmpListViewModel`**

In `src/Namager.App/ViewModels/AmpListViewModel.cs`, add (mirroring `PresetListViewModel`; they route through the existing `RunAsync`, which for amps also drains in-flight detail reads and never invalidates usage):

```csharp
    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { IsEmpty: false, Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _amps.MoveAmpStepAsync(s.Index, up: true)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { IsEmpty: false } s && s.Index < AmpService.SlotCount - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _amps.MoveAmpStepAsync(s.Index, up: false)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveItemUpAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _amps.MoveAmpStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= AmpService.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _amps.MoveAmpStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }
```

- [ ] **Step 6: Run the VM tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter AmpListViewModelTests`
Expected: PASS.

- [ ] **Step 7: Add the reorder buttons to `AmpListView.axaml`**

First add the `Button.reorder` style (copy from `PresetListView.axaml` lines 8–16) inside a `<UserControl.Styles>` block if the file has none, else add the `<Style Selector="Button.reorder">` to the existing styles.

In the top command-bar `StackPanel` (the one starting at line 19 with the Refresh button), add after the Refresh `Button`:

```xml
      <Button Command="{Binding MoveUpCommand}" IsEnabled="{Binding CanMutate}" ToolTip.Tip="Move up">
        <PathIcon Data="{StaticResource Icon.ChevronUp}" Width="16" Height="16"/>
      </Button>
      <Button Command="{Binding MoveDownCommand}" IsEnabled="{Binding CanMutate}" ToolTip.Tip="Move down">
        <PathIcon Data="{StaticResource Icon.ChevronDown}" Width="16" Height="16"/>
      </Button>
```

In the item `DataTemplate` (`x:DataType="vm:AmpItemViewModel"`, line 45), add a right-docked reorder `StackPanel` as the FIRST child of the item's root panel (mirroring `PresetListView.axaml` lines 52–64; adapt the parent binding type to `AmpListViewModel`):

```xml
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="2"
                        IsVisible="{Binding !IsEmpty}" VerticalAlignment="Center">
              <Button Classes="reorder" IsEnabled="{Binding CanMoveUp}" ToolTip.Tip="Move up"
                      Command="{Binding $parent[ListBox].((vm:AmpListViewModel)DataContext).MoveItemUpCommand}"
                      CommandParameter="{Binding}">
                <PathIcon Data="{StaticResource Icon.ChevronUp}" Width="12" Height="12"/>
              </Button>
              <Button Classes="reorder" IsEnabled="{Binding CanMoveDown}" ToolTip.Tip="Move down"
                      Command="{Binding $parent[ListBox].((vm:AmpListViewModel)DataContext).MoveItemDownCommand}"
                      CommandParameter="{Binding}">
                <PathIcon Data="{StaticResource Icon.ChevronDown}" Width="12" Height="12"/>
              </Button>
            </StackPanel>
```

If the item root is not a `DockPanel` (so `DockPanel.Dock="Right"` wouldn't apply), wrap/adjust to match the file's layout while keeping the buttons right-aligned — read the template first and mirror `PresetListView`'s structure. Do not hardcode colors (use the existing `PathIcon`/tokens).

- [ ] **Step 8: Build the app to verify the XAML compiles**

Run: `dotnet build src/Namager.App`
Expected: build succeeds (no AVLN XAML errors).

- [ ] **Step 9: Add hardware-validation rows**

Append to `docs/HARDWARE-VALIDATION-amps-tab.md` under "## Checks":

```markdown
- [ ] **Reorder**: select an amp, click Move Up/Down (toolbar) and the per-row up/down buttons;
      confirm the slot order changes on the pedal, ~235 ms/step, names+content intact.
- [ ] **Reorder a referenced amp**: reorder an amp that a preset uses; confirm it is NOT blocked,
      the move succeeds, and the preset still resolves its amp (name unchanged).
- [ ] **No highlight rescan**: the "used in presets" highlights do not blank/reflow after a reorder.
```

- [ ] **Step 10: Full suite + commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/Sonulab.Core/Services/AmpService.cs src/Namager.App/ViewModels/AmpItemViewModel.cs \
        src/Namager.App/ViewModels/AmpListViewModel.cs src/Namager.App/Views/AmpListView.axaml \
        docs/HARDWARE-VALIDATION-amps-tab.md tests/Namager.App.Tests/AmpListViewModelTests.cs
git commit -m "feat(app): amp slot reorder (up/down buttons, dswap engine, no usage rescan)"
```

---

## Task 4: IR tab reorder — service front, item flags, VM commands, view buttons

**Files:**
- Modify: `src/Sonulab.Core/Services/IrService.cs`
- Modify: `src/Namager.App/ViewModels/IrItemViewModel.cs`
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs`
- Modify: `src/Namager.App/Views/IrListView.axaml`
- Create: `docs/HARDWARE-VALIDATION-irs-tab.md`
- Test: `tests/Namager.App.Tests/IrListViewModelTests.cs`

**Interfaces:**
- Consumes: `SlotBlobService.MoveStepAsync` (Task 2), `IrService._inner`.
- Produces: `IrService.MoveIrStepAsync(int from, bool up, CancellationToken ct = default)`; `IrItemViewModel.CanMoveUp`/`CanMoveDown`; `IrListViewModel.MoveUpCommand`/`MoveDownCommand`/`MoveItemUpCommand`/`MoveItemDownCommand`.

This task is the exact IR twin of Task 3. `IrService` has no `SlotCount` constant, so use `Sonulab.Core.Services.SlotBlobService.SlotCount` for the boundary checks. `IrListViewModel.RunAsync` already never invalidates usage (same as amps), so reorder inherits the no-rescan behavior.

- [ ] **Step 1: Write the failing VM tests**

In `tests/Namager.App.Tests/IrListViewModelTests.cs`, add (mirroring Task 3 Step 1, `Ir`-flavored; the IR fake device is `new FakeSlotBlobDevice(@"root\ir", 32, 4096)` and blobs are 4096 B; IRs use `FakePresetUsageService.IrLine` for the referenced-usage case and `PresetsUsingIr`):

```csharp
    [Fact] public async Task MoveDown_reorders_items_and_touches_usage_never()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, usage.InvalidateCount);
        Assert.Equal(0, usage.MovedCount);
    }

    [Fact] public async Task Reorder_is_allowed_on_a_referenced_ir()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        usage.Map = FakePresetUsageService.MapFor((0, "P0", new[] { FakePresetUsageService.IrLine("A") }));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Null(vm.ErrorMessage);
    }
```

Add a `MakeWithUsage(seed)` helper mirroring the amp one but with the IR list path/size (`@"root\ir", 32, 4096`) and `IrService`/`IrListViewModel`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter IrListViewModelTests`
Expected: FAIL — commands not defined.

- [ ] **Step 3: Add the `IrService` front**

In `src/Sonulab.Core/Services/IrService.cs`, near `RenameIrAsync`:

```csharp
    /// <summary>Move an IR slot up/down one position (atomic dswap; no usage impact).</summary>
    public Task MoveIrStepAsync(int from, bool up, CancellationToken ct = default) =>
        _inner.MoveStepAsync(from, up, null, ct);
```

- [ ] **Step 4: Add `CanMoveUp`/`CanMoveDown` to `IrItemViewModel`**

In `src/Namager.App/ViewModels/IrItemViewModel.cs`, after `IsEmpty`:

```csharp
    public bool CanMoveUp => !IsEmpty && Index > 0;
    public bool CanMoveDown => !IsEmpty && Index < Sonulab.Core.Services.SlotBlobService.SlotCount - 1;
```

- [ ] **Step 5: Add the reorder commands to `IrListViewModel`**

Add the four commands exactly as in Task 3 Step 5, but calling `_irs.MoveIrStepAsync(...)`, using `AmpItemViewModel`→`IrItemViewModel` and `SlotBlobService.SlotCount` for the down boundary:

```csharp
    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { IsEmpty: false, Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _irs.MoveIrStepAsync(s.Index, up: true)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { IsEmpty: false } s && s.Index < Sonulab.Core.Services.SlotBlobService.SlotCount - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _irs.MoveIrStepAsync(s.Index, up: false)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveItemUpAsync(IrItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _irs.MoveIrStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(IrItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= Sonulab.Core.Services.SlotBlobService.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _irs.MoveIrStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }
```

- [ ] **Step 6: Run the VM tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter IrListViewModelTests`
Expected: PASS.

- [ ] **Step 7: Add the reorder buttons to `IrListView.axaml`**

Mirror Task 3 Step 7 exactly, but with `vm:IrListViewModel` / `vm:IrItemViewModel`. Add the `Button.reorder` style (copy from `PresetListView.axaml`) if absent; add the two toolbar Move buttons after Refresh in the command-bar `StackPanel` (line 18); add the right-docked reorder `StackPanel` to the item `DataTemplate` (`x:DataType="vm:IrItemViewModel"`, line 82). Read the template first to place the buttons correctly.

- [ ] **Step 8: Build the app**

Run: `dotnet build src/Namager.App`
Expected: build succeeds.

- [ ] **Step 9: Create the IR hardware-validation checklist**

Create `docs/HARDWARE-VALIDATION-irs-tab.md`:

```markdown
# Hardware validation — IRs tab reorder (Cycle 2)

Manual checks requiring the pedal (VoidX-Control CLOSED; app via `dotnet run --project src/Namager.App`).

- [ ] **Reorder**: select an IR, click Move Up/Down (toolbar) and the per-row up/down buttons;
      confirm the slot order changes on the pedal, ~120 ms/step, names+content intact.
- [ ] **Reorder a referenced IR**: reorder an IR that a preset uses; confirm it is NOT blocked,
      the move succeeds, and the preset still resolves its IR (name unchanged).
- [ ] **No highlight rescan**: the "used in presets" highlights do not blank/reflow after a reorder.
```

- [ ] **Step 10: Full suite + commit**

Run: `dotnet test`
Expected: PASS (whole solution green).

```bash
git add src/Sonulab.Core/Services/IrService.cs src/Namager.App/ViewModels/IrItemViewModel.cs \
        src/Namager.App/ViewModels/IrListViewModel.cs src/Namager.App/Views/IrListView.axaml \
        docs/HARDWARE-VALIDATION-irs-tab.md tests/Namager.App.Tests/IrListViewModelTests.cs
git commit -m "feat(app): IR slot reorder (up/down buttons, dswap engine, no usage rescan)"
```

---

## Self-review (traceability to spec)

- Spec C1 (`SlotBlobService.SwapAsync`) → Task 2. C2 (`SlotBubbleReorder` + `ReorderService` refactor) → Task 1. C3 (`SlotBlobService.MoveStepAsync`) → Task 2. C4 (amp/IR service fronts) → Tasks 3/4. C5 (VM commands) → Tasks 3/4. C6 (item `CanMove*`) → Tasks 3/4. C7 (views) → Tasks 3/4.
- Simplification 1 (no usage rescan) enforced + tested: Tasks 3/4 assert `InvalidateCount == 0` and `MovedCount == 0`; the amp/IR `RunAsync` never invalidated. Simplification 2 (no used-guard) tested: `Reorder_is_allowed_on_a_referenced_amp/ir`.
- Non-goals honored: no drag; `SlotBlobService.MoveAsync` (multi-slot) deliberately omitted (UI single-steps only; multi-slot lives on the shared engine for presets/HwCheck); no preset changes beyond the shared-helper extraction.
- Type consistency: `SlotBubbleReorder.MoveAsync/MoveStepAsync`, `SlotBlobService.SwapAsync/MoveStepAsync`, `AmpService.MoveAmpStepAsync`, `IrService.MoveIrStepAsync`, `Can MoveUp/CanMoveDown`, `MoveUp/MoveDown/MoveItemUp/MoveItemDownCommand` used identically across producing/consuming tasks.
