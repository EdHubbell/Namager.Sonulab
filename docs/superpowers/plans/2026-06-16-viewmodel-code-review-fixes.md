# ViewModel Code-Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply 8 code-review fixes to the Avalonia ViewModel layer — per-field dirty tracking, save-only-dirty, friendly status + Client passthrough, preserve selection on move, PresetItemViewModel IsEmpty notification, remove duplicate Fluent theme, fix ViewLocator base type, add gate/event tests.

**Architecture:** All changes are surgical edits to existing files — no new source files, only one new test file. Changes 1–5 touch ViewModels, Change 6 edits the .csproj, Change 7 edits ViewLocator, Change 8 adds/removes test files. The gate test reuses the existing FakePresetDevice shared via `<Compile Include=...>`.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm 8.4, FluentAvaloniaUI 2.5, xUnit 2.9, C# 12 partial classes with source generators.

---

### Task 1: ParameterFieldViewModel — per-field dirty tracking

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs`

- [ ] **Step 1: Add `_originalJson` field and `IsDirty`/`MarkClean` members, set snapshot at end of constructor**

Replace the entire file content:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class ParameterFieldViewModel : ObservableObject
{
    public string Path { get; }
    public string Label { get; }
    public string Kind { get; }
    public double Min { get; }
    public double Max { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private double _number;
    [ObservableProperty] private string? _text;

    private string _originalJson = "";
    public bool IsDirty => ToJsonValue() != _originalJson;
    public void MarkClean() => _originalJson = ToJsonValue();

    public ParameterFieldViewModel(NodeSchema schema, string currentValueJson)
    {
        Path = schema.Path;
        Label = string.IsNullOrEmpty(schema.Desc) ? schema.Path : schema.Desc;
        Options = schema.Options;
        Min = schema.Min ?? 0; Max = schema.Max ?? 1;

        Kind = schema.Type switch
        {
            "float" => "float",
            "enum" => "enum",
            "plist" => "plist",
            "item" => "string",
            _ => "string",
        };

        var trimmed = currentValueJson.Trim();
        if (Kind == "float" && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            _number = n;
        else
            _text = trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2 ? trimmed[1..^1] : trimmed;

        _originalJson = ToJsonValue();
    }

    public string ToJsonValue() => Kind == "float"
        ? Number.ToString(CultureInfo.InvariantCulture)
        : "\"" + (Text ?? "") + "\"";
}
```

- [ ] **Step 2: Build to verify no errors**

```
cd C:\Development\Buckdrivers\Sonulab\StompStationManager
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.`

---

### Task 2: ParameterEditorViewModel — save only changed fields + dirty wiring

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`

- [ ] **Step 1: Wire PropertyChanged subscription in LoadAsync, filter to dirty fields in SaveAsync**

Replace the entire file:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class ParameterEditorViewModel : ObservableObject
{
    private readonly SonuClient _client;
    public ParameterEditorViewModel(SonuClient client) => _client = client;

    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _presetName = "";

    [RelayCommand]
    private async Task LoadAsync()
    {
        Fields.Clear();
        foreach (var rec in await _client.BrowseRecordsAsync(@"root\app"))
        {
            var schema = NodeSchema.FromRecord(rec);
            if (schema.Type is not ("float" or "enum" or "plist")) continue; // editable leaves only
            var value = rec.Json.TryGetProperty("value", out var v) ? v.GetRawText() : "\"\"";
            var field = new ParameterFieldViewModel(schema, value);
            field.PropertyChanged += (_, _) => IsDirty = true;
            Fields.Add(field);
        }
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in Fields.Where(f => f.IsDirty))
            await _client.WriteAsync(f.Path, f.ToJsonValue());
        if (!string.IsNullOrEmpty(PresetName))
            await _client.SaveAsync(@"root\app\preset", PresetName);
        foreach (var f in Fields) f.MarkClean();
        IsDirty = false;
    }
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.`

---

