# Per-Row Reorder Buttons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add up/down buttons to the right of each non-empty preset row that move the preset one physical slot, using the cheapest possible device operation, and disable the list while a move runs.

**Architecture:** A new `ReorderService.MoveStepAsync(from, up)` handles single-step moves: it delegates an *occupied-neighbour* move to the existing 3-copy adjacent-swap fast path, and handles an *empty-neighbour* move with a new 1-copy `select`+`save` relocate (instead of the slow ~12 s param-replay). The view model exposes per-row `MoveItemUp/DownCommand`; `PresetItemViewModel` gains `CanMoveUp`/`CanMoveDown` flags for boundary disabling; the XAML row template docks two compact chevron buttons on the right, and the `ListBox` is disabled while `IsBusy`.

**Tech Stack:** .NET 10, Avalonia 12 (built-in FluentTheme), CommunityToolkit.Mvvm, xUnit, `FakePresetDevice` in-memory test double.

**Design reference:** `docs/superpowers/specs/2026-06-16-per-row-reorder-buttons-design.md`

**Key facts already verified against the codebase:**
- `ReorderService` (`src/Sonulab.Core/Services/ReorderService.cs`) already has `MoveAsync(from, to, progress, ct)`, a `TempPrefix = "__sstmp_"` guard, the `RotateViaSelectSaveAsync` fast path, the `WriteRangeViaReplayAsync` fallback, and backup/rollback. Reuse, don't duplicate.
- `DeviceRepository` methods: `ListPresetsAsync`, `SelectPresetAsync(name)`, `SaveCurrentAsAsync(name)`, `RenameAsync(index, name)`, `DeleteAsync(index)`, `ReadPresetAsync(index) → PresetDocument`, `WritePresetToSlotAsync(index, name, doc, verify, ct)`. `DeviceRepository.SlotCount == 30` (public const).
- `PresetDocument` has `ToBytes()` and `Parse(byte[])`; `PresetSlot(int Index, string Name)` with `IsEmpty`.
- `FakePresetDevice` (`tests/Sonulab.Core.Tests/FakePresetDevice.cs`) is `virtual SendAsync` — tests subclass it to count/fail specific commands. A `select` is `write root\app\preset:{"value":"NAME"}`; a `save` is the same with `,"save":"save"`; param replay emits `write root\app\<other path>:...`.
- `PresetItemViewModel` is constructed ONLY at `PresetListViewModel.cs:34`. Changing its constructor is safe.
- The row template's `PointerPressed="OnItemPointerPressed"` starts a drag; without a guard it will hijack button clicks (Task 5 fixes this).

---

### Task 1: `MoveStepAsync` — guards, boundaries, and occupied-neighbour swap

**Files:**
- Modify: `src/Sonulab.Core/Services/ReorderService.cs`
- Test: `tests/Sonulab.Core.Tests/ReorderServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these tests to `ReorderServiceTests.cs` (the existing `Dev()`, `Repo()`, `Names()`, `Amp()` helpers are already in the file):

```csharp
[Fact] public async Task MoveStep_down_swaps_with_occupied_neighbour()
{
    var d = Dev(); await d.OpenAsync(); var r = Repo(d);
    await new ReorderService(r).MoveStepAsync(from: 0, up: false);   // A <-> B
    Assert.Equal(new[] { "B", "A", "C", "D" }, (await Names(r))[..4]);
    Assert.Equal("\"mA\"", await Amp(r, 1));
    Assert.Equal("\"mB\"", await Amp(r, 0));
}

[Fact] public async Task MoveStep_up_swaps_with_occupied_neighbour()
{
    var d = Dev(); await d.OpenAsync(); var r = Repo(d);
    await new ReorderService(r).MoveStepAsync(from: 3, up: true);    // D <-> C
    Assert.Equal(new[] { "A", "B", "D", "C" }, (await Names(r))[..4]);
    Assert.Equal("\"mD\"", await Amp(r, 2));
}

