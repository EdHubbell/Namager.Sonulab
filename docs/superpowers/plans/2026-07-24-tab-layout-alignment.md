# Tab Layout Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Presets, Amps and IRs tabs render with identical toolbar and list geometry, so switching tabs moves nothing.

**Architecture:** Four layout metrics become resources in `SonulabTheme.axaml`, consumed through three style classes (`.slot-toolbar`, `.slot-list`, `.slot-message`) that the views apply instead of repeating literal margins. Because each number is then defined once, the tabs cannot drift apart again. The redundant header Move Up/Down buttons are removed along with the ViewModel commands behind them, which also brings the over-wide Amps toolbar inside its 360px column.

**Tech Stack:** Avalonia 12.0.4 (built-in `FluentTheme`), .NET 10, CommunityToolkit.Mvvm `[RelayCommand]`, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-24-tab-layout-alignment-design.md`

## Global Constraints

- **Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia** — it targets Avalonia 11 and crashes at runtime on 12.
- **`AvaloniaUseCompiledBindingsByDefault` is `true`** (`src/Namager.App/Namager.App.csproj:10`). A `{Binding Foo}` referencing a removed command is a **compile error**, not a runtime warning. Delete bindings and commands in the same task.
- **No hardcoded spacing literals in the three list views** once Task 3 lands — spacing comes from the tokens, mirroring the existing CLAUDE.md rule for color ("use tokens, never hex literals in views").
- Inside `SonulabTheme.axaml` reference tokens with `{StaticResource …}`; **inside views use `{DynamicResource …}`**, since the tokens arrive via a `Styles` include and static lookup across that boundary is load-order sensitive.
- **Test count must not change.** The tests touching removed commands are rewritten onto the per-row commands, never deleted.
- Build: `dotnet build` · Test: `dotnet test` · Run: `dotnet run --project src/Namager.App` (no device needed for layout checks; the app runs disconnected).
- **Sequencing:** another agent is working in these same view files on `feat-amp-ir-reorder`. Do not start Task 1 until that work is merged.

## File Structure

| File | Responsibility after this plan |
|---|---|
| `src/Namager.App/Styles/SonulabTheme.axaml` | Sole definition of the four layout tokens, the three layout classes, and the hoisted `Button.reorder` style |
| `src/Namager.App/Views/PresetListView.axaml` | Presets toolbar + list, no spacing literals |
| `src/Namager.App/Views/AmpListView.axaml` | Amps toolbar + list + detail host, no spacing literals |
| `src/Namager.App/Views/IrListView.axaml` | IRs toolbar + list + message rows, no spacing literals |
| `src/Namager.App/Views/ParameterEditorView.axaml` | Presets detail pane; toolbar row in the shared band, error text on its own row |
| `src/Namager.App/Views/AmpDetailPanel.axaml` | Amps detail pane; band-height spacer at top |
| `src/Namager.App/Views/MainWindow.axaml` | Page hosting; pane gap from token |
| `src/Namager.App/ViewModels/{Preset,Amp,Ir}ListViewModel.cs` | Per-row reorder commands only |
| `tests/Namager.App.Tests/LayoutContractTests.cs` | **New** — guards that the views use the classes rather than literals |

Deliberately out of scope: the duplicated `TextBlock.used` style in `AmpListView`/`IrListView`, and unifying the three near-identical ListBox `ItemTemplate`s. Both are real duplication; neither is this spec's problem.

---

### Task 1: Layout tokens and style classes

Adds the vocabulary. Nothing consumes it yet, so the app must look **exactly** as it does today when this task lands.

**Files:**
- Modify: `src/Namager.App/Styles/SonulabTheme.axaml:45` (insert tokens), and append styles after the existing `Border.rule` style block (`:64-69`)

**Interfaces:**
- Consumes: nothing.
- Produces: resource keys `Sonulab.PageInset` (Thickness), `Sonulab.ListInset` (Thickness), `Sonulab.PaneGap` (Thickness), `Sonulab.ToolbarHeight` (Double); style classes `slot-toolbar`, `slot-list`, `slot-message`; hoisted `Button.reorder`.

- [ ] **Step 1: Measure the current Presets toolbar height**

The token must equal what Presets renders today, or that tab will shift. `AvaloniaUI.DiagnosticsSupport` is already referenced for Debug builds (`Namager.App.csproj:32`), so the inspector is available.

Run: `dotnet run --project src/Namager.App`
Press **F12** to open the diagnostics inspector, select the Presets tab, and click the toolbar `StackPanel` (the direct child of the root `DockPanel`, the one holding the Refresh button). Read its `Bounds.Height`.

Expected: `32`. **If it reads anything else, use that number wherever `32` appears in this plan** and note the correction in the spec's token table.

Fallback if F12 does not open: temporarily add `Loaded="OnDbg"` to the toolbar `StackPanel` in `PresetListView.axaml` with a code-behind handler `void OnDbg(object? s, RoutedEventArgs e) => Log.Info("toolbar h={0}", ((Control)s!).Bounds.Height);`, run, read the log, then revert both edits.

- [ ] **Step 2: Add the four tokens**

In `src/Namager.App/Styles/SonulabTheme.axaml`, insert immediately before the `</ResourceDictionary>` that closes the outer dictionary (currently line 45, just after `</ResourceDictionary.ThemeDictionaries>`):

```xml
      <!-- ===== Layout metrics. Theme-invariant, so they sit outside ThemeDictionaries.
           Single definition point: the three list tabs must not re-declare spacing. ===== -->
      <Thickness x:Key="Sonulab.PageInset">8,6,8,4</Thickness>
      <Thickness x:Key="Sonulab.ListInset">8,0</Thickness>
      <Thickness x:Key="Sonulab.PaneGap">12,0,0,0</Thickness>
      <x:Double x:Key="Sonulab.ToolbarHeight">32</x:Double>