### Task 3: ConnectionViewModel — friendly status, Client property, try/catch

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ConnectionViewModel.cs`

- [ ] **Step 1: Add `Client` property, update status message to use `.Message`, wrap body in try/catch**

Replace the entire file:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly DeviceSession _session;
    private readonly IReadOnlyList<string> _ports;
    private static readonly int[] Bauds = { 115200 };

    public ConnectionViewModel(DeviceSession session, IReadOnlyList<string> ports)
    { _session = session; _ports = ports; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private string _status = "Disconnected";

    public DeviceRepository? Repository { get; private set; }
    public ReorderService? Reorder { get; private set; }
    public SonuClient? Client { get; private set; }
    public event EventHandler? Connected;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            var state = await _session.ConnectAsync(_ports, Bauds);
            IsConnected = state.Connected;
            if (!state.Connected) { Status = "Disconnected (no device found)"; return; }

            WritesAllowed = state.Compatibility!.WritesAllowed;
            Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility!.Message}";
            Client = _session.Client;
            Repository = new DeviceRepository(_session.Client!);
            Reorder = new ReorderService(Repository);
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
        }
    }
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.`

---

### Task 4: PresetListViewModel — RunAsync returns bool, preserve selection after move

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetListViewModel.cs`

- [ ] **Step 1: Change RunAsync return type to Task<bool>, update MoveUpAsync/MoveDownAsync to capture dest and re-select**

Replace the entire file:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class PresetListViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly ReorderService _reorder;
    private readonly bool _writes;

    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed; }

    public ObservableCollection<PresetItemViewModel> Items { get; } = new();
    [ObservableProperty] private PresetItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";

    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message;
        try { await work(); await ReloadAsync(); return true; }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        var slots = await _repo.ListPresetsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new PresetItemViewModel(s));
    }

    [RelayCommand] private Task RefreshAsync() => ReloadAsync();

    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} up…", () => _reorder.MoveAsync(s.Index, dest)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { } s && s.Index < Items.Count - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} down…", () => _reorder.MoveAsync(s.Index, dest)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task DuplicateAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        int dest = Items.FirstOrDefault(i => i.IsEmpty)?.Index ?? -1;
        if (dest < 0) return;
        await RunAsync($"Duplicating '{s.Name}'…", () => _repo.DuplicateAsync(s.Index, dest, s.Name + " copy"));
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s) await RunAsync($"Deleting '{s.Name}'…", () => _repo.DeleteAsync(s.Index));
    }

    [RelayCommand] private async Task RenameAsync(string? newName)
    {
        if (Selected is { } s && !string.IsNullOrWhiteSpace(newName)) await RunAsync($"Renaming…", () => _repo.RenameAsync(s.Index, newName!));
    }
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.`

---

### Task 5: PresetItemViewModel — raise IsEmpty on Name change

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetItemViewModel.cs`

- [ ] **Step 1: Add OnNameChanged partial method**

Replace the entire file:

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

    public PresetItemViewModel(PresetSlot slot) { Index = slot.Index; _name = slot.Name; }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
```

- [ ] **Step 2: Build to verify**

```
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.`

---

### Task 6: Remove redundant Avalonia.Themes.Fluent package reference

**Files:**
- Modify: `src/Sonulab.App/Sonulab.App.csproj`

- [ ] **Step 1: Remove the `Avalonia.Themes.Fluent` PackageReference line**

Edit `src/Sonulab.App/Sonulab.App.csproj` — delete this line:
```xml
    <PackageReference Include="Avalonia.Themes.Fluent" Version="12.0.4" />
```

The ItemGroup should then contain only: Avalonia, Avalonia.Desktop, Avalonia.Fonts.Inter, AvaloniaUI.DiagnosticsSupport, CommunityToolkit.Mvvm, FluentAvaloniaUI.

- [ ] **Step 2: Build to confirm FluentAvaloniaUI covers the theme**

```
dotnet build src/Sonulab.App/Sonulab.App.csproj
```

Expected: `Build succeeded.` (no missing references to Avalonia.Themes.Fluent).

---

### Task 7: ViewLocator — match on ObservableObject instead of ViewModelBase

**Files:**
- Modify: `src/Sonulab.App/ViewLocator.cs`

- [ ] **Step 1: Change `Match` to check for `ObservableObject`**

Replace the `Match` method body only:

```csharp
    public bool Match(object? data) => data is CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
```

Full file after change:

```csharp
using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Sonulab.App.ViewModels;

namespace Sonulab.App;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data) => data is CommunityToolkit.Mvvm.ComponentModel.ObservableObject;
}
```

- [ ] **Step 2: Build full solution to verify**