[Fact] public async Task MoveStep_up_from_first_slot_is_noop()
{
    var d = Dev(); await d.OpenAsync(); var r = Repo(d);
    await new ReorderService(r).MoveStepAsync(from: 0, up: true);    // to = -1
    Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
}

[Fact] public async Task MoveStep_down_from_last_slot_is_noop()
{
    var d = Dev(); await d.OpenAsync(); var r = Repo(d);
    d.SeedSlot(29, "Z", new[] { @"root\app\amp\amp:{""value"":""mZ""}" });
    await new ReorderService(r).MoveStepAsync(from: 29, up: false);  // to = 30
    Assert.Equal("Z", (await Names(r))[29]);
}

[Fact] public async Task MoveStep_on_empty_slot_throws()
{
    var d = Dev(); await d.OpenAsync(); var r = Repo(d);            // slots 4..29 empty
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => new ReorderService(r).MoveStepAsync(from: 10, up: true));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~ReorderServiceTests.MoveStep"`
Expected: FAIL — `MoveStepAsync` does not exist (compile error).

- [ ] **Step 3: Implement `MoveStepAsync` (swap path only)**

Add this public method to `ReorderService` (place it just after `MoveAsync`). The empty-neighbour branch calls `RelocateToEmptyAsync`, which is added in Task 2 — to keep this task compiling, add a temporary throwing stub for it now and replace the stub in Task 2.

```csharp
/// <summary>
/// Move a single preset one physical slot up or down. Occupied neighbour → adjacent
/// swap (3-copy fast path via <see cref="MoveAsync"/>); empty neighbour → 1-copy relocate.
/// A move past the first/last slot is a no-op.
/// </summary>
public async Task MoveStepAsync(int from, bool up,
    IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
{
    var slots = await _repo.ListPresetsAsync(ct);
    if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
    int to = up ? from - 1 : from + 1;
    if (to < 0 || to >= slots.Count) return;                 // at a boundary: nothing to do
    if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
    if (slots.Any(s => s.Name.StartsWith(TempPrefix, StringComparison.Ordinal)))
        throw new InvalidOperationException($"A preset name uses the reserved prefix '{TempPrefix}'; rename it before reordering.");

    if (!slots[to].IsEmpty)
    {
        await MoveAsync(from, to, progress, ct);             // occupied neighbour: adjacent swap
        return;
    }

    await RelocateToEmptyAsync(slots[from].Name, from, to, progress, ct);   // empty neighbour
}

// TEMPORARY STUB — replaced with the real implementation in Task 2.
private Task RelocateToEmptyAsync(string origName, int from, int to,
    IProgress<ReorderProgress>? progress, CancellationToken ct) =>
    throw new NotImplementedException();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~ReorderServiceTests.MoveStep"`
Expected: PASS (all 5). The swap tests exercise occupied neighbours; the no-op and empty-throw tests never reach the stub.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/ReorderService.cs tests/Sonulab.Core.Tests/ReorderServiceTests.cs
git commit -m "feat(reorder): MoveStepAsync single-step swap + guards"
```

---

### Task 2: Fast 1-copy relocate into an empty neighbour

**Files:**
- Modify: `src/Sonulab.Core/Services/ReorderService.cs`
- Test: `tests/Sonulab.Core.Tests/ReorderServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `ReorderServiceTests.cs`. The `CountingDevice` proves the cheap path: exactly one `save`, at least one `select`, and ZERO param-replay writes (which is what the slow fallback would emit).