```

`PaneGap` is a `Thickness`, not a `Double`, because it is consumed as a `Margin` — XAML will not widen a `Double` into a `Thickness`. `ToolbarHeight` stays a `Double` because it is consumed as `Height`.

- [ ] **Step 3: Add the three layout classes plus the hoisted reorder style**

Append after the `Border.rule` style block (currently ends line 69):

```xml
  <!-- ===== Slot-tab layout contract =====
       Every list pane is [toolbar row of ToolbarHeight] + [list]. Pinning the row height means
       an icon-only toolbar and a toolbar with text buttons occupy the same band, so the list
       and the detail pane beside it always start at the same y. -->
  <Style Selector="StackPanel.slot-toolbar">
    <Setter Property="Margin" Value="{StaticResource Sonulab.PageInset}"/>
    <Setter Property="Height" Value="{StaticResource Sonulab.ToolbarHeight}"/>
  </Style>
  <!-- Buttons fill the band exactly, so text and icon content cannot produce different heights. -->
  <Style Selector="StackPanel.slot-toolbar > Button">
    <Setter Property="Height" Value="{StaticResource Sonulab.ToolbarHeight}"/>
    <Setter Property="MinHeight" Value="0"/>
    <Setter Property="VerticalAlignment" Value="Stretch"/>
  </Style>
  <Style Selector="ListBox.slot-list">
    <Setter Property="Margin" Value="{StaticResource Sonulab.ListInset}"/>
  </Style>
  <!-- Inline warning/error rows in a LIST pane. Detail panes keep their own frame. -->
  <Style Selector="TextBlock.slot-message">
    <Setter Property="Margin" Value="{StaticResource Sonulab.ListInset}"/>
    <Setter Property="FontSize" Value="11"/>
    <Setter Property="TextWrapping" Value="Wrap"/>
  </Style>

  <!-- Per-row reorder chevrons. Hoisted out of the three list views, which each carried an
       identical copy. -->
  <Style Selector="Button.reorder">
    <Setter Property="Padding" Value="4"/>
    <Setter Property="MinWidth" Value="0"/>
    <Setter Property="MinHeight" Value="0"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
  </Style>
