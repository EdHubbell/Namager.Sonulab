# Preset Reorder (Plan 3b of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement drag-style preset reorder — move a preset from one slot to another, shifting the range between — as an atomic, read-back-verified operation with rollback, built on Plan 3a's `WritePresetToSlotAsync`.

**Architecture:** A pure `SlotPlanner` computes the new slot arrangement (list remove+insert) and the affected range — fully unit-testable with zero device. `ReorderService` snapshots the affected slots' blobs, writes the new arrangement using **temporary unique names** (so the device's save-by-name is never ambiguous), finalizes the real names, and rolls back from the snapshot on any failure. Because preset content can't be `dwrite`-n, every slot write replays params via the proven primitive — slow but safe — so a progress callback is provided.

**Tech Stack:** .NET 10, C#, xUnit. Builds on Plan 3a (`DeviceRepository`, `WritePresetToSlotAsync`, `ReadPresetAsync`, `DeleteAsync`, `RenameAsync`, `ListPresetsAsync`) and the faithful `FakePresetDevice`.

**Why temp names (PROTOCOL.md):** `save` targets the slot whose **name** matches the saved name. During a shuffle two slots could transiently want the same final name, making the save land in the wrong slot. Writing every affected slot under a distinct temporary name first, then renaming to finals (a name-only `dwrite chunk:-1` that never triggers save-by-name), removes the ambiguity.

## Public API defined by this plan

```csharp
namespace Sonulab.Core.Services;

public static class SlotPlanner {
    // occupants[i] = -1 for an empty slot, else a stable id (the item's CURRENT slot index).
    // Move the item at `from` to position `to` (remove + insert); array length is preserved.
    public static int[] Move(int[] occupants, int from, int to);
    // Inclusive slot range that may change for a from->to move.
    public static (int Min, int Max) ChangedRange(int from, int to);
}

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService {
    public ReorderService(DeviceRepository repo);
    // Move the preset at slot `from` to slot `to`. Atomic: verifies each write; on any failure
    // restores the affected range from the pre-move snapshot, then rethrows.
    public Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default);
}
```

## File structure

```
src/Sonulab.Core/Services/
  SlotPlanner.cs        (create — pure)
  ReorderService.cs     (create — ReorderProgress + ReorderService)
tests/Sonulab.Core.Tests/
  SlotPlannerTests.cs
  ReorderServiceTests.cs
docs/
  HARDWARE-VALIDATION-plan3b.md   (create — guarded reorder checklist, Task 5)
tools/HwCheck/Program.cs          (modify — add --reorder-test mode, Task 5)
```

---

### Task 1: SlotPlanner (pure list reorder)

**Files:** Create `src/Sonulab.Core/Services/SlotPlanner.cs`; Test `tests/Sonulab.Core.Tests/SlotPlannerTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/SlotPlannerTests.cs`:
```csharp
using Sonulab.Core.Services;
using Xunit;

public class SlotPlannerTests
{
    [Fact] public void Move_down_shifts_intervening_items_up()
    {
        // ids 0..4 in slots 0..4; move slot 1 -> slot 3
        var result = SlotPlanner.Move(new[] { 0, 1, 2, 3, 4 }, from: 1, to: 3);
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, result);
    }

    [Fact] public void Move_up_shifts_intervening_items_down()
    {
        var result = SlotPlanner.Move(new[] { 0, 1, 2, 3, 4 }, from: 3, to: 1);
        Assert.Equal(new[] { 0, 3, 1, 2, 4 }, result);
    }

    [Fact] public void Move_preserves_length_and_handles_empty_slots()
    {
        // -1 = empty; move slot 0 -> slot 2
        var result = SlotPlanner.Move(new[] { 5, -1, 7, -1 }, from: 0, to: 2);
        Assert.Equal(4, result.Length);
        Assert.Equal(new[] { -1, 7, 5, -1 }, result);
    }

    [Fact] public void Move_to_same_index_is_identity()
    {
        Assert.Equal(new[] { 0, 1, 2 }, SlotPlanner.Move(new[] { 0, 1, 2 }, 1, 1));
    }

    [Fact] public void ChangedRange_is_inclusive_min_max()
    {
        Assert.Equal((1, 3), SlotPlanner.ChangedRange(1, 3));
        Assert.Equal((1, 3), SlotPlanner.ChangedRange(3, 1));
        Assert.Equal((2, 2), SlotPlanner.ChangedRange(2, 2));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SlotPlannerTests`
