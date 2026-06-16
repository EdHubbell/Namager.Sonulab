# Preset-Editing UX Improvements — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-block enable indicator, constant-width blocks, auto-load-on-selection, and in-place preset rename (context menu + F2) to the StompStation Manager UI.

**Architecture:** All changes are in `src/Sonulab.App` (ViewModels + Views + one Behavior + one Converter). VM logic is TDD'd against `FakeSonuLink`/`FakePresetDevice`; XAML and the attached behavior are build-verified + manually eyeballed. No `Sonulab.Core` changes.

**Tech Stack:** .NET 10, Avalonia 12 (built-in FluentTheme), CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia. Icons are built-in `PathIcon` geometries from `Icons.axaml` (e.g. `Icon.Power`). No third-party icon lib.
- Device preset names cap at ~31 chars (enforce `MaxLength="31"` on the rename box).
- Device writes go through the existing `RunAsync` gate (`writesAllowed`); never write directly from a command without it.
- Block enable leaf is `root\app\<block>\on_off`, `type:"enum"`, value `"ON"`/`"OFF"`. `eq` has none.
- Follow existing converter/VM patterns; keep files focused.

**Verified facts (don't re-investigate):**
- `ParameterEditorViewModel.LoadAsync` builds `Blocks` (each `BlockSectionViewModel`) from `browse root\app`; `on_off` is an `enum` so it lands in `section.Fields`.
- `BlockSectionViewModel` currently: `Header`, `IsExpanded`, `Fields`, `SubGroups`.
- `ParameterFieldViewModel` has `string Path`, `[ObservableProperty] string? Text`, `string Kind`.
- `FakeSonuLink`: `SeedBrowse(path, params records)`, `SeedScalar`; `write <path>:{"value":X}` is captured into a scalar store; `browse <path>` returns the seeded records. `SendAsync` is NOT virtual — wrap it with an `ISonuLink` decorator to count commands.
- `PresetListViewModel.RunAsync(string,Func<Task>)` returns `bool` (false if writes gated), sets `IsBusy`, runs work, reloads `Items`, clears `IsBusy`.
- The view's root `DataContext` for `PresetListView` is the `PresetListViewModel`; for `ParameterEditorView` it's the `ParameterEditorViewModel`.
- `Icon.Power` geometry exists in `Icons.axaml` (used by `MainWindow.axaml`).

---

### Task 1: Enable indicator — view model + converters (Feature B)

**Files:**
- Modify: `src/Sonulab.App/ViewModels/BlockSectionViewModel.cs`
- Modify: `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs` (in `LoadAsync`, set `EnableField`)
- Modify: `src/Sonulab.App/Converters/Converters.cs` (add `NotNull`, `EnabledToBrush`)
- Test: `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`

**Interfaces:**
- Produces: `BlockSectionViewModel.EnableField` (get/set `ParameterFieldViewModel?`), `BlockSectionViewModel.Enabled` (get `bool?`). Converters `Sonulab.App.Converters.NotNull.Instance`, `EnabledToBrush.Instance`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs` (inside the class):

```csharp
    static ParameterEditorViewModel VmFor(FakeSonuLink d) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(System.Array.Empty<string>()));

    [Fact] public async Task Block_Enabled_reflects_on_off_leaf()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}",
            "root\\app\\gate\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\gate\\threshold:{\"desc\":\"Threshold\",\"value\":-60.0,\"type\":\"float\",\"min\":-100.0,\"max\":-20.0}",
            "root\\app\\eq\\low:{\"desc\":\"Low\",\"value\":0.0,\"type\":\"float\",\"min\":-15.0,\"max\":15.0}");
        await d.OpenAsync();
        var vm = VmFor(d);
        await vm.LoadCommand.ExecuteAsync(null);
        bool? En(string h) => vm.Blocks.First(b => b.Header.Equals(h, StringComparison.OrdinalIgnoreCase)).Enabled;
        Assert.True(En("amp"));
        Assert.False(En("gate"));
        Assert.Null(En("eq"));        // eq has no on_off -> no indicator
    }

    [Fact] public async Task Block_Enabled_updates_when_on_off_field_changes()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        await d.OpenAsync();
        var vm = VmFor(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var amp = vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase));
        Assert.True(amp.Enabled);
        bool raised = false;
        amp.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BlockSectionViewModel.Enabled)) raised = true; };
        amp.EnableField!.Text = "OFF";
        Assert.False(amp.Enabled);
        Assert.True(raised);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests.Block_Enabled"`