```csharp
sealed class CountingDevice : FakePresetDevice
{
    public int Saves, Selects, ParamWrites;
    public override Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (command.Contains("\"save\":\"save\"")) Saves++;
        else if (command.StartsWith("write root\\app\\preset:", StringComparison.Ordinal)) Selects++;
        else if (command.StartsWith("write root\\app\\", StringComparison.Ordinal)) ParamWrites++;
        return base.SendAsync(command, ct);
    }
}

[Fact] public async Task MoveStep_down_into_empty_relocates_with_one_copy()
{
    var d = new CountingDevice();
    d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
    await d.OpenAsync(); var r = Repo(d);
    await new ReorderService(r).MoveStepAsync(from: 0, up: false);          // A relocates to slot 1
    var names = await Names(r);
    Assert.Equal("", names[0]);
    Assert.Equal("A", names[1]);
    Assert.Equal("\"mA\"", await Amp(r, 1));
    Assert.Equal(1, d.Saves);            // ONE copy, not a 3-copy swap
    Assert.Equal(0, d.ParamWrites);      // proves it did NOT use the slow param-replay fallback
    Assert.True(d.Selects >= 1);
}

[Fact] public async Task MoveStep_up_into_empty_relocates()
{
    var d = new FakePresetDevice();
    d.SeedSlot(1, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 0 empty
    await d.OpenAsync(); var r = Repo(d);
    await new ReorderService(r).MoveStepAsync(from: 1, up: true);           // A relocates to slot 0
    var names = await Names(r);
    Assert.Equal("A", names[0]);
    Assert.Equal("", names[1]);
    Assert.Equal("\"mA\"", await Amp(r, 0));
}

[Fact] public async Task MoveStep_relocate_rolls_back_on_save_failure()
{
    var d = new FailOnceOnSave(1);                                         // fail the relocate's save
    d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
    await d.OpenAsync(); var r = Repo(d);
    await Assert.ThrowsAnyAsync<System.Exception>(
        () => new ReorderService(r).MoveStepAsync(from: 0, up: false));
    Assert.True(d.Fired);
    var names = await Names(r);
    Assert.Equal("A", names[0]);          // original restored
    Assert.Equal("", names[1]);           // destination left empty
    Assert.Equal("\"mA\"", await Amp(r, 0));
}
```

