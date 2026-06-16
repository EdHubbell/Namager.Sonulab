# Drag-to-Reorder + Select+Save Engine Implementation Plan (Feature B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make reorder ~55× faster by rewriting `ReorderService` to use the **select+save** mechanism (~216 ms/slot, device copies content internally), and let the user **drag a preset** to a new slot with an insertion-line **drop indicator**.

**Architecture:** A drag from→to is a **rotation** of the contiguous range `[min,max]`. The new engine applies it in place with **one empty temp slot**: stage the displaced preset, shift the range via `select(name)` + `save(tempName)` (each save = the device copying 8 KB internally), then finalize names. The proven param-replay path is kept as a **fallback** when no empty temp slot exists. Backup-of-range + read-back verify + rollback are retained. The UI adds drag-drop with an insertion adorner; the up/down buttons stay.

**Tech Stack:** .NET 10, Avalonia 12, xUnit. Builds on `SlotPlanner`, `ReorderService`, `DeviceRepository` (`SelectPresetAsync`, `SaveCurrentAsAsync`, `RenameAsync`, `DeleteAsync`, `ReadPresetAsync`, `ListPresetsAsync`), `PresetListViewModel`, and the faithful `FakePresetDevice` (models select=load-live, save-by-name=write-slot, dwrite chunk:-1=rename).

**Spec:** `docs/superpowers/specs/2026-06-16-drag-reorder-design.md`. **Probe-confirmed:** no native list-write reorder; `select→save` ≈ 216 ms; param-replay ≈ 12 000 ms.

---

## Engine algorithm (rotation via select+save, one temp slot)

Moving **up** (`from > to`); moving **down** mirrors it. `origName[k]` = each slot's original name;
`TEMP_PREFIX = "__sstmp_"`; `STAGE`/`DST(k)` are unique names under that prefix.

```
1. select(origName[from]); rename(temp, STAGE);   save(STAGE)        // displaced preset -> temp slot
2. for k = from downTo to+1:
     select(origName[k-1]); rename(k, DST(k));     save(DST(k))       // slot k <- (k-1)'s content
3. select(STAGE);          rename(to, DST(to));    save(DST(to))      // slot to  <- displaced preset
4. delete(temp)                                                       // clear staging slot
5. finalize names (dwrite chunk:-1, no content move):
     rename(to, origName[from]); for k=to+1..from: rename(k, origName[k-1])
```
Why correct: in step 2 each source `k-1` is read (selected) before it's overwritten (it's overwritten one iteration later as the next dest). Sources always carry their **original** names (we only ever rename *destinations* to temp names, and dests are higher than their sources in the top-down walk), so `select(origName[k-1])` is unambiguous. Destinations get **unique** temp names (collision-guarded by `TEMP_PREFIX`), so save-by-name never lands in the wrong slot. Final names are a permutation of the originals → unique. Saves = `|from-to|+2`.

Temp slot = first empty slot **outside** `[min,max]`. If none exists, fall back to the retained
param-replay engine (`WriteRangeViaReplay`) — correct, just slow. Backup/verify/rollback unchanged.

---

## Public API (unchanged signature; internals rewritten)

```csharp
namespace Sonulab.Core.Services;
public sealed class ReorderService {
    public ReorderService(DeviceRepository repo);
    public Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default);
}
namespace Sonulab.App.ViewModels; // PresetListViewModel gains:
//   public IAsyncRelayCommand<(int from,int to)> MoveToCommand   (drop handler)
```

## File structure
```
src/Sonulab.Core/Services/ReorderService.cs            (rewrite: select+save rotation primary, param-replay fallback)
src/Sonulab.App/ViewModels/PresetListViewModel.cs      (add MoveToAsync(from,to))
src/Sonulab.App/Views/PresetListView.axaml(.cs)        (drag-drop + insertion-line indicator)
src/Sonulab.App/Behaviors/PresetDropIndicator.cs       (adorner/line for the drop position)
tests/Sonulab.Core.Tests/ReorderServiceTests.cs        (extend for the new engine)
tests/Sonulab.App.Tests/PresetListViewModelTests.cs    (add MoveTo mapping/gating)
docs/HARDWARE-VALIDATION-plan-dragreorder.md           (deferred operator checklist)
```