Expected: FAIL — `EnableField`/`Enabled` don't exist (compile error).

- [ ] **Step 3: Add `EnableField`/`Enabled` to `BlockSectionViewModel`**

Replace the body of `src/Sonulab.App/ViewModels/BlockSectionViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sonulab.App.ViewModels;

public sealed partial class BlockSectionViewModel : ObservableObject
{
    public string Header { get; }
    [ObservableProperty] private bool _isExpanded = true;
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    public ObservableCollection<SubGroupViewModel> SubGroups { get; } = new();
    public BlockSectionViewModel(string header) => Header = header;

    private ParameterFieldViewModel? _enableField;

    /// <summary>The block's `on_off` field if it has one; drives <see cref="Enabled"/>.</summary>
    public ParameterFieldViewModel? EnableField
    {
        get => _enableField;
        set
        {
            if (_enableField is not null) _enableField.PropertyChanged -= OnEnableFieldChanged;
            _enableField = value;
            if (_enableField is not null) _enableField.PropertyChanged += OnEnableFieldChanged;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    /// <summary>True/false when the block has an on_off toggle (ON/OFF); null when it has none (e.g. eq).</summary>
    public bool? Enabled => _enableField is null
        ? null
        : string.Equals(_enableField.Text, "ON", StringComparison.OrdinalIgnoreCase);

    private void OnEnableFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterFieldViewModel.Text)) OnPropertyChanged(nameof(Enabled));
    }
}
```

- [ ] **Step 4: Set `EnableField` in `LoadAsync`**

In `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`, change the block-add tail of `LoadAsync` from:

```csharp
            if (section.Fields.Count > 0 || section.SubGroups.Count > 0)
                Blocks.Add(section);
```

to:

```csharp
            section.EnableField = section.Fields.FirstOrDefault(f => f.Path.EndsWith("\\on_off", StringComparison.Ordinal));
            if (section.Fields.Count > 0 || section.SubGroups.Count > 0)
                Blocks.Add(section);
```

- [ ] **Step 5: Add the converters**

Append to `src/Sonulab.App/Converters/Converters.cs` (the file already has `using Avalonia.Media;` and `using Avalonia.Data.Converters;`):

```csharp
/// <summary>bool? -> bool: true when non-null. Used to show the enable icon only when a block has an on_off toggle.</summary>
public sealed class NotNull : IValueConverter
{
    public static readonly NotNull Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) => value is not null;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}

/// <summary>enable-state (bool?) -> brush: true => LimeGreen (on), false/null => Gray (off).</summary>
public sealed class EnabledToBrush : IValueConverter
{
    public static readonly EnabledToBrush Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is true ? Brushes.LimeGreen : Brushes.Gray;
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests"`
Expected: PASS (the two new tests + the existing editor tests).

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.App/ViewModels/BlockSectionViewModel.cs src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs src/Sonulab.App/Converters/Converters.cs tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(editor): per-block Enabled state from on_off leaf + converters"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

---