(`FailOnceOnSave` already exists in this test file from the existing suite.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~ReorderServiceTests.MoveStep_down_into_empty|FullyQualifiedName~ReorderServiceTests.MoveStep_up_into_empty|FullyQualifiedName~ReorderServiceTests.MoveStep_relocate"`
Expected: FAIL — `RelocateToEmptyAsync` throws `NotImplementedException`.

- [ ] **Step 3: Replace the stub with the real relocate**

In `ReorderService.cs`, delete the temporary stub from Task 1 and add this implementation (place it after `RotateViaSelectSaveAsync`):

```csharp
// FAST PATH: move one preset into an EMPTY adjacent slot with a single select+save copy.
private async Task RelocateToEmptyAsync(string origName, int from, int to,
    IProgress<ReorderProgress>? progress, CancellationToken ct)
{
    var backup = await _repo.ReadPresetAsync(from, ct);      // for verify + rollback
    string temp = TempPrefix + "reloc";
    try
    {
        ct.ThrowIfCancellationRequested();
        await _repo.SelectPresetAsync(origName, ct);         // live = preset content
        await _repo.RenameAsync(to, temp, ct);               // give the empty slot a unique name
        await _repo.SaveCurrentAsAsync(temp, ct);            // device copies live content into slot `to`
        await _repo.DeleteAsync(from, ct);                   // vacate the source slot
        await _repo.RenameAsync(to, origName, ct);           // restore the preset's real name
        progress?.Report(new ReorderProgress(1, 1, $"slot {to + 1}"));

        var back = await _repo.ReadPresetAsync(to, ct);      // read-back verify
        if (!back.ToBytes().AsSpan().SequenceEqual(backup.ToBytes()))
            throw new InvalidOperationException($"Relocate verify failed for slot {to}.");
    }
    catch (Exception original)
    {
        try
        {
            // Restore: rebuild the source slot from backup, then clear the destination.
            await _repo.WritePresetToSlotAsync(from, TempPrefix + "r", backup, verify: false, CancellationToken.None);
            await _repo.RenameAsync(from, origName, CancellationToken.None);
            await _repo.DeleteAsync(to, CancellationToken.None);
        }
        catch (Exception rb)
        {
            throw new AggregateException("Relocate failed and rollback also failed; device may be inconsistent.", original, rb);
        }
        throw;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~ReorderServiceTests"`
Expected: PASS — all existing `ReorderServiceTests` plus the new MoveStep tests.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/ReorderService.cs tests/Sonulab.Core.Tests/ReorderServiceTests.cs
git commit -m "feat(reorder): 1-copy relocate into empty neighbour"
```

---

### Task 3: `PresetItemViewModel` move-availability flags

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetItemViewModel.cs`
- Modify: `src/Sonulab.App/ViewModels/PresetListViewModel.cs:34` (constructor call)
- Test: `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `PresetListViewModelTests.cs` (uses the existing `Make()` helper, which seeds A,B,C in slots 0–2):

```csharp
[Fact] public async Task Move_flags_reflect_position_and_occupancy()
{
    var (vm, _) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    Assert.False(vm.Items[0].CanMoveUp);     // first slot
    Assert.True(vm.Items[0].CanMoveDown);
    Assert.True(vm.Items[2].CanMoveUp);
    Assert.True(vm.Items[2].CanMoveDown);    // slot 2 < 29, gap below
    Assert.False(vm.Items[5].CanMoveUp);     // empty slot — no buttons
    Assert.False(vm.Items[5].CanMoveDown);
    Assert.False(vm.Items[29].CanMoveDown);  // last slot
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests.Move_flags"`
Expected: FAIL — `CanMoveUp`/`CanMoveDown` do not exist (compile error).

- [ ] **Step 3: Add the flags and update the construction site**

Replace the body of `src/Sonulab.App/ViewModels/PresetItemViewModel.cs` with:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class PresetItemViewModel : ObservableObject
{
    public int Index { get; }
    public int DisplaySlot => Index + 1;
    [ObservableProperty] private string _name;
    public bool IsEmpty => string.IsNullOrEmpty(Name);

    /// <summary>True when this slot holds a preset and is not the first slot.</summary>
    public bool CanMoveUp { get; }
    /// <summary>True when this slot holds a preset and is not the last slot.</summary>
    public bool CanMoveDown { get; }

    public PresetItemViewModel(PresetSlot slot, int slotCount)
    {
        Index = slot.Index; _name = slot.Name;
        bool occupied = !string.IsNullOrEmpty(slot.Name);
        CanMoveUp = occupied && Index > 0;
        CanMoveDown = occupied && Index < slotCount - 1;
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
```

Then update the construction site in `src/Sonulab.App/ViewModels/PresetListViewModel.cs:34`:

```csharp
        foreach (var s in slots) Items.Add(new PresetItemViewModel(s, slots.Count));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests.Move_flags"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/ViewModels/PresetItemViewModel.cs src/Sonulab.App/ViewModels/PresetListViewModel.cs tests/Sonulab.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(ui): per-item CanMoveUp/CanMoveDown flags"
```

---

### Task 4: `MoveItemUp/DownCommand` on the list view model

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetListViewModel.cs`
- Test: `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `PresetListViewModelTests.cs`:

```csharp
[Fact] public async Task MoveItemDown_moves_that_row_and_selects_it()
{
    var (vm, _) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);   // A (slot 0) down, swaps with B
    Assert.Equal("B", vm.Items[0].Name);
    Assert.Equal("A", vm.Items[1].Name);
    Assert.Equal("A", vm.Selected?.Name);                    // selection follows the moved preset
}

[Fact] public async Task MoveItemUp_moves_that_row_independent_of_selection()
{
    var (vm, _) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];                               // selection is on A, not the moved row
    await vm.MoveItemUpCommand.ExecuteAsync(vm.Items[2]);     // C (slot 2) up, swaps with B
    Assert.Equal("A", vm.Items[0].Name);
    Assert.Equal("C", vm.Items[1].Name);
    Assert.Equal("B", vm.Items[2].Name);
}

[Fact] public async Task MoveItemDown_into_empty_gap_relocates()
{
    var (vm, _) = Make();                                    // A,B,C at 0..2; slot 3 empty
    await vm.RefreshCommand.ExecuteAsync(null);
    await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[2]);   // C down into empty slot 3
    Assert.True(vm.Items[2].IsEmpty);
    Assert.Equal("C", vm.Items[3].Name);
}

[Fact] public async Task MoveItem_on_empty_row_is_noop()
{
    var (vm, _) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    await vm.MoveItemUpCommand.ExecuteAsync(vm.Items[5]);     // empty slot
    Assert.True(vm.Items[5].IsEmpty);
    Assert.Equal("A", vm.Items[0].Name);                     // nothing moved
}

[Fact] public async Task MoveItem_is_gated_when_writes_disallowed()
{
    var dev = new FakePresetDevice();
    dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
    dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
    await dev.OpenAsync();
    var repo = new DeviceRepository(new SonuClient(dev));
    var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
    await vm.RefreshCommand.ExecuteAsync(null);
    await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
    Assert.Equal("A", vm.Items[0].Name);                     // unchanged — writes gated
    Assert.Equal("B", vm.Items[1].Name);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests.MoveItem"`
Expected: FAIL — `MoveItemUpCommand`/`MoveItemDownCommand` do not exist (compile error).

- [ ] **Step 3: Add the commands**

Insert these two `[RelayCommand]` methods into `PresetListViewModel` (after `MoveDownAsync`). `DeviceRepository` is already in scope via `using Sonulab.Core.Services;`.

```csharp
    [RelayCommand] private async Task MoveItemUpAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= DeviceRepository.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests.MoveItem"`
Expected: PASS (all 5).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/ViewModels/PresetListViewModel.cs tests/Sonulab.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(ui): per-row MoveItemUp/MoveItemDown commands"
```

---

### Task 5: XAML row buttons, busy-disable, and drag guard

**Files:**
- Modify: `src/Sonulab.App/Views/PresetListView.axaml`
- Modify: `src/Sonulab.App/Views/PresetListView.axaml.cs`

No unit test (Avalonia view); verification is `dotnet build` + manual eyeball.

- [ ] **Step 1: Add a compact button style**

In `PresetListView.axaml`, add a `UserControl.Styles` block immediately after the opening `<UserControl ...>` tag (before `<DockPanel>`):

```xml
  <UserControl.Styles>
    <Style Selector="Button.reorder">
      <Setter Property="Padding" Value="4"/>
      <Setter Property="MinWidth" Value="0"/>
      <Setter Property="MinHeight" Value="0"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="VerticalAlignment" Value="Center"/>
    </Style>
  </UserControl.Styles>
```

- [ ] **Step 2: Replace the `ListBox` block with the per-row-button version**

Replace the entire `<!-- Preset list -->` `ListBox` element (currently `PresetListView.axaml:42-64`) with:

```xml
    <!-- Preset list -->
    <ListBox x:Name="PresetList" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}" Margin="8,0"
             IsEnabled="{Binding !IsBusy}"
             DragDrop.AllowDrop="True">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:PresetItemViewModel">
          <DockPanel PointerPressed="OnItemPointerPressed">
            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="2"
                        IsVisible="{Binding !IsEmpty}" VerticalAlignment="Center">
              <Button Classes="reorder" IsEnabled="{Binding CanMoveUp}" ToolTip.Tip="Move up"
                      Command="{Binding $parent[ListBox].((vm:PresetListViewModel)DataContext).MoveItemUpCommand}"
                      CommandParameter="{Binding}">
                <PathIcon Data="{StaticResource Icon.ChevronUp}" Width="12" Height="12"/>
              </Button>
              <Button Classes="reorder" IsEnabled="{Binding CanMoveDown}" ToolTip.Tip="Move down"
                      Command="{Binding $parent[ListBox].((vm:PresetListViewModel)DataContext).MoveItemDownCommand}"
                      CommandParameter="{Binding}">
                <PathIcon Data="{StaticResource Icon.ChevronDown}" Width="12" Height="12"/>
              </Button>
            </StackPanel>
            <TextBlock Text="{Binding DisplaySlot, StringFormat='{}{0:00}'}"
                       Width="28" VerticalAlignment="Center"
                       FontFamily="Consolas,Cascadia Mono,monospace"
                       Opacity="0.6"/>
            <TextBlock VerticalAlignment="Center" Margin="8,0,0,0"
                       Opacity="{Binding IsEmpty, Converter={x:Static conv:BoolToOpacity.Instance}}">
              <TextBlock.Text>
                <Binding Path="Name"/>
              </TextBlock.Text>
              <TextBlock.FontStyle>
                <Binding Path="IsEmpty" Converter="{x:Static conv:BoolToItalic.Instance}"/>
              </TextBlock.FontStyle>
            </TextBlock>
          </DockPanel>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
```

- [ ] **Step 3: Guard the drag handler so button clicks don't start a drag**

In `PresetListView.axaml.cs`, replace the first line inside `OnItemPointerPressed` so presses originating on a reorder button are ignored. The full updated method:

```csharp
    private async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Ignore presses on the per-row reorder buttons so they fire their command, not a drag.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null) return;

        if (sender is Control c && c.DataContext is PresetItemViewModel item && !item.IsEmpty)
        {
            _dragFrom = item.Index;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(PresetIndexFormat, item.Index.ToString()));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
    }