---

### Task 1: Rewrite `ReorderService` to the select+save rotation engine

**Files:** Modify `src/Sonulab.Core/Services/ReorderService.cs`; Modify `tests/Sonulab.Core.Tests/ReorderServiceTests.cs`.

Keep `MoveAsync`'s guards, backup, verify, rollback, and `AggregateException` behavior from Plan 3b.
Replace the `WriteRangeAsync` (which used `WritePresetToSlotAsync` param-replay) with: a primary
`RotateViaSelectSaveAsync` and a fallback `WriteRangeViaReplayAsync` (the old logic), chosen by
temp-slot availability. `SlotPlanner` is unchanged.

- [ ] **Step 1: Write the failing tests** — replace the body of `ReorderServiceTests`'s happy-path
  region with engine-agnostic order+content assertions, and add temp-slot/fallback coverage.

`tests/Sonulab.Core.Tests/ReorderServiceTests.cs` (full replacement):
```csharp
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class ReorderServiceTests
{
    static FakePresetDevice Dev(int used = 4)
    {
        var d = new FakePresetDevice();
        var tag = new[] { "mA", "mB", "mC", "mD", "mE", "mF" };
        var nm = new[] { "A", "B", "C", "D", "E", "F" };
        for (int i = 0; i < used; i++)
            d.SeedSlot(i, nm[i], new[] { $@"root\app\amp\amp:{{""value"":""{tag[i]}""}}" });
        return d;
    }
    static DeviceRepository Repo(FakePresetDevice d) => new(new SonuClient(d));
    static async Task<string[]> Names(DeviceRepository r) => (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
    static async Task<string?> Amp(DeviceRepository r, int i) => (await r.ReadPresetAsync(i)).GetValueJson(@"root\app\amp\amp");

    [Fact] public async Task Move_up_rotates_order_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(from: 3, to: 1);   // D up to slot 1
        Assert.Equal(new[] { "A", "D", "B", "C" }, (await Names(r))[..4]);
        Assert.Equal("\"mD\"", await Amp(r, 1));   // content followed the name
        Assert.Equal("\"mB\"", await Amp(r, 2));
        Assert.Equal("\"mC\"", await Amp(r, 3));
    }

    [Fact] public async Task Move_down_rotates_order_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(from: 0, to: 2);   // A down to slot 2
        Assert.Equal(new[] { "B", "C", "A", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mA\"", await Amp(r, 2));
        Assert.Equal("\"mB\"", await Amp(r, 0));
    }

    [Fact] public async Task Same_index_is_noop()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(2, 2);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
    }

    [Fact] public async Task Fallback_when_no_empty_temp_slot_still_reorders()
    {
        var d = Dev(used: 30); await d.OpenAsync(); var r = Repo(d);  // full device -> no temp slot
        await new ReorderService(r).MoveAsync(2, 0);
        var names = await Names(r);
        Assert.Equal("C", names[0]); Assert.Equal("A", names[1]); Assert.Equal("B", names[2]);
    }

    [Theory]
    [InlineData(-1, 2)] [InlineData(0, 30)] [InlineData(31, 1)]
    public async Task Out_of_range_throws(int from, int to)
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ReorderService(r).MoveAsync(from, to));
    }

    [Fact] public async Task Moving_empty_slot_throws()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);   // slots 4..29 empty
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReorderService(r).MoveAsync(10, 0));
    }

    sealed class FailOnceOnSave : FakePresetDevice
    {
        private readonly int _n; private int _saves; public bool Fired;
        public FailOnceOnSave(int n) => _n = n;
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\"")) { _saves++; if (!Fired && _saves == _n) { Fired = true; throw new System.IO.IOException("fail"); } }
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Rollback_restores_original_on_save_failure()
    {
        var d = new FailOnceOnSave(2);
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        d.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        d.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(r).MoveAsync(3, 0));
        Assert.True(d.Fired);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mB\"", await Amp(r, 1));
    }

    [Fact] public async Task Reports_progress()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        var seen = new List<ReorderProgress>();
        await new ReorderService(r).MoveAsync(3, 1, new Progress<ReorderProgress>(p => { lock (seen) seen.Add(p); }));
        Assert.NotEmpty(seen);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReorderServiceTests`