### Task 2: Auto-load selected preset — view model (Feature C)

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`
- Test: `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`

**Interfaces:**
- Produces: `ParameterEditorViewModel.LoadForCommand` (IAsyncRelayCommand taking `string`), `ParameterEditorViewModel.IsLoading` (observable bool).

- [ ] **Step 1: Write the failing tests**

Append to `ParameterEditorViewModelTests.cs`:

```csharp
    // Counts device commands by wrapping the link (FakeSonuLink.SendAsync is not virtual).
    sealed class CountingLink : ISonuLink
    {
        private readonly ISonuLink _inner;
        public int Browses, PresetWrites;
        public CountingLink(ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public System.Threading.Tasks.Task OpenAsync(System.Threading.CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public System.Threading.Tasks.Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.StartsWith("browse ", StringComparison.Ordinal)) Browses++;
            else if (command.StartsWith("write root\\app\\preset:", StringComparison.Ordinal)) PresetWrites++;
            return _inner.SendAsync(command, ct);
        }
    }

    static (ParameterEditorViewModel vm, CountingLink link) LoadForVm()
    {
        var dev = new FakeSonuLink();
        dev.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        var vm = new ParameterEditorViewModel(new SonuClient(link),
            new LabelService(new Dictionary<string, string>()), new ParameterExposure(System.Array.Empty<string>()));
        return (vm, link);
    }

    [Fact] public async Task LoadFor_activates_preset_builds_blocks_and_toggles_IsLoading()
    {
        var (vm, link) = LoadForVm();
        var states = new System.Collections.Generic.List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterEditorViewModel.IsLoading)) states.Add(vm.IsLoading); };
        await vm.LoadForCommand.ExecuteAsync("Quad Reverb");
        Assert.Equal("Quad Reverb", vm.PresetName);
        Assert.NotEmpty(vm.Blocks);
        Assert.Equal(1, link.PresetWrites);                 // activated on device
        Assert.False(vm.IsLoading);
        Assert.Equal(new[] { true, false }, states);        // disabled during load, re-enabled after
    }

    [Fact] public async Task LoadFor_dedups_same_preset_name()
    {
        var (vm, link) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("X");
        await vm.LoadForCommand.ExecuteAsync("X");           // same name -> no-op
        Assert.Equal(1, link.PresetWrites);
        Assert.Equal(1, link.Browses);
    }
```

Add `using Sonulab.Core.Transport;` at the top of the test file if not already present (for `ISonuLink`/`FakeSonuLink`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests.LoadFor"`
Expected: FAIL — `LoadForCommand`/`IsLoading` don't exist.

- [ ] **Step 3: Add `LoadForAsync`/`IsLoading`**

In `ParameterEditorViewModel.cs`, add the observable near the other ones (after `[ObservableProperty] private string _presetName = "";`):

```csharp
    [ObservableProperty] private bool _isLoading;
    private string? _loadedName;
```

And add this command after `LoadAsync`:

```csharp
    /// <summary>Activate <paramref name="presetName"/> on the device, then load its params. No-op if already loaded.</summary>
    [RelayCommand]
    private async Task LoadForAsync(string presetName)
    {
        if (string.IsNullOrEmpty(presetName) || presetName == _loadedName) return;
        IsLoading = true;
        try
        {
            await _client.WriteAsync(@"root\app\preset", "\"" + presetName + "\"");   // select/activate on device
            await LoadAsync();                                                          // browse + rebuild blocks
            PresetName = presetName;
            _loadedName = presetName;
        }
        finally { IsLoading = false; }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~ParameterEditorViewModelTests.LoadFor"`
Expected: PASS (both).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(editor): LoadForAsync activates+loads a preset with IsLoading + dedup"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

---