```
dotnet build
```

Expected: `Build succeeded.`

---

### Task 8: Tests — delete placeholder, add gate test + event/Client assertions

**Files:**
- Delete: `tests/Sonulab.App.Tests/UnitTest1.cs`
- Modify: `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs` (add `fired`/`Client` assertions)
- Modify: `tests/Sonulab.App.Tests/PresetListViewModelTests.cs` (add `Writes_are_gated_when_not_allowed`)

- [ ] **Step 1: Delete the placeholder test file**

Delete `tests/Sonulab.App.Tests/UnitTest1.cs`.

- [ ] **Step 2: Add `fired` and `Client` assertions to ConnectionViewModelTests**

In `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`, inside `Connect_sets_status_and_exposes_repository`, add before `ExecuteAsync`:
```csharp
        bool fired = false; vm.Connected += (_, _) => fired = true;
```
And after the assertions block, add:
```csharp
        Assert.True(fired);
        Assert.NotNull(vm.Client);
```

Full updated test file:

```csharp
using Sonulab.App.ViewModels;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class ConnectionViewModelTests
{
    // A connector whose factory yields a fake that answers identity + per-node browse on baud 115200.
    static DeviceSession Session()
    {
        FakeSerialPort Make()
        {
            var p = new FakeSerialPort();
            p.Responder = cmd => p.OpenedBaud != 115200 ? "" : cmd switch
            {
                @"read root\sys\_name"    => "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n",
                @"read root\sys\_id"      => "root\\sys\\_id:{\"value\":\"abc\"}\r\n",
                @"read root\sys\_ver"     => "root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n",
                @"read root\sys\_arch"    => "root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n",
                @"read root\sys\_license" => "root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n",
                @"browse root\presets"    => "root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n",
                @"browse root\amp"        => "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n",
                @"browse root\ir"         => "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n",
                _ => "",
            };
            return p;
        }
        var connector = new SonuConnector(Make, new SerialLinkOptions { PollMs = 2, IdleGapMs = 10, FirstByteTimeoutMs = 20, MaxWaitMs = 300 });
        return new DeviceSession(connector, new CompatibilityChecker(FirmwareCatalog.Default));
    }

    [Fact] public async Task Connect_sets_status_and_exposes_repository()
    {
        var vm = new ConnectionViewModel(Session(), new[] { "COM6" });
        Assert.False(vm.IsConnected);
        bool fired = false; vm.Connected += (_, _) => fired = true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.True(vm.WritesAllowed);
        Assert.Contains("AMP Station", vm.Status);
        Assert.Contains("2.5.1", vm.Status);
        Assert.NotNull(vm.Repository);
        Assert.NotNull(vm.Reorder);
        Assert.True(fired);
        Assert.NotNull(vm.Client);
    }
}
```

- [ ] **Step 3: Add `Writes_are_gated_when_not_allowed` test to PresetListViewModelTests**

Append new test method to `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`:

```csharp
    [Fact] public async Task Writes_are_gated_when_not_allowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("A", vm.Items[0].Name);   // unchanged — writes were gated
        Assert.Equal("B", vm.Items[1].Name);
    }
```

- [ ] **Step 4: Run all tests**

```
cd C:\Development\Buckdrivers\Sonulab\StompStationManager
dotnet test
```

Expected: All tests pass. Count should be ~104 (92 Core + 12 App).

---

### Task 9: Final build verification + commit

- [ ] **Step 1: Full solution build**

```
dotnet build
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 2: Commit all changes**

```bash
git add src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs \
        src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs \
        src/Sonulab.App/ViewModels/ConnectionViewModel.cs \
        src/Sonulab.App/ViewModels/PresetListViewModel.cs \
        src/Sonulab.App/ViewModels/PresetItemViewModel.cs \
        src/Sonulab.App/Sonulab.App.csproj \
        src/Sonulab.App/ViewLocator.cs \
        tests/Sonulab.App.Tests/ConnectionViewModelTests.cs \
        tests/Sonulab.App.Tests/PresetListViewModelTests.cs
git rm tests/Sonulab.App.Tests/UnitTest1.cs
git commit -m "fix(app): save only dirty fields, friendly status + Client, keep selection on move, ViewLocator base, remove dup theme, +gate/event tests"
```

Expected: Commit SHA printed.