```

Deliberately **not** set on `.slot-toolbar`: `Orientation` and `Spacing`. The list toolbars use `Spacing="4"` and the parameter editor's uses `Spacing="8"`; those differences are legitimate and stay local. Only height and inset are part of the alignment contract.

- [ ] **Step 4: Verify nothing changed**

Run: `dotnet build`
Expected: succeeds, no new warnings.

Run: `dotnet test`
Expected: all green, count unchanged.

Run: `dotnet run --project src/Namager.App` and click through Presets → Amps → IRs.
Expected: **identical to before this task.** The three views still carry their own `Button.reorder` copies, which shadow the hoisted one harmlessly; they are removed in Task 3.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Styles/SonulabTheme.axaml
git commit -m "feat(app): layout tokens + slot-tab style classes

Single definition point for toolbar/list spacing so the three list tabs
cannot drift apart. Nothing consumes them yet.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Remove the header Move Up/Down buttons and commands

Every row already carries its own up/down chevrons, so the header pair is redundant. Removing two icon buttons also takes the Amps toolbar from roughly 396px to roughly 312px, inside its 360px column — which is what stops the Delete button spilling into the detail area.

This is a refactor, not a behavior change. Rewrite the tests **first** so they stay green throughout, then delete the production code and let the compiler prove no caller remains.

**Files:**
- Modify: `tests/Namager.App.Tests/PresetListViewModelTests.cs:29-37, 88-102, 248-263, 265-273, 336-345`
- Modify: `tests/Namager.App.Tests/AmpListViewModelTests.cs:874-884, 886-896`
- Modify: `tests/Namager.App.Tests/IrListViewModelTests.cs:318-328, 330-339`
- Modify: `src/Namager.App/ViewModels/PresetListViewModel.cs:87-109` (delete)
- Modify: `src/Namager.App/ViewModels/AmpListViewModel.cs:167-186` (delete)
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs:158-177` (delete)
- Modify: `src/Namager.App/Views/PresetListView.axaml:24-29` (delete)
- Modify: `src/Namager.App/Views/AmpListView.axaml:30-35` (delete)
- Modify: `src/Namager.App/Views/IrListView.axaml:29-34` (delete)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `MoveUpCommand` / `MoveDownCommand` no longer exist on any of the three ViewModels. The surviving reorder API is `MoveItemUpCommand` / `MoveItemDownCommand`, each taking the row's item VM: `MoveItemUpAsync(PresetItemViewModel? item)` / `MoveItemDownAsync(PresetItemViewModel? item)` (`PresetListViewModel.cs:111,121`), and the `AmpItemViewModel` / `IrItemViewModel` equivalents (`AmpListViewModel.cs:187,195`, `IrListViewModel.cs:178,186`).

**Behavioral note for the rewrites:** the header commands operated on `Selected` and then set `Selected = Items[dest]`. The per-row commands take an explicit item and set `Selected` the same way, so assertions on `Selected` still hold. The bounds guards differ cosmetically — `MoveDownAsync` used `s.Index < Items.Count - 1`, `MoveItemDownAsync` uses `s.Index >= DeviceRepository.SlotCount - 1` — but `Items` always holds 30 rows, so both refuse at slot 29. No test outcome changes.

- [ ] **Step 1: Rewrite the five preset test call sites**

In `tests/Namager.App.Tests/PresetListViewModelTests.cs`, replace lines 29-37:

```csharp
    [Fact] public async Task MoveItemDown_moves_the_row_and_reloads()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);   // "A" at slot 0 -> slot 1
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
    }
```

Replace lines 97-98 (keep the `vm.Selected` line — `DeleteCommand` on the next line still needs it):

```csharp
        vm.Selected = vm.Items[0];
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
```

Replace lines 248-263:

```csharp
    [Fact] public async Task Row_move_reads_no_preset_content()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var link = new DreadCountingLink(dev);
        var repo = new DeviceRepository(new SonuClient(link));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, link.Dreads);                          // lean: zero content reads
    }
```

Replace lines 265-273:

```csharp
    [Fact] public async Task Row_move_on_an_empty_slot_is_a_noop()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[5]);   // empty slot — must not throw
        await vm.MoveItemUpCommand.ExecuteAsync(vm.Items[5]);
        Assert.True(vm.Items[5].IsEmpty);                        // nothing moved
    }
```

Replace lines 336-345:

```csharp
    [Fact] public async Task MoveItemDown_notifies_moved_not_invalidate()
    {
        var (vm, _, usage) = MakeWithUsage();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);   // A at slot 0 -> slot 1
        Assert.Equal(1, usage.MovedCount);
        Assert.Equal((0, 1), usage.LastMoved);
        Assert.Equal(0, usage.InvalidateCount);
    }
```

- [ ] **Step 2: Rewrite the two amp test call sites**

In `tests/Namager.App.Tests/AmpListViewModelTests.cs`, replace lines 874-884:

```csharp
    [Fact] public async Task MoveItemDown_reorders_items_and_touches_usage_never()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);   // "A" at slot 0 -> slot 1
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, usage.InvalidateCount);          // reorder must NOT rescan
        Assert.Equal(0, usage.MovedCount);               // nor targeted-notify (that's presets only)
    }
```

Replace lines 892-893:

```csharp
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
```

- [ ] **Step 3: Rewrite the two IR test call sites**

In `tests/Namager.App.Tests/IrListViewModelTests.cs`, replace lines 318-328:

```csharp
    [Fact] public async Task MoveItemDown_reorders_items_and_touches_usage_never()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, usage.InvalidateCount);
        Assert.Equal(0, usage.MovedCount);
    }
```

Replace lines 335-336:

```csharp
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
```

- [ ] **Step 4: Run the tests — still green before any production change**

Run: `dotnet test`
Expected: all green, count unchanged. The rewritten tests exercise the per-row path, which already exists.

- [ ] **Step 5: Delete the ViewModel commands, and watch the build go red**

Delete the whole `[RelayCommand] private async Task MoveUpAsync()` and `[RelayCommand] private async Task MoveDownAsync()` methods from:
- `src/Namager.App/ViewModels/PresetListViewModel.cs:87-109`
- `src/Namager.App/ViewModels/AmpListViewModel.cs:167-186`
- `src/Namager.App/ViewModels/IrListViewModel.cs:158-177`

Leave `MoveItemUpAsync` / `MoveItemDownAsync` untouched.

Run: `dotnet build`
Expected: **FAILS.** Compiled bindings resolve `MoveUpCommand` / `MoveDownCommand` at compile time, so the six buttons still in the views produce errors like `Unable to resolve property or method of name 'MoveUpCommand'`. That failure is the proof there were no other callers.

- [ ] **Step 6: Delete the six header buttons**

From `src/Namager.App/Views/PresetListView.axaml`, delete lines 24-29:

```xml
      <Button Command="{Binding MoveUpCommand}" ToolTip.Tip="Move up">
        <PathIcon Data="{StaticResource Icon.ChevronUp}" Width="16" Height="16"/>
      </Button>
      <Button Command="{Binding MoveDownCommand}" ToolTip.Tip="Move down">
        <PathIcon Data="{StaticResource Icon.ChevronDown}" Width="16" Height="16"/>
      </Button>
```

From `src/Namager.App/Views/AmpListView.axaml`, delete lines 30-35, and from `src/Namager.App/Views/IrListView.axaml`, delete lines 29-34 — the same two buttons, each additionally carrying `IsEnabled="{Binding CanMutate}"`.

- [ ] **Step 7: Verify**

Run: `dotnet build`
Expected: succeeds.

Run: `dotnet test`
Expected: all green, count unchanged from Step 4.

Run: `dotnet run --project src/Namager.App`
Expected: Presets header is Refresh / Duplicate / Delete; Amps is Refresh / Upload .nam… / Upload .vxamp… / Delete; IRs is Refresh / Upload .wav… / Upload .irblob… / Delete. **On Amps, no button now extends past the list's right edge.** Per-row chevrons still work on all three tabs.

- [ ] **Step 8: Commit**