Expected: FAIL — the new content-assertions/fallback aren't satisfied by the old engine yet (or compile errors from removed helpers).

- [ ] **Step 3: Rewrite `ReorderService`**

Replace `src/Sonulab.Core/Services/ReorderService.cs`:
```csharp
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService
{
    private const string TempPrefix = "__sstmp_";
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
        if (slots.Any(s => s.Name.StartsWith(TempPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"A preset name uses the reserved prefix '{TempPrefix}'; rename it before reordering.");

        var (min, max) = SlotPlanner.ChangedRange(from, to);
        var origName = slots.Select(s => s.Name).ToArray();

        // Backup the affected range for rollback.
        var backup = new Dictionary<int, (string Name, PresetDocument Doc)>();
        for (int i = min; i <= max; i++)
            if (!slots[i].IsEmpty) backup[i] = (origName[i], await _repo.ReadPresetAsync(i, ct));

        // Temp slot: an empty slot OUTSIDE the affected range.
        int temp = -1;
        for (int i = 0; i < slots.Count; i++)
            if ((i < min || i > max) && slots[i].IsEmpty) { temp = i; break; }

        try
        {
            if (temp >= 0) await RotateViaSelectSaveAsync(origName, from, to, min, max, temp, progress, ct);
            else await WriteRangeViaReplayAsync(origName, backup, from, to, min, max, progress, ct);
        }
        catch (Exception original)
        {
            try { await RestoreRangeAsync(origName, backup, min, max, ct); }
            catch (Exception rb) { throw new AggregateException("Reorder failed and rollback also failed; device may be inconsistent.", original, rb); }
            throw;
        }
    }

    // FAST PATH: rotate [min,max] in place using select+save and one temp slot.
    private async Task RotateViaSelectSaveAsync(
        string[] origName, int from, int to, int min, int max, int temp,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        int span = max - min;          // number of shifts
        int total = span + 2; int done = 0;
        string Stage = TempPrefix + "stage";
        string Dst(int k) => TempPrefix + "d" + k;

        async Task Move(string sourceName, int destSlot, string destName)
        {
            ct.ThrowIfCancellationRequested();
            await _repo.SelectPresetAsync(sourceName, ct);    // load source content into live
            await _repo.RenameAsync(destSlot, destName, ct);  // name the dest so save targets it
            await _repo.SaveCurrentAsAsync(destName, ct);     // device copies content into destSlot
        }

        if (from > to)   // moving up
        {
            await Move(origName[from], temp, Stage); progress?.Report(new ReorderProgress(++done, total, "stage"));
            for (int k = from; k > to; k--) { await Move(origName[k - 1], k, Dst(k)); progress?.Report(new ReorderProgress(++done, total, $"slot {k + 1}")); }
            await Move(Stage, to, Dst(to)); progress?.Report(new ReorderProgress(++done, total, $"slot {to + 1}"));
            await _repo.DeleteAsync(temp, ct);
            await _repo.RenameAsync(to, origName[from], ct);
            for (int k = to + 1; k <= from; k++) await _repo.RenameAsync(k, origName[k - 1], ct);
        }
        else             // moving down
        {
            await Move(origName[from], temp, Stage); progress?.Report(new ReorderProgress(++done, total, "stage"));
            for (int k = from; k < to; k++) { await Move(origName[k + 1], k, Dst(k)); progress?.Report(new ReorderProgress(++done, total, $"slot {k + 1}")); }
            await Move(Stage, to, Dst(to)); progress?.Report(new ReorderProgress(++done, total, $"slot {to + 1}"));
            await _repo.DeleteAsync(temp, ct);
            await _repo.RenameAsync(to, origName[from], ct);
            for (int k = from; k < to; k++) await _repo.RenameAsync(k, origName[k + 1], ct);
        }
    }

    // FALLBACK (no temp slot): the proven param-replay write-to-slot, snapshot-based + temp names.
    private async Task WriteRangeViaReplayAsync(
        string[] origName, Dictionary<int, (string Name, PresetDocument Doc)> backup,
        int from, int to, int min, int max, IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var occupants = new int[origName.Length];
        for (int i = 0; i < origName.Length; i++) occupants[i] = string.IsNullOrEmpty(origName[i]) ? -1 : i;
        var target = SlotPlanner.Move(occupants, from, to);
        int total = (max - min + 1) * 2; int done = 0;
        for (int s = min; s <= max; s++)
        {
            int src = target[s];
            if (src == -1) await _repo.DeleteAsync(s, ct);
            else await _repo.WritePresetToSlotAsync(s, TempPrefix + s, backup[src].Doc, verify: true, ct);
            progress?.Report(new ReorderProgress(++done, total, $"slot {s + 1}: content"));
        }
        for (int s = min; s <= max; s++)
        {
            int src = target[s];
            if (src != -1) await _repo.RenameAsync(s, backup[src].Name, ct);
            progress?.Report(new ReorderProgress(++done, total, $"slot {s + 1}: name"));
        }
    }

    private async Task RestoreRangeAsync(
        string[] origName, Dictionary<int, (string Name, PresetDocument Doc)> backup, int min, int max, CancellationToken ct)
    {
        // Rewrite each affected slot to its ORIGINAL content+name from the backup (param-replay, robust).
        for (int s = min; s <= max; s++)
            if (backup.TryGetValue(s, out var b)) await _repo.WritePresetToSlotAsync(s, TempPrefix + "r" + s, b.Doc, verify: false, ct);
            else await _repo.DeleteAsync(s, ct);
        for (int s = min; s <= max; s++)
            if (backup.TryGetValue(s, out var b)) await _repo.RenameAsync(s, b.Name, ct);
    }
}
```
> Notes: the fast path needs `backup` only for rollback (not for the move itself). The fallback and
> rollback reuse `WritePresetToSlotAsync` (Plan 3a) which read-back-verifies. `SlotPlanner` is
> untouched. The `FakePresetDevice` models select/save/rename so all paths run in tests.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReorderServiceTests`
Expected: PASS (all cases: up/down rotation content+order, no-empty fallback, guards, rollback, progress).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ReorderService select+save rotation engine (~55x faster) + param-replay fallback"
```