```

Add `using Avalonia;` if `Visual` is not already resolvable (the file already has `using Avalonia.VisualTree;` for `FindAncestorOfType` and `using Avalonia.Controls;` for `Button`). Add `using Avalonia;` at the top if the build reports `Visual` unresolved.

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. (XAML compile errors surface here.)

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/Views/PresetListView.axaml src/Sonulab.App/Views/PresetListView.axaml.cs
git commit -m "feat(ui): per-row up/down buttons + busy-disable list + drag guard"
```

---

### Task 6: Full verification and manual eyeball

**Files:** none (verification only)

- [ ] **Step 1: Run the full build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors, 0 warnings introduced by this change.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass (~117 existing + the new MoveStep, Move_flags, and MoveItem tests). Confirm the count went up and nothing regressed.

- [ ] **Step 3: Launch the app and eyeball the list** (requires VoidX-Control CLOSED if a device is attached; otherwise the list is empty but layout is still checkable)

Run: `dotnet run --project src/Sonulab.App`
Verify by eye:
- Non-empty rows show two small chevron buttons right-aligned; empty rows show none.
- The first non-empty row's Up button is greyed/disabled; the last non-empty row's Down button is greyed/disabled.
- Clicking a row's Up/Down disables the whole list (progress bar shows), then the list re-enables with the new order and the moved preset selected.
- Dragging a row still works and clicking a button does NOT start a drag.

- [ ] **Step 4: Final commit if any cleanup was needed**

```bash
git add -A
git commit -m "chore: per-row reorder buttons verification"
```

(If no changes, skip. Merge to `main` per the project workflow — fast-forward — and push, after the review pass.)

---

## Notes for the implementer

- **Do NOT modify** `MoveAsync`, `RotateViaSelectSaveAsync`, `WriteRangeViaReplayAsync`, drag-drop, or the top command-bar buttons — they stay as-is (user wants them kept).
- The relocate's temp names (`__sstmp_reloc`, `__sstmp_r`) are well under the device's ~31-char name cap and are guarded against pre-existing collisions by the `TempPrefix` check in `MoveStepAsync`.
- Rollback in `RelocateToEmptyAsync` uses `CancellationToken.None` so a cancelled move still cleans up — matching the existing `MoveAsync` rollback convention.
- If the `$parent[ListBox]` binding fails to resolve the command at runtime (check the debug output for binding errors), the fallback is `$parent[UserControl].((vm:PresetListViewModel)DataContext)` — both reach the same view model via DataContext inheritance.