### Task 3: In-place rename — view models (Feature D)

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetItemViewModel.cs` (item-local edit state + Begin/Cancel)
- Modify: `src/Sonulab.App/ViewModels/PresetListViewModel.cs` (CommitRename; remove old `RenameAsync`)
- Test: `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`

**Interfaces:**
- Produces: `PresetItemViewModel.IsEditing` (obs bool), `PresetItemViewModel.EditName` (obs string), `PresetItemViewModel.BeginRenameCommand` (IRelayCommand, no param), `PresetItemViewModel.CancelRenameCommand` (IRelayCommand, no param), `PresetListViewModel.CommitRenameCommand` (IAsyncRelayCommand taking `PresetItemViewModel?`).
- Begin/Cancel are item-local (the context menu lives in a popup outside the ListBox visual tree, so it can only bind to the item's own DataContext). Commit needs the repo, so it lives on the list VM and is invoked from the in-tree edit TextBox.

- [ ] **Step 1: Write the failing tests**

Append these tests to `tests/Sonulab.App.Tests/PresetListViewModelTests.cs` (leave the existing `Rename_changes_selected_name` test in place for now — it and the old `RenameAsync` command are removed together in Task 5, once the bottom rename bar that binds them is gone):

```csharp
    [Fact] public async Task InPlace_rename_changes_name()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];                       // "A"
        item.BeginRenameCommand.Execute(null);
        Assert.True(item.IsEditing);
        Assert.Equal("A", item.EditName);
        item.EditName = "Aprime";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("Aprime", vm.Items[0].Name);
    }

    [Fact] public async Task CommitRename_noop_when_blank_and_exits_edit()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "   ";                        // whitespace -> treated as no change
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.False(item.IsEditing);
    }

    [Fact] public async Task BeginRename_on_empty_row_does_nothing()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Items[5].BeginRenameCommand.Execute(null); // empty slot
        Assert.False(vm.Items[5].IsEditing);
    }

    [Fact] public async Task CancelRename_exits_edit_without_change()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Aprime";
        item.CancelRenameCommand.Execute(null);
        await vm.CommitRenameCommand.ExecuteAsync(item);   // guarded by IsEditing -> no-op
        Assert.False(item.IsEditing);
        Assert.Equal("A", vm.Items[0].Name);
    }

    [Fact] public async Task InPlace_rename_gated_when_writes_disallowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Nope";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("A", vm.Items[0].Name);          // unchanged (gated)
        Assert.False(item.IsEditing);                 // left edit mode
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests.InPlace_rename|FullyQualifiedName~PresetListViewModelTests.CommitRename|FullyQualifiedName~PresetListViewModelTests.BeginRename|FullyQualifiedName~PresetListViewModelTests.CancelRename"`
Expected: FAIL — commands/properties don't exist.

- [ ] **Step 3: Add edit state + Begin/Cancel to `PresetItemViewModel`**

Replace the body of `src/Sonulab.App/ViewModels/PresetItemViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>In-place rename state (display swaps a TextBlock for an edit TextBox while true).</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";

    public PresetItemViewModel(PresetSlot slot, int slotCount)
    {
        Index = slot.Index; _name = slot.Name;
        bool occupied = !string.IsNullOrEmpty(slot.Name);
        CanMoveUp = occupied && Index > 0;
        CanMoveDown = occupied && Index < slotCount - 1;
    }

    /// <summary>Enter in-place edit mode (no-op on an empty slot). The actual rename is committed by the list VM.</summary>
    [RelayCommand] private void BeginRename()
    {
        if (IsEmpty) return;
        EditName = Name;
        IsEditing = true;
    }

    /// <summary>Leave edit mode without renaming.</summary>
    [RelayCommand] private void CancelRename() => IsEditing = false;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
```

- [ ] **Step 4: Add `CommitRename` to `PresetListViewModel`**

In `src/Sonulab.App/ViewModels/PresetListViewModel.cs`, ADD this command (leave the existing
`RenameAsync` in place for now — it and the bottom rename bar are removed together in Task 5, so the
compiled binding to `RenameCommand` stays valid until then):

```csharp
    [RelayCommand] private async Task CommitRenameAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;          // guard: Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        // RunAsync reloads the list (recreating items) on success; on a gated/failed write it does not,
        // so clear the edit flag ourselves in that case.
        if (!await RunAsync($"Renaming '{s.Name}'…", () => _repo.RenameAsync(s.Index, name)))
            s.IsEditing = false;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter "FullyQualifiedName~PresetListViewModelTests"`