Expected: FAIL — `SlotPlanner` does not exist.

- [ ] **Step 3: Implement SlotPlanner**

`src/Sonulab.Core/Services/SlotPlanner.cs`:
```csharp
namespace Sonulab.Core.Services;

public static class SlotPlanner
{
    public static int[] Move(int[] occupants, int from, int to)
    {
        var list = new List<int>(occupants);
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        return list.ToArray();
    }

    public static (int Min, int Max) ChangedRange(int from, int to) =>
        from <= to ? (from, to) : (to, from);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SlotPlannerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): SlotPlanner pure list reorder + changed range"
```

---

### Task 2: ReorderService.MoveAsync (happy path)

**Files:** Create `src/Sonulab.Core/Services/ReorderService.cs`; Test `tests/Sonulab.Core.Tests/ReorderServiceTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/ReorderServiceTests.cs`:
```csharp
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class ReorderServiceTests
{
    static (DeviceRepository repo, FakePresetDevice dev) Seed()
    {
        var dev = new FakePresetDevice();
        // 4 presets in slots 0..3, content tagged so we can tell them apart
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        return (new DeviceRepository(new SonuClient(dev)), dev);
    }

    static async Task<string[]> Names(DeviceRepository repo) =>
        (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();

    [Fact] public async Task Move_down_reorders_names_in_order()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 1, to: 3);
        var names = await Names(repo);
        Assert.Equal("A", names[0]);
        Assert.Equal("C", names[1]);
        Assert.Equal("D", names[2]);
        Assert.Equal("B", names[3]);     // B moved from slot 1 to slot 3
    }

    [Fact] public async Task Move_carries_content_with_the_preset()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 1, to: 3);
        var slot3 = await repo.ReadPresetAsync(3);
        Assert.Equal("\"mB\"", slot3.GetValueJson(@"root\app\amp\amp"));   // B's content followed B
        var slot1 = await repo.ReadPresetAsync(1);
        Assert.Equal("\"mC\"", slot1.GetValueJson(@"root\app\amp\amp"));   // C shifted up into slot 1
    }

    [Fact] public async Task Move_up_reorders_correctly()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 3, to: 0);
        Assert.Equal(new[] { "D", "A", "B", "C" }, (await Names(repo))[..4]);
    }

    [Fact] public async Task Same_index_move_is_noop()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(2, 2);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(repo))[..4]);
    }

    [Fact] public async Task Reports_progress()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        var seen = new List<ReorderProgress>();
        await new ReorderService(repo).MoveAsync(1, 3, new Progress<ReorderProgress>(p => { lock (seen) seen.Add(p); }));
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.True(p.Done <= p.Total));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReorderServiceTests`
Expected: FAIL — `ReorderService`/`ReorderProgress` do not exist.

- [ ] **Step 3: Implement ReorderService**

`src/Sonulab.Core/Services/ReorderService.cs`:
```csharp
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService
{
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        if (from == to) return;

        var slots = await _repo.ListPresetsAsync(ct);
        var occupants = new int[slots.Count];
        for (int i = 0; i < slots.Count; i++) occupants[i] = slots[i].IsEmpty ? -1 : i;

        var target = SlotPlanner.Move(occupants, from, to);
        var (min, max) = SlotPlanner.ChangedRange(from, to);

        // Snapshot the affected occupied slots (name + content) for both the write source and rollback.
        var snap = new Dictionary<int, (string Name, PresetDocument Doc)>();
        for (int i = min; i <= max; i++)
            if (occupants[i] != -1)
                snap[i] = (slots[i].Name, await _repo.ReadPresetAsync(i, ct));

        try
        {
            await WriteRangeAsync(target, snap, min, max, progress, ct);
        }
        catch
        {
            // Roll back: restore the original arrangement (identity over the affected range).
            await WriteRangeAsync(occupants, snap, min, max, null, ct);
            throw;
        }
    }

    // Writes `arrangement[slot]` content into each slot in [min,max] using temporary unique names,
    // then sets the final names. arrangement[slot] is -1 (empty) or a snapshot key (source slot id).
    private async Task WriteRangeAsync(
        int[] arrangement, Dictionary<int, (string Name, PresetDocument Doc)> snap,
        int min, int max, IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        int total = (max - min + 1) * 2;
        int done = 0;

        // Phase 1: content under temporary unique names (or delete for empties).
        for (int slot = min; slot <= max; slot++)
        {
            ct.ThrowIfCancellationRequested();
            int src = arrangement[slot];
            if (src == -1)
            {
                await _repo.DeleteAsync(slot, ct);
            }
            else
            {
                var (_, doc) = snap[src];
                await _repo.WritePresetToSlotAsync(slot, TempName(slot), doc, verify: true, ct);
            }
            progress?.Report(new ReorderProgress(++done, total, $"slot {slot + 1}: content"));
        }

        // Phase 2: final names (name-only; never triggers save-by-name, so duplicates are impossible here).
        for (int slot = min; slot <= max; slot++)
        {
            ct.ThrowIfCancellationRequested();
            int src = arrangement[slot];
            if (src != -1)
                await _repo.RenameAsync(slot, snap[src].Name, ct);
            progress?.Report(new ReorderProgress(++done, total, $"slot {slot + 1}: name"));
        }
    }

    private static string TempName(int slot) => $"__sstmp_{slot}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReorderServiceTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ReorderService.MoveAsync (snapshot + temp-name shuffle + finalize)"
```