---

### Task 2: `PresetListViewModel.MoveToAsync(from,to)` for drop

**Files:** Modify `src/Sonulab.App/ViewModels/PresetListViewModel.cs`; Modify `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`.

- [ ] **Step 1: Write the failing test** (add to `PresetListViewModelTests`)
```csharp
    [Fact] public async Task MoveTo_reorders_via_service()
    {
        var (vm, _) = Make();                 // existing helper seeds A,B,C in slots 0..2
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveToCommand.ExecuteAsync((0, 2));   // A -> slot 2
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("C", vm.Items[1].Name);
        Assert.Equal("A", vm.Items[2].Name);
    }

    [Fact] public async Task MoveTo_is_gated_when_writes_disallowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveToCommand.ExecuteAsync((0, 1));
        Assert.Equal("A", vm.Items[0].Name);   // unchanged
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter PresetListViewModelTests`
Expected: FAIL — `MoveToCommand` not defined.

- [ ] **Step 3: Add the command** to `PresetListViewModel` (alongside MoveUp/MoveDown; reuse `RunAsync`)
```csharp
    [RelayCommand]
    private async Task MoveToAsync((int from, int to) move)
    {
        if (move.from == move.to) return;
        if (move.from < 0 || move.from >= Items.Count) return;
        if (Items[move.from].IsEmpty) return;
        int clampedTo = Math.Clamp(move.to, 0, Items.Count - 1);
        if (await RunAsync($"Moving '{Items[move.from].Name}'…", () => _reorder.MoveAsync(move.from, clampedTo)) )
            { /* reloaded by RunAsync */ }
    }
```
(If `RunAsync` returns `Task` not `Task<bool>`, just `await` it; the `if` is harmless either way — match the existing signature.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter PresetListViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): PresetListViewModel.MoveToAsync(from,to) for drag-drop reorder"
```

---

### Task 3: Drag-drop + insertion-line drop indicator (build-verified)

**Files:** Modify `src/Sonulab.App/Views/PresetListView.axaml(.cs)`; Create `src/Sonulab.App/Behaviors/PresetDropIndicator.cs`. No unit test (UI); verified by build + (deferred) running the app.

- [ ] **Step 1: Implement drag-drop in the list code-behind**

In `src/Sonulab.App/Views/PresetListView.axaml`, on the `ListBox` add `DragDrop.AllowDrop="True"` and
`x:Name="PresetList"`. In `PresetListView.axaml.cs`, wire pointer-drag to start a drag carrying the
source index, handle `DragOver` to compute+show the insertion index, and `Drop` to invoke the VM:
```csharp
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class PresetListView : UserControl
{
    private int _dragFrom = -1;
    public PresetListView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is PresetItemViewModel item && !item.IsEmpty)
        {
            _dragFrom = item.Index;
            var data = new DataObject();
            data.Set("preset-index", item.Index);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
    }

    private int IndexAt(DragEventArgs e)
    {
        var list = this.FindControl<ListBox>("PresetList")!;
        for (int i = 0; i < list.ItemCount; i++)
            if (list.ContainerFromIndex(i) is Control row)
            {
                var p = e.GetPosition(row);
                if (p.Y >= 0 && p.Y <= row.Bounds.Height)
                    return p.Y < row.Bounds.Height / 2 ? i : i + 1;   // before/after this row
            }
        return list.ItemCount;   // past the end
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains("preset-index") ? DragDropEffects.Move : DragDropEffects.None;
        PresetDropIndicator.Show(this.FindControl<ListBox>("PresetList")!, IndexAt(e));
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        PresetDropIndicator.Hide();
        if (_dragFrom < 0 || DataContext is not PresetListViewModel vm) return;
        int insert = IndexAt(e);
        int to = insert > _dragFrom ? insert - 1 : insert;     // account for removal shift
        if (to != _dragFrom && vm.MoveToCommand.CanExecute((_dragFrom, to)))
            vm.MoveToCommand.Execute((_dragFrom, to));
        _dragFrom = -1;
        e.Handled = true;
    }
}
```
In the `ListBox.ItemTemplate`'s root element, add `PointerPressed="OnItemPointerPressed"`.

- [ ] **Step 2: Implement the insertion-line indicator**

`src/Sonulab.App/Behaviors/PresetDropIndicator.cs` — a simple adorner line drawn between rows:
```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Sonulab.App.Behaviors;