Expected: PASS (all preset-list VM tests, including the new rename ones).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.App/ViewModels/PresetItemViewModel.cs src/Sonulab.App/ViewModels/PresetListViewModel.cs tests/Sonulab.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(ui): in-place rename commands (item-local begin/cancel, list commit)"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

---

### Task 4: Parameter editor view — width, enable icon, loading overlay + selection→load wiring (Features A, B, C views)

**Files:**
- Modify: `src/Sonulab.App/Views/ParameterEditorView.axaml`
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`

No unit test (XAML + thin glue); verified by `dotnet build` + `dotnet test` (no regressions) + manual eyeball. Depends on Tasks 1 & 2.

- [ ] **Step 1: Update `ParameterEditorView.axaml`**

Replace the entire file with (width fix on Expander + ScrollViewer, icon header, loading overlay; the field templates are unchanged from the original):

```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Sonulab.App.ViewModels" xmlns:conv="using:Sonulab.App.Converters"
             x:Class="Sonulab.App.Views.ParameterEditorView" x:DataType="vm:ParameterEditorViewModel">
  <Grid>
    <DockPanel IsEnabled="{Binding !IsLoading}">
      <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="8">
        <Button Content="Load" Command="{Binding LoadCommand}"/>
        <Button Content="Save" Command="{Binding SaveCommand}"/>
        <TextBlock Text="●" IsVisible="{Binding IsDirty}" Foreground="#FF9900"
                   VerticalAlignment="Center" ToolTip.Tip="Unsaved changes"/>
      </StackPanel>
      <ScrollViewer HorizontalScrollBarVisibility="Disabled">
        <ItemsControl ItemsSource="{Binding Blocks}">
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:BlockSectionViewModel">
              <Expander IsExpanded="{Binding IsExpanded}" Margin="4,2"
                        HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                <Expander.Header>
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <PathIcon Data="{StaticResource Icon.Power}" Width="14" Height="14"
                              VerticalAlignment="Center"
                              IsVisible="{Binding Enabled, Converter={x:Static conv:NotNull.Instance}}"
                              Foreground="{Binding Enabled, Converter={x:Static conv:EnabledToBrush.Instance}}"
                              ToolTip.Tip="Effect enabled"/>
                    <TextBlock Text="{Binding Header}" VerticalAlignment="Center"/>
                  </StackPanel>
                </Expander.Header>
                <StackPanel>
                  <ItemsControl ItemsSource="{Binding Fields}">
                    <ItemsControl.ItemTemplate>
                      <DataTemplate x:DataType="vm:ParameterFieldViewModel">
                        <Grid ColumnDefinitions="180,*" Margin="4,2">
                          <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"
                                     ToolTip.Tip="{Binding Label}" TextTrimming="CharacterEllipsis"/>
                          <Panel Grid.Column="1">
                            <StackPanel Orientation="Horizontal" Spacing="6"
                                        IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Float}}">
                              <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}" Width="150"/>
                              <TextBlock Text="{Binding Number, StringFormat='{}{0:F2}'}" Width="52"
                                         VerticalAlignment="Center" FontFamily="Consolas,Cascadia Mono,monospace" FontSize="11"/>
                            </StackPanel>
                            <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"
                                      IsVisible="{Binding Kind, Converter={x:Static conv:Eq.EnumOrPlist}}"/>
                            <TextBox Text="{Binding Text}" IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Str}}"/>
                          </Panel>
                        </Grid>
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
                  <ItemsControl ItemsSource="{Binding SubGroups}">
                    <ItemsControl.ItemTemplate>
                      <DataTemplate x:DataType="vm:SubGroupViewModel">
                        <StackPanel Margin="12,4,0,0">
                          <TextBlock Text="{Binding Header}" FontWeight="SemiBold" Margin="0,2"/>
                          <ItemsControl ItemsSource="{Binding Fields}">
                            <ItemsControl.ItemTemplate>
                              <DataTemplate x:DataType="vm:ParameterFieldViewModel">
                                <Grid ColumnDefinitions="168,*" Margin="4,2">
                                  <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"
                                             ToolTip.Tip="{Binding Label}" TextTrimming="CharacterEllipsis"/>
                                  <Panel Grid.Column="1">
                                    <StackPanel Orientation="Horizontal" Spacing="6"
                                                IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Float}}">
                                      <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}" Width="150"/>
                                      <TextBlock Text="{Binding Number, StringFormat='{}{0:F2}'}" Width="52"
                                                 VerticalAlignment="Center" FontFamily="Consolas,Cascadia Mono,monospace" FontSize="11"/>
                                    </StackPanel>
                                    <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"
                                              IsVisible="{Binding Kind, Converter={x:Static conv:Eq.EnumOrPlist}}"/>
                                    <TextBox Text="{Binding Text}" IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Str}}"/>
                                  </Panel>
                                </Grid>
                              </DataTemplate>
                            </ItemsControl.ItemTemplate>
                          </ItemsControl>
                        </StackPanel>
                      </DataTemplate>
                    </ItemsControl.ItemTemplate>
                  </ItemsControl>
                </StackPanel>
              </Expander>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </ScrollViewer>
    </DockPanel>

    <!-- Loading overlay: detail view is disabled + dimmed while a preset loads -->
    <Border IsVisible="{Binding IsLoading}" Background="#60000000">
      <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" Spacing="6">
        <ProgressBar IsIndeterminate="True" Width="140"/>
        <TextBlock Text="Loading…" HorizontalAlignment="Center" Opacity="0.8"/>
      </StackPanel>
    </Border>
  </Grid>