---

### Task 3: Rollback on failure

**Files:** Test `tests/Sonulab.Core.Tests/ReorderServiceTests.cs` (add a test); no production change expected (the `try/catch` in Task 2 already rolls back) — this task proves it.

- [ ] **Step 1: Write the failing test** (add to `ReorderServiceTests`)
```csharp
    // A device that fails the Nth save lets us prove rollback restores the original arrangement.
    sealed class FailAfterNSaves : FakePresetDevice
    {
        private readonly int _n; private int _saves;
        public FailAfterNSaves(int n) => _n = n;
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\"") && ++_saves > _n)
                throw new System.IO.IOException("simulated write failure");
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Rollback_restores_original_on_failure()
    {
        var dev = new FailAfterNSaves(1);   // allow the first slot's save, fail the next
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(repo).MoveAsync(1, 3));

        // After rollback the original A,B,C,D order and content are intact.
        var slots = await repo.ListPresetsAsync();
        Assert.Equal(new[] { "A", "B", "C", "D" }, slots.Take(4).Select(s => s.Name).ToArray());
        Assert.Equal("\"mB\"", (await repo.ReadPresetAsync(1)).GetValueJson(@"root\app\amp\amp"));
    }
```
> Note: rollback itself issues saves through the same device. `FailAfterNSaves` only fails the *forward* pass (its counter keeps incrementing, so rollback saves would also throw). To let rollback succeed, make the failure one-shot: after throwing once, allow subsequent saves. Update the override:
```csharp
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\"") && ++_saves == _n + 1)
                throw new System.IO.IOException("simulated write failure");
            return base.SendAsync(command, ct);
        }
```
(Throws exactly once — on the (_n+1)th save — then rollback's saves proceed.)

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test --filter ReorderServiceTests`
Expected: PASS if the Task 2 rollback is correct. If it FAILS (state not restored), fix `MoveAsync`'s catch block to fully restore the range, then re-run. (Do not weaken the test.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(core): ReorderService rollback restores original on write failure"
```

---

### Task 4: Guard against out-of-range / invalid moves

**Files:** Modify `src/Sonulab.Core/Services/ReorderService.cs`; Test `tests/Sonulab.Core.Tests/ReorderServiceTests.cs`.

- [ ] **Step 1: Write the failing tests** (add to `ReorderServiceTests`)
```csharp
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 30)]
    [InlineData(31, 1)]
    public async Task Out_of_range_indices_throw(int from, int to)
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ReorderService(repo).MoveAsync(from, to));
    }

    [Fact] public async Task Moving_an_empty_slot_throws()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();   // slots 4..29 are empty
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReorderService(repo).MoveAsync(10, 0));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReorderServiceTests`
Expected: FAIL — no guards yet (out-of-range indexing throws the wrong type or empties move silently).

- [ ] **Step 3: Add the guards** at the top of `MoveAsync`, right after `if (from == to) return;`
```csharp
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
```
Then DELETE the now-duplicate `var slots = await _repo.ListPresetsAsync(ct);` line that previously followed (there must be exactly one `ListPresetsAsync` call — keep this guarded one, remove the later duplicate).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReorderServiceTests`
Expected: PASS — all ReorderService tests (happy path, rollback, guards) green.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — all tests across Plans 1/2/3a/3b green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): ReorderService validates indices and refuses to move empty slots"
```

---

### Task 5: Guarded hardware reorder validation

**Files:** Modify `tools/HwCheck/Program.cs` (add `--reorder-test`); Create `docs/HARDWARE-VALIDATION-plan3b.md`. Operator runs it with VoidX closed; it reorders two presets and then **reorders them back**, leaving the device as it started.

- [ ] **Step 1: Add a `--reorder-test` mode to the harness**

In `tools/HwCheck/Program.cs`, after the `--write-test` handling (or as a sibling branch), add:
```csharp
if (Array.IndexOf(args, "--reorder-test") >= 0)
{
    Console.WriteLine("\n--- GUARDED REORDER TEST (moves a preset and moves it back) ---");
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); return 3; }
    var svc = new ReorderService(repo);

    var before = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    int from = Array.FindIndex(before, n => !string.IsNullOrEmpty(n));
    int to = Array.FindLastIndex(before, n => !string.IsNullOrEmpty(n));
    if (from == to) { Console.WriteLine("need >=2 presets; abort."); return 3; }
    Console.WriteLine($"moving idx {from} ('{before[from]}') -> idx {to}, then back...");

    await svc.MoveAsync(from, to, new Progress<ReorderProgress>(p => Console.WriteLine($"   [{p.Done}/{p.Total}] {p.Message}")));
    var moved = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    Console.WriteLine(moved[to] == before[from] ? $"  OK: '{before[from]}' now at idx {to}" : "  FAIL: move");

    await svc.MoveAsync(to, from);   // move it back
    var restored = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    bool ok = restored.SequenceEqual(before);
    Console.WriteLine(ok ? "  OK: order restored to original" : "  FAIL: not restored");
    session.Disconnect();
    Console.WriteLine(ok ? "RESULT: REORDER-TEST PASS" : "RESULT: REORDER-TEST FAIL");
    return ok ? 0 : 4;
}
```
Ensure `using Sonulab.Core.Services;` is present (it is, from Plan 3a). This branch must run BEFORE the final read-only `return 0;` — place it alongside the `--write-test` block.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded.`

- [ ] **Step 3: Write the checklist doc**

Create `docs/HARDWARE-VALIDATION-plan3b.md`:
```markdown
# Plan 3b — Manual Hardware Validation (guarded reorder)

VoidX-Control CLOSED. The harness moves a preset to another slot, then moves it back, so the
device ends as it started. (Each move replays ~157 params per shifted slot — expect tens of seconds.)

Run: `dotnet run --project tools/HwCheck -- --reorder-test`

Expect:
1. Connect + Compatibility=Tested, writesAllowed=true.
2. Progress lines per slot ("content"/"name").
3. "now at idx T": the moved preset is at the target slot.
4. "order restored to original": after moving back, names match the original list exactly.

Record pass/fail and the wall-clock time. If order isn't restored, capture output and STOP.
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test/docs: Plan 3b guarded hardware reorder harness + checklist"
```

- [ ] **Step 5: (Operator) run `dotnet run --project tools/HwCheck -- --reorder-test`** with VoidX closed; confirm move + restore, and record results.

---

## Self-review notes
- **Spec coverage (design spec §5–6, reorder):** pure arrangement → Task 1; atomic move with temp-name shuffle + verify → Task 2; rollback → Task 3; validation/empties → Task 4; hardware → Task 5. Reorder builds entirely on Plan 3a's `WritePresetToSlotAsync` (which read-back-verifies each slot) — honoring its documented name-uniqueness precondition via `TempName`.
- **Placeholder scan:** none — all code complete. Task 3's note refines the test's fail-once device so rollback can complete; Task 4 explicitly removes the duplicate `ListPresetsAsync` line introduced by the guard.
- **Type consistency:** `SlotPlanner.Move/ChangedRange` (Task 1) feed `ReorderService.MoveAsync` (Task 2). `ReorderProgress` is reported in Task 2 and consumed in Task 5. `WriteRangeAsync` is reused for both apply and rollback with the same `snap` map. Occupant ids are slot indices, so snapshot lookups in `[min,max]` always resolve (a from→to move only relocates ids within that range).
- **Safety:** every slot write verifies by read-back (via `WritePresetToSlotAsync`); any failure triggers a full restore of the affected range from the pre-move snapshot before rethrowing; the hardware test self-restores by moving back.
```