// Draws a 2px insertion line in the ListBox's adorner layer at the given item index.
public static class PresetDropIndicator
{
    private static Rectangle? _line;

    public static void Show(ListBox list, int index)
    {
        var layer = AdornerLayer.GetAdornerLayer(list);
        if (layer is null) return;
        _line ??= new Rectangle { Height = 2, Fill = Brushes.DodgerColor() ?? Brushes.DodgerBlue, IsHitTestVisible = false };
        if (_line.Parent is null) layer.Children.Add(_line);

        double y;
        if (index <= 0 && list.ContainerFromIndex(0) is Control first) y = first.Bounds.Y;
        else if (list.ContainerFromIndex(index) is Control at) y = at.Bounds.Y;
        else if (list.ContainerFromIndex(index - 1) is Control prev) y = prev.Bounds.Y + prev.Bounds.Height;
        else y = 0;

        _line.Width = list.Bounds.Width;
        Canvas.SetLeft(_line, 0);
        Canvas.SetTop(_line, y);
    }

    public static void Hide()
    {
        if (_line?.Parent is Panel p) p.Children.Remove(_line);
    }
}
```
> Note: `Brushes.DodgerColor()` is a placeholder — use `Brushes.DodgerBlue` (or a theme accent brush
> `(IBrush)Application.Current!.FindResource("SystemAccentColor")` wrapped in a SolidColorBrush). The
> adorner layer positions children by their `Canvas.Left/Top`. If `AdornerLayer` child positioning
> differs in Avalonia 12, position via a `Canvas` overlay in the view instead — the executor verifies
> visually. Keep it a thin horizontal line spanning the list width at the computed Y.

- [ ] **Step 3: Build**

Run: `dotnet build src/Sonulab.App`
Expected: `Build succeeded.` Fix any Avalonia 12 DragDrop/AdornerLayer API mismatches until clean.

- [ ] **Step 4: Full suite**

Run: `dotnet test`
Expected: PASS — Core + App (drag UI has no unit tests; engine + VM are covered).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): drag-to-reorder presets with an insertion-line drop indicator"
```