</UserControl>
```

- [ ] **Step 2: Wire selection → load in `MainWindowViewModel`**

In `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`, replace the `Connected` handler body:

```csharp
        _connection.Connected += (_, _) =>
        {
            Presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed);
            _ = Presets.RefreshCommand.ExecuteAsync(null);
            Editor = new ParameterEditorViewModel(_connection.Client!);
        };
```

with:

```csharp
        _connection.Connected += (_, _) =>
        {
            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed);
            var editor = new ParameterEditorViewModel(_connection.Client!);
            // Selecting a preset activates + loads it into the editor (dedup is handled in LoadForAsync).
            presets.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PresetListViewModel.Selected)
                    && presets.Selected is { IsEmpty: false } sel)
                    editor.LoadForCommand.Execute(sel.Name);
            };
            Presets = presets;
            Editor = editor;
            _ = presets.RefreshCommand.ExecuteAsync(null);
        };
```

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds (0 errors); all tests pass (no regressions). XAML errors surface in the build.

- [ ] **Step 4: Commit**

```bash
git add src/Sonulab.App/Views/ParameterEditorView.axaml src/Sonulab.App/ViewModels/MainWindowViewModel.cs
git commit -m "feat(editor): constant-width blocks, enable icon, loading overlay + auto-load on select"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

---

### Task 5: Preset list view — in-place rename UI + focus behavior (Feature D view)

**Files:**
- Create: `src/Sonulab.App/Behaviors/EditBoxBehavior.cs`
- Modify: `src/Sonulab.App/Views/PresetListView.axaml`
- Modify: `src/Sonulab.App/Views/PresetListView.axaml.cs`

No unit test (XAML + attached behavior); verified by `dotnet build` + `dotnet test` + manual eyeball. Depends on Task 3.

- [ ] **Step 1: Create the focus behavior**