```bash
git add src/Namager.App/Views/PresetListView.axaml src/Namager.App/Views/AmpListView.axaml \
        src/Namager.App/Views/IrListView.axaml \
        src/Namager.App/ViewModels/PresetListViewModel.cs \
        src/Namager.App/ViewModels/AmpListViewModel.cs \
        src/Namager.App/ViewModels/IrListViewModel.cs \
        tests/Namager.App.Tests/PresetListViewModelTests.cs \
        tests/Namager.App.Tests/AmpListViewModelTests.cs \
        tests/Namager.App.Tests/IrListViewModelTests.cs
git commit -m "refactor(app): drop redundant header Move Up/Down

Every row already carries up/down chevrons. Removing the header pair also
brings the Amps toolbar inside its 360px column, so Delete no longer spills
into the detail area. Tests move to the per-row commands; count unchanged.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Apply the layout classes to the three list views

The alignment fix proper. A guard test drives it: written first, it fails because the views still carry literal margins.

**Files:**
- Create: `tests/Namager.App.Tests/LayoutContractTests.cs`
- Modify: `src/Namager.App/Views/PresetListView.axaml` (local style block, toolbar, list)
- Modify: `src/Namager.App/Views/AmpListView.axaml` (local style block, toolbar, message row, list)
- Modify: `src/Namager.App/Views/IrListView.axaml` (local style block, toolbar, two message rows, list)

**Interfaces:**
- Consumes: `Sonulab.PageInset`, `Sonulab.ListInset`, `Sonulab.ToolbarHeight`, and the `slot-toolbar` / `slot-list` / `slot-message` / `reorder` classes from Task 1.
- Produces: `LayoutContractTests.RepoRoot()`, a `[CallerFilePath]`-derived absolute path to the repo root, reused by Task 4.

- [ ] **Step 1: Write the failing guard test**

The `.axaml` files are not copied to the test output directory, so locate them from the test source file's own compile-time path.

Create `tests/Namager.App.Tests/LayoutContractTests.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace Namager.App.Tests;