---

### Task 4: Deferred hardware validation checklist

**Files:** Create `docs/HARDWARE-VALIDATION-plan-dragreorder.md`. No code.

- [ ] **Step 1: Write the checklist**
```markdown
# Drag-Reorder — Manual Hardware Validation (DEFERRED until operator at PC, VoidX closed)

1. Fast engine on hardware: `dotnet run --project tools/HwCheck -- --reorder-test`
   — confirm move + move-back restores order; note the round-trip time (expect SECONDS now, not minutes).
2. Full-range timing: optionally extend the harness to move idx 0 -> a far slot and back; confirm
   ~seconds and order restored.
3. Drag UI: `dotnet run --project src/Sonulab.App`, Connect, drag a preset up/down — confirm the
   insertion line shows between the right two presets, and on drop the order updates (with progress).
4. No-empty fallback: only if you ever fill all 30 slots — reorder still works (slower).
Record pass/fail + observed timings.
```

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: deferred hardware validation checklist for drag-reorder"
```

- [ ] **Step 3: (Operator, later)** run the checklist when back at the PC with VoidX closed.

---

## Self-review notes
- **Spec coverage:** select+save engine → Task 1 (with retained param-replay fallback); drop command → Task 2; drag UI + insertion indicator → Task 3; hardware re-validation → Task 4 (deferred). Backup/verify/rollback + temp-name collision guard retained from Plan 3b.
- **Placeholder scan:** the only intentionally-open spots are Task 3's UI specifics (drop-indicator brush + AdornerLayer-vs-Canvas positioning), explicitly flagged for the executor to verify visually — the engine + VM (Tasks 1–2) are fully coded and unit-tested.
- **Type consistency:** `MoveAsync(from,to,progress,ct)` signature unchanged; `SlotPlanner.Move/ChangedRange` reused; `MoveToCommand` takes `(int,int)`; `WritePresetToSlotAsync`/`SelectPresetAsync`/`SaveCurrentAsAsync`/`RenameAsync`/`DeleteAsync` are the existing `DeviceRepository` methods. `FakePresetDevice` models all of them, so the engine's correctness (order+content) is genuinely tested offline.
- **Deferred (operator away):** all on-hardware + visual checks (Task 4 + Task 3 Step's eyeball).