Create `src/Sonulab.App/Behaviors/EditBoxBehavior.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Sonulab.App.Behaviors;

/// <summary>
/// Attached property for the in-place rename box: when <c>FocusOnVisible</c> becomes true (bound to the
/// row's IsEditing), the TextBox is focused and its text selected once layout settles.
/// </summary>
public static class EditBoxBehavior
{
    public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusOnVisible", typeof(EditBoxBehavior));

    public static void SetFocusOnVisible(TextBox o, bool value) => o.SetValue(FocusOnVisibleProperty, value);
    public static bool GetFocusOnVisible(TextBox o) => o.GetValue(FocusOnVisibleProperty);

    static EditBoxBehavior()
    {
        FocusOnVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.GetNewValue<bool>())
                Dispatcher.UIThread.Post(() =>
                {
                    if (box.IsVisible) { box.Focus(); box.SelectAll(); }
                }, DispatcherPriority.Loaded);
        });
    }
}
```

- [ ] **Step 2: Update `PresetListView.axaml`**

Make these edits:

(a) Add the behaviors namespace to the root `UserControl` opening tag (after the `conv:` xmlns line):

```xml
             xmlns:behaviors="using:Sonulab.App.Behaviors"
```

(b) DELETE the bottom rename bar block entirely:

```xml
    <!-- Rename bar -->
    <Grid DockPanel.Dock="Bottom" ColumnDefinitions="*,Auto" Margin="8,4">
      <TextBox x:Name="RenameBox" Grid.Column="0" Watermark="New name…" Margin="0,0,4,0"/>
      <Button Grid.Column="1" Content="Rename"
              Command="{Binding RenameCommand}"
              CommandParameter="{Binding #RenameBox.Text}"/>
    </Grid>
```

(c) Add an F2 key binding to the `ListBox` — change the `ListBox` opening tag from:

```xml
    <ListBox x:Name="PresetList" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}" Margin="8,0"
             IsEnabled="{Binding !IsBusy}">
```

to:

```xml
    <ListBox x:Name="PresetList" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}" Margin="8,0"
             IsEnabled="{Binding !IsBusy}">
      <ListBox.KeyBindings>
        <KeyBinding Gesture="F2" Command="{Binding Selected.BeginRenameCommand}"/>
      </ListBox.KeyBindings>
```

(d) Add a context menu and swap the name `TextBlock` for a TextBlock+TextBox pair. Replace the row `DockPanel` and its trailing name `TextBlock` — change from:

```xml
          <DockPanel>
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
```

to:

```xml
          <DockPanel>
            <DockPanel.ContextMenu>
              <ContextMenu>
                <MenuItem Header="Rename" Command="{Binding BeginRenameCommand}"/>
              </ContextMenu>
            </DockPanel.ContextMenu>
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
            <Panel VerticalAlignment="Center" Margin="8,0,0,0">
              <TextBlock VerticalAlignment="Center" IsVisible="{Binding !IsEditing}"
                         Opacity="{Binding IsEmpty, Converter={x:Static conv:BoolToOpacity.Instance}}">
                <TextBlock.Text>
                  <Binding Path="Name"/>
                </TextBlock.Text>
                <TextBlock.FontStyle>
                  <Binding Path="IsEmpty" Converter="{x:Static conv:BoolToItalic.Instance}"/>
                </TextBlock.FontStyle>
              </TextBlock>
              <TextBox IsVisible="{Binding IsEditing}" Text="{Binding EditName}" MaxLength="31"
                       Padding="2,0" VerticalAlignment="Center"
                       behaviors:EditBoxBehavior.FocusOnVisible="{Binding IsEditing}"
                       LostFocus="OnEditBoxLostFocus">
                <TextBox.KeyBindings>
                  <KeyBinding Gesture="Enter"
                              Command="{Binding $parent[ListBox].((vm:PresetListViewModel)DataContext).CommitRenameCommand}"
                              CommandParameter="{Binding}"/>
                  <KeyBinding Gesture="Escape" Command="{Binding CancelRenameCommand}"/>
                </TextBox.KeyBindings>
              </TextBox>
            </Panel>
          </DockPanel>
```

- [ ] **Step 3: Add the LostFocus handler to `PresetListView.axaml.cs`**