/// <summary>Guards the layout contract from docs/superpowers/specs/2026-07-24-tab-layout-alignment-design.md:
/// the three list tabs must take their toolbar/list spacing from the shared style classes, never from
/// literals. Without this, a well-meaning edit re-hardcodes a margin and the tabs silently drift apart
/// again. Reads the .axaml as text — the App test project has no Avalonia.Headless reference and this
/// deliberately does not add one.</summary>
public class LayoutContractTests
{
    internal static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string View(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Namager.App", "Views", name));

    public static TheoryData<string> ListViews => new()
    {
        "PresetListView.axaml", "AmpListView.axaml", "IrListView.axaml",
    };

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_uses_the_shared_toolbar_and_list_classes(string file)
    {
        var xaml = View(file);
        Assert.Contains("Classes=\"slot-toolbar\"", xaml);
        Assert.Contains("Classes=\"slot-list\"", xaml);
    }

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_has_no_hardcoded_toolbar_or_list_spacing(string file)
    {
        var xaml = View(file);
        Assert.DoesNotContain("Margin=\"8,6,8,4\"", xaml);   // old Presets/IRs toolbar literal
        Assert.DoesNotContain("Margin=\"0,0,0,6\"", xaml);   // old Amps toolbar literal
        Assert.DoesNotContain("Margin=\"8,0\"", xaml);       // old list + IR message literal
    }

    [Theory, MemberData(nameof(ListViews))]
    public void List_view_does_not_redeclare_the_reorder_button_style(string file)
        => Assert.DoesNotContain("Selector=\"Button.reorder\"", View(file));

    [Fact]
    public void Theme_defines_the_layout_tokens()
    {
        var theme = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Styles", "SonulabTheme.axaml"));
        Assert.Contains("x:Key=\"Sonulab.PageInset\"", theme);
        Assert.Contains("x:Key=\"Sonulab.ListInset\"", theme);
        Assert.Contains("x:Key=\"Sonulab.PaneGap\"", theme);
        Assert.Contains("x:Key=\"Sonulab.ToolbarHeight\"", theme);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test --filter LayoutContractTests`
Expected: `Theme_defines_the_layout_tokens` PASSES (Task 1 added them). The three theories FAIL — the views have no `Classes="slot-toolbar"` and still contain the literal margins.

- [ ] **Step 3: Convert `PresetListView.axaml`**

Delete the local `<UserControl.Styles>` block (lines 8-16) entirely — `Button.reorder` now lives in the theme. Then change the toolbar (line 20) to:

```xml
    <StackPanel DockPanel.Dock="Top" Classes="slot-toolbar" Orientation="Horizontal" Spacing="4">
```

and the list (line 39) to:

```xml
    <ListBox x:Name="PresetList" Classes="slot-list" ItemsSource="{Binding Items}"
             SelectedItem="{Binding Selected}" IsEnabled="{Binding !IsBusy}">
```

- [ ] **Step 4: Convert `AmpListView.axaml`**

From the `<UserControl.Styles>` block, delete only the `Button.reorder` style (lines 14-20); **keep** `TextBlock.used`. Then the toolbar (line 26):

```xml
      <StackPanel DockPanel.Dock="Top" Classes="slot-toolbar" Orientation="Horizontal" Spacing="4">
```

the error row (lines 47-50):

```xml
      <TextBlock DockPanel.Dock="Top" Classes="slot-message"
                 Foreground="{DynamicResource Sonulab.DangerBrush}"
                 Text="{Binding ErrorMessage}"
                 IsVisible="{Binding ErrorMessage, Converter={x:Static ObjectConverters.IsNotNull}}"/>
```

and the list (line 52):

```xml
      <ListBox Classes="slot-list" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}"
               IsEnabled="{Binding !IsBusy}">
```

- [ ] **Step 5: Convert `IrListView.axaml`**

From the `<UserControl.Styles>` block, delete only the `Button.reorder` style (lines 14-20); **keep** `TextBlock.used`. Then the toolbar (line 25):

```xml
    <StackPanel DockPanel.Dock="Top" Classes="slot-toolbar" Orientation="Horizontal" Spacing="4">
```

both message rows (lines 47-52):

```xml
    <TextBlock DockPanel.Dock="Top" Classes="slot-message"
               Foreground="{DynamicResource Sonulab.WarningBrush}"
               Text="{Binding UploadBlockedMessage}"
               IsVisible="{Binding UploadBlockedMessage, Converter={x:Static ObjectConverters.IsNotNull}}"/>
    <TextBlock DockPanel.Dock="Top" Classes="slot-message"
               Foreground="{DynamicResource Sonulab.DangerBrush}"
               Text="{Binding ErrorMessage}"
               IsVisible="{Binding ErrorMessage, Converter={x:Static ObjectConverters.IsNotNull}}"/>
```

and the list (line 89):

```xml
    <ListBox Classes="slot-list" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}"
             IsEnabled="{Binding !IsBusy}">
```

The upload `Border`'s `Margin="8,4"` (line 55) stays — it is a bottom panel, not part of the toolbar/list contract.

Note the first IR message row gains `TextWrapping="Wrap"` from the class, which it lacked. That is an improvement: a long blocked-upload message previously clipped.

- [ ] **Step 6: Run the guard test — now green**

Run: `dotnet test --filter LayoutContractTests`
Expected: all PASS.

- [ ] **Step 7: Verify the full suite and the app**

Run: `dotnet build && dotnet test`
Expected: all green, count = previous count + 10 (the new theories: 3 + 3 + 3 + 1).

Run: `dotnet run --project src/Namager.App` and cycle Presets → Amps → IRs.
Expected: **the first toolbar button and the list's left and top edges do not move between tabs.** Presets and IRs look exactly as they did before this plan started.

- [ ] **Step 8: Commit**

```bash
git add tests/Namager.App.Tests/LayoutContractTests.cs \
        src/Namager.App/Views/PresetListView.axaml \
        src/Namager.App/Views/AmpListView.axaml \
        src/Namager.App/Views/IrListView.axaml
git commit -m "fix(app): align toolbar and list geometry across the three list tabs

Amps sat 8px left and 6px high of Presets/IRs with a 16px wider list.
All three now take spacing from the shared classes, guarded by a test that
fails if a literal margin creeps back in.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Align the detail panes' top edges

The list panes now agree. This makes each detail pane's top edge land level with its list's top edge, and retires the magic `34`.

**Files:**
- Modify: `src/Namager.App/Views/ParameterEditorView.axaml:6-14`
- Modify: `src/Namager.App/Views/AmpDetailPanel.axaml:7-9`
- Modify: `src/Namager.App/Views/AmpListView.axaml` (the `AmpDetailPanel` margin, formerly line 105)
- Modify: `src/Namager.App/Views/MainWindow.axaml:125`
- Modify: `tests/Namager.App.Tests/LayoutContractTests.cs` (extend)
- Modify: `docs/HARDWARE-VALIDATION-ui-polish.md` (append the manual check)

**Interfaces:**
- Consumes: `Sonulab.PaneGap`, `Sonulab.ToolbarHeight`, the `slot-toolbar` class, and `LayoutContractTests.RepoRoot()` from Task 3.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Extend the guard test**

Append to `LayoutContractTests`:

```csharp
    [Fact]
    public void Amp_detail_pane_reserves_the_toolbar_band_instead_of_a_magic_offset()
    {
        var detail = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Views", "AmpDetailPanel.axaml"));
        Assert.Contains("Classes=\"slot-toolbar\"", detail);

        var amps = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Namager.App", "Views", "AmpListView.axaml"));
        Assert.DoesNotContain("Margin=\"16,34,0,0\"", amps);   // the retired magic offset
    }

    [Fact]
    public void Both_detail_panes_take_the_same_gap_token()
    {
        foreach (var file in new[] { "AmpListView.axaml", "MainWindow.axaml" })
        {
            var xaml = File.ReadAllText(
                Path.Combine(RepoRoot(), "src", "Namager.App", "Views", file));
            Assert.Contains("Sonulab.PaneGap", xaml);
        }
    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test --filter LayoutContractTests`
Expected: the two new facts FAIL; everything from Task 3 still passes.

- [ ] **Step 3: Move the editor's error text out of its toolbar row**

Pinning the row to `ToolbarHeight` would clip a two-line error. Replace `src/Namager.App/Views/ParameterEditorView.axaml` lines 6-14 with:

```xml
      <StackPanel DockPanel.Dock="Top" Classes="slot-toolbar" Orientation="Horizontal" Spacing="8">
        <Button Content="Load" Command="{Binding LoadCommand}"/>
        <Button Content="Save" Command="{Binding SaveCommand}"/>
        <TextBlock Text="●" IsVisible="{Binding IsDirty}" Foreground="{DynamicResource Sonulab.WarningBrush}"
                   VerticalAlignment="Center" ToolTip.Tip="Unsaved changes"/>
      </StackPanel>
      <!-- Out of the toolbar band: the row is height-pinned, and a wrapped error would clip. -->
      <TextBlock DockPanel.Dock="Top" Margin="0,0,0,4" FontSize="11" TextWrapping="Wrap"
                 Foreground="{DynamicResource Sonulab.DangerBrush}"
                 Text="{Binding ErrorMessage}"
                 IsVisible="{Binding ErrorMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
```

The dirty-dot stays inline — it is one glyph and cannot wrap. This message row keeps the detail frame's own `0,0,0,4`, matching `AmpDetailPanel`; it deliberately does **not** use `.slot-message`, whose 8px left inset belongs to the list frame.

- [ ] **Step 4: Reserve the band in the amp detail pane**

In `src/Namager.App/Views/AmpDetailPanel.axaml`, insert as the first child of the root `DockPanel` (before the message TextBlocks at line 10):

```xml
    <!-- The list pane has a toolbar; this pane has no buttons. Reserving the same band keeps this
         pane's top edge level with the list's, and replaces a hand-tuned 34px offset. -->
    <StackPanel DockPanel.Dock="Top" Classes="slot-toolbar"/>
```

- [ ] **Step 5: Point both detail panes at the gap token**

In `src/Namager.App/Views/AmpListView.axaml`, the detail host becomes:

```xml
    <views:AmpDetailPanel Grid.Column="1" Margin="{DynamicResource Sonulab.PaneGap}"/>
```

In `src/Namager.App/Views/MainWindow.axaml` line 125:

```xml
            <ContentControl Grid.Column="1"
                            Content="{Binding Editor}" Margin="{DynamicResource Sonulab.PaneGap}"/>
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: all green, count = Task 3's count + 2.

- [ ] **Step 7: Verify in the app**

Run: `dotnet run --project src/Namager.App`

Expected:
- **Presets** — Load/Save sit in the same horizontal band as Refresh/Duplicate/Delete; the parameter editor's first expander and the preset list start at the same y.
- **Amps** — the detail card's top edge is level with the list's top edge; the gap between list and detail is 12px, matching Presets (it was 16px).
- **IRs** — unchanged from Task 3.

- [ ] **Step 8: Record the manual check**

Append to `docs/HARDWARE-VALIDATION-ui-polish.md`:

```markdown
## Tab layout alignment (spec 2026-07-24-tab-layout-alignment-design.md)

No device required — the app runs disconnected for these.

- [ ] Cycle Presets → Amps → IRs → Presets. The first toolbar button does not move.
- [ ] Same cycle: the list's left and top edges do not move.
- [ ] Presets and Amps: the detail pane's top edge is level with the list's top edge.
- [ ] Amps: no toolbar button extends past the list's right edge.
- [ ] Amps and IRs: trigger an upload-blocked or error message; the list drops by the same
      amount on both tabs, and a long message wraps instead of clipping.
- [ ] Presets: with a parameter-save error showing, the Load/Save row keeps its height and the
      error text wraps below it.
- [ ] Repeat the first two checks in the other theme variant (light/dark).
```

- [ ] **Step 9: Commit**

```bash
git add src/Namager.App/Views/ParameterEditorView.axaml \
        src/Namager.App/Views/AmpDetailPanel.axaml \
        src/Namager.App/Views/AmpListView.axaml \
        src/Namager.App/Views/MainWindow.axaml \
        tests/Namager.App.Tests/LayoutContractTests.cs \
        docs/HARDWARE-VALIDATION-ui-polish.md
git commit -m "fix(app): align detail pane top edges with their lists

Load/Save move into the shared toolbar band; the amp detail pane reserves
the same band instead of a hand-tuned 34px offset; both panes take the same
12px gap token.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage.** Page shape → Tasks 3 and 4. Four tokens → Task 1 Step 2. Three style classes → Task 1 Step 3. Header button sets → Task 2. Code changes behind the removal → Task 2 Steps 1-6. Parameter editor's error row → Task 4 Step 3. All ten files in the spec's table appear in a task. Verification items 1-4 → Task 3 Step 7, Task 4 Steps 6-8. Risks: file collision → Global Constraints; `ToolbarHeight` measurement → Task 1 Step 1; hard `Height` pin → Task 4 Step 3; overflow margin → Task 2 Step 7. The spec's fallback of a short upload label is **not** given a task, correctly — it only applies if measurement contradicts the arithmetic, and Task 2 Step 7 is where that would surface.

**Type consistency.** `RepoRoot()` is defined `internal static` in Task 3 and reused in Task 4. Token key strings are identical in Task 1's XAML, Task 3's assertions and Task 4's usages. Command names in Task 2's rewrites (`MoveItemUpCommand` / `MoveItemDownCommand`) match the generated names for `MoveItemUpAsync` / `MoveItemDownAsync` at the cited source lines.

**Known ordering hazard.** Task 3 deletes the per-view `Button.reorder` styles; the hoisted copy from Task 1 must already be in place or the row chevrons lose their styling. The task order enforces this, and `List_view_does_not_redeclare_the_reorder_button_style` fails loudly if only half the change lands.