Replace the body of `src/Sonulab.App/Views/PresetListView.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class PresetListView : UserControl
{
    public PresetListView() => InitializeComponent();

    // Commit an in-place rename when the edit box loses focus (e.g. click elsewhere).
    // Guarded by IsEditing so an Escape (which clears IsEditing) won't re-commit the abandoned edit.
    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PresetItemViewModel item }
            && DataContext is PresetListViewModel vm && item.IsEditing)
            vm.CommitRenameCommand.Execute(item);
    }
}
```

- [ ] **Step 4: Remove the now-dead bottom-bar rename command + its test**

The bottom rename bar (the only consumer of `RenameCommand`) was deleted in Step 2(b), so remove the
command and its test together now.

In `src/Sonulab.App/ViewModels/PresetListViewModel.cs`, DELETE:

```csharp
    [RelayCommand] private async Task RenameAsync(string? newName)
    {
        if (Selected is { } s && !string.IsNullOrWhiteSpace(newName)) await RunAsync($"Renaming…", () => _repo.RenameAsync(s.Index, newName!));
    }
```

In `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`, DELETE:

```csharp
    [Fact] public async Task Rename_changes_selected_name()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.RenameCommand.ExecuteAsync("Aprime");
        Assert.Equal("Aprime", vm.Items[0].Name);
    }
```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.App/Behaviors/EditBoxBehavior.cs src/Sonulab.App/Views/PresetListView.axaml src/Sonulab.App/Views/PresetListView.axaml.cs src/Sonulab.App/ViewModels/PresetListViewModel.cs tests/Sonulab.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(ui): in-place rename UI (context menu, F2, edit box + focus) — remove bottom bar"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>

---

### Task 6: Full verification + manual eyeball

**Files:** none (verification only)

- [ ] **Step 1: Full build + test**

Run: `dotnet build` then `dotnet test`
Expected: build clean; all tests pass (Core unchanged; App count up by the new VM tests, minus the removed `Rename_changes_selected_name`).

- [ ] **Step 2: Launch and eyeball** (requires VoidX-Control CLOSED + pedal connected for the editor to populate)

Run: `dotnet run --project src/Sonulab.App`, connect, then verify by eye:
- **A:** effect blocks stay the same (full panel) width whether expanded or collapsed.
- **B:** each block header shows a power icon — green when its `on_off` is ON, grey when OFF; `eq` shows no icon. Toggling a block's Enable combobox flips the header icon live.
- **C:** clicking a preset in the list activates + loads it into the editor; the detail view is disabled with a "Loading…" overlay until it finishes; re-clicking the same preset doesn't reload.
- **D:** right-click a preset → Rename edits the name in place; F2 on the selected preset does the same; Enter commits (list briefly disables + reloads), Escape cancels, clicking away commits; the bottom rename bar is gone; names cap at 31 chars.

- [ ] **Step 3: Final commit if any cleanup was needed**

```bash
git add -A
git commit -m "chore: preset-editing UX verification"
```
(If nothing changed, skip. Then merge to `main` per the project workflow after the final review.)

---

## Notes for the implementer

- Do NOT change `Sonulab.Core`. All work is in `Sonulab.App`.
- `BeginRename`/`CancelRename` are item-local on `PresetItemViewModel` because the `ContextMenu` renders in a popup outside the `ListBox` visual tree — only the item's own DataContext is reachable there. `CommitRename` lives on the list VM (it needs the repo) and is reached from the edit `TextBox`, which IS in the ListBox tree, via `$parent[ListBox]`.
- If the `Selected.BeginRenameCommand` F2 binding or the `$parent[ListBox]` command bindings don't resolve at runtime (watch the debug output for binding errors), the fallback is to handle F2 / commit in code-behind against `DataContext as PresetListViewModel`.
- The enable icon is display-only; the existing `on_off` "Enable" combobox remains the control. Don't remove it.
