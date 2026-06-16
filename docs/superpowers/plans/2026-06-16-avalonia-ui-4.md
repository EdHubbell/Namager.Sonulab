# Avalonia UI (Plan 4 of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A desktop Avalonia app (MVVM) that connects to the StompStation, shows a connection/compatibility banner, lists presets with **move up/down + duplicate + rename + delete**, edits parameters via a schema-driven generic editor, and does backup/restore — all driven by the `Sonulab.Core` services.

**Architecture:** Avalonia 11 + CommunityToolkit.Mvvm. ViewModels depend only on `Sonulab.Core` services (`DeviceSession`, `DeviceRepository`, `ReorderService`, `BackupService`, `SonuClient`) through small interfaces, so they unit-test against the existing fakes (`FakePresetDevice`/`FakeSonuLink`) with **no hardware and no UI**. Views are thin XAML bound to the VMs and verified by running the app. All device work is async and marshaled back to the UI thread by Avalonia's binding layer; long operations (reorder/duplicate ≈ tens of seconds) surface a busy state + progress.

**Tech Stack:** .NET 10, Avalonia 11, **FluentAvalonia** (Fluent v2 theme + `SymbolIcon` icons + `NavigationView`), CommunityToolkit.Mvvm, xUnit. Builds on Plans 1–3b.

**UI style (per request):** Fluent design, icon-forward, **dashboard layout** — a `NavigationView` left rail (Presets / Amps / IRs / Settings) with a top status/connection bar and card-style content panels. Use FluentAvalonia's `SymbolIcon` (e.g. `ArrowUp`, `ArrowDown`, `Copy`, `Delete`, `Save`, `ArrowClockwise`, `Plug`) on actions. ViewModels are style-agnostic; this affects Task 1 (theme) and Task 6 (Views) only.

**Scope (v1):** Presets are the focus — list, reorder, duplicate, rename, delete, edit, backup/restore. Amp/IR lists are shown **read-only** (reorder/upload deferred). `.nam` upload remains phase 2.

## Public surface (ViewModels) defined by this plan

```csharp
namespace Sonulab.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject {
    public ConnectionViewModel(DeviceSession session, IReadOnlyList<string> ports);
    public bool IsConnected { get; }
    public string Status { get; }              // "Disconnected" / "AMP Station 2.5.1 — Tested" / ...
    public bool WritesAllowed { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public DeviceRepository? Repository { get; }
    public ReorderService? Reorder { get; }
    public event EventHandler? Connected;
}

public partial class PresetItemViewModel : ObservableObject {
    public int Index { get; }                  // 0-based protocol index
    public int DisplaySlot => Index + 1;       // 1-based UI
    public string Name { get; set; }
    public bool IsEmpty { get; }
}

public partial class PresetListViewModel : ObservableObject {
    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed);
    public ObservableCollection<PresetItemViewModel> Items { get; }
    public PresetItemViewModel? Selected { get; set; }
    public bool IsBusy { get; }
    public string BusyMessage { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }       // Selected up one slot
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand DuplicateCommand { get; }    // Selected -> next empty slot
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand<string> RenameCommand { get; }
}

public partial class ParameterFieldViewModel : ObservableObject {
    public ParameterFieldViewModel(NodeSchema schema, string currentValueJson);
    public string Path { get; }
    public string Label { get; }
    public string Kind { get; }                // "float" | "enum" | "plist" | "string" | "readonly"
    public double Number { get; set; }
    public double Min { get; }  public double Max { get; }
    public string? Text { get; set; }
    public IReadOnlyList<string> Options { get; }
    public string ToJsonValue();               // current value -> JSON literal for `write`
}

public partial class ParameterEditorViewModel : ObservableObject {
    public ParameterEditorViewModel(SonuClient client);
    public ObservableCollection<ParameterFieldViewModel> Fields { get; }
    public bool IsDirty { get; }
    public IAsyncRelayCommand LoadCommand { get; }         // browse root\app -> fields
    public IAsyncRelayCommand SaveCommand { get; }         // write changed fields + save:save
    public string PresetName { get; set; }
}
```

## File structure

```
src/Sonulab.App/
  Sonulab.App.csproj                 (Avalonia exe)
  Program.cs  App.axaml  App.axaml.cs
  ViewModels/ConnectionViewModel.cs
  ViewModels/PresetItemViewModel.cs
  ViewModels/PresetListViewModel.cs
  ViewModels/ParameterFieldViewModel.cs
  ViewModels/ParameterEditorViewModel.cs
  ViewModels/MainWindowViewModel.cs
  Views/MainWindow.axaml(.cs)
  Views/PresetListView.axaml(.cs)
  Views/ParameterEditorView.axaml(.cs)
tests/Sonulab.App.Tests/
  Sonulab.App.Tests.csproj           (xUnit; refs Sonulab.App + Sonulab.Core + reuses Core test fakes)
  ConnectionViewModelTests.cs
  PresetListViewModelTests.cs
  ParameterFieldViewModelTests.cs
  ParameterEditorViewModelTests.cs
```

> The App.Tests project references the **`FakePresetDevice`/`FakeSonuLink`** sources. Since those live in `tests/Sonulab.Core.Tests`, add them to App.Tests via `<Compile Include="..\Sonulab.Core.Tests\FakePresetDevice.cs" />` and `FakeSonuLink` is already in `src/Sonulab.Core` (production) so it's referenced normally. (Confirm `FakeSonuLink`'s location during Task 1; if it's in the test project, include it the same way.)

---

### Task 1: Scaffold the Avalonia app + VM test project

**Files:** Create `src/Sonulab.App/*` (template), `tests/Sonulab.App.Tests/*`.

- [ ] **Step 1: Create projects**

From repo root:
```bash
dotnet new install Avalonia.Templates
dotnet new avalonia.mvvm -n Sonulab.App -o src/Sonulab.App -f net10.0
dotnet new xunit -n Sonulab.App.Tests -o tests/Sonulab.App.Tests -f net10.0
dotnet sln add src/Sonulab.App tests/Sonulab.App.Tests
dotnet add src/Sonulab.App reference src/Sonulab.Core
dotnet add tests/Sonulab.App.Tests reference src/Sonulab.App
dotnet add tests/Sonulab.App.Tests reference src/Sonulab.Core
dotnet add src/Sonulab.App package CommunityToolkit.Mvvm
dotnet add src/Sonulab.App package FluentAvalonia
```

- [ ] **Step 1b: Use the Fluent theme** — set `App.axaml` styles to FluentAvalonia. In `src/Sonulab.App/App.axaml`, replace the `<Application.Styles>` content with:
```xml
  <Application.Styles>
    <sty:FluentAvaloniaTheme xmlns:sty="using:FluentAvalonia.Styling" />
  </Application.Styles>
```
(Keep the `x:Class`/`xmlns` on the `<Application>` root.) This gives Fluent v2 styling app-wide; `SymbolIcon` becomes available via `xmlns:ui="using:FluentAvalonia.UI.Controls"`.

- [ ] **Step 2: Make the App.Tests project see the Core test fakes**

In `tests/Sonulab.App.Tests/Sonulab.App.Tests.csproj` add (path-adjust if `FakePresetDevice.cs` differs):
```xml
  <ItemGroup>
    <Compile Include="..\Sonulab.Core.Tests\FakePresetDevice.cs" />
  </ItemGroup>
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded.` (the template app + empty test project compile).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: scaffold Sonulab.App (Avalonia MVVM) + App.Tests"
```

---

### Task 2: ConnectionViewModel

**Files:** Create `src/Sonulab.App/ViewModels/ConnectionViewModel.cs`; Test `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`:
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
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.True(vm.WritesAllowed);
        Assert.Contains("AMP Station", vm.Status);
        Assert.Contains("2.5.1", vm.Status);
        Assert.NotNull(vm.Repository);
        Assert.NotNull(vm.Reorder);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ConnectionViewModelTests`
Expected: FAIL — `ConnectionViewModel` does not exist.

- [ ] **Step 3: Implement ConnectionViewModel**

`src/Sonulab.App/ViewModels/ConnectionViewModel.cs`:
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
    public event EventHandler? Connected;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var state = await _session.ConnectAsync(_ports, Bauds);
        IsConnected = state.Connected;
        if (!state.Connected) { Status = "Disconnected (no device found)"; return; }

        WritesAllowed = state.Compatibility!.WritesAllowed;
        Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility.Status}";
        Repository = new DeviceRepository(_session.Client!);
        Reorder = new ReorderService(Repository);
        Connected?.Invoke(this, EventArgs.Empty);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ConnectionViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): ConnectionViewModel"
```

---

### Task 3: PresetItemViewModel + PresetListViewModel (load/select)

**Files:** Create `src/Sonulab.App/ViewModels/PresetItemViewModel.cs`, `src/Sonulab.App/ViewModels/PresetListViewModel.cs`; Test `tests/Sonulab.App.Tests/PresetListViewModelTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/PresetListViewModelTests.cs`:
```csharp
using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetListViewModelTests
{
    static (PresetListViewModel vm, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        return (new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true), dev);
    }

    [Fact] public async Task Refresh_loads_30_items_with_display_slots()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Items.Count);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.Equal(1, vm.Items[0].DisplaySlot);
        Assert.True(vm.Items[5].IsEmpty);
    }

    [Fact] public async Task MoveDown_moves_selected_and_reloads()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                       // "A" at slot 0
        await vm.MoveDownCommand.ExecuteAsync(null);     // -> slot 1
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
    }

    [Fact] public async Task Duplicate_copies_selected_into_first_empty_slot()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];                       // "B"
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Contains(vm.Items, i => i.Name == "B copy");
    }

    [Fact] public async Task Delete_empties_selected_slot()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[2];                       // "C"
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.True(vm.Items[2].IsEmpty);
    }

    [Fact] public async Task Rename_changes_selected_name()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.RenameCommand.ExecuteAsync("Aprime");
        Assert.Equal("Aprime", vm.Items[0].Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PresetListViewModelTests`
Expected: FAIL — VMs don't exist.

- [ ] **Step 3: Implement the VMs**

`src/Sonulab.App/ViewModels/PresetItemViewModel.cs`:
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
}
```

`src/Sonulab.App/ViewModels/PresetListViewModel.cs`:
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

    private async Task RunAsync(string message, Func<Task> work)
    {
        if (!_writes) return;
        IsBusy = true; BusyMessage = message;
        try { await work(); await ReloadAsync(); }
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
        if (Selected is { Index: > 0 } s) await RunAsync($"Moving slot {s.DisplaySlot} up…", () => _reorder.MoveAsync(s.Index, s.Index - 1));
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { } s && s.Index < Items.Count - 1) await RunAsync($"Moving slot {s.DisplaySlot} down…", () => _reorder.MoveAsync(s.Index, s.Index + 1));
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PresetListViewModelTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): PresetItem + PresetListViewModel (load/reorder/duplicate/rename/delete)"
```

---

### Task 4: ParameterFieldViewModel (schema -> editable field)

**Files:** Create `src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs`; Test `tests/Sonulab.App.Tests/ParameterFieldViewModelTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/ParameterFieldViewModelTests.cs`:
```csharp
using Sonulab.App.ViewModels;
using Sonulab.Core.Model;
using Xunit;

public class ParameterFieldViewModelTests
{
    static NodeSchema Schema(string json) { NodeRecord.TryParse(json, out var r); return NodeSchema.FromRecord(r!); }

    [Fact] public void Float_field_exposes_range_and_round_trips_json()
    {
        var s = Schema(@"root\app\amp\gain:{""desc"":""Gain"",""value"":0.0,""type"":""float"",""min"":-20.0,""max"":20.0,""def"":0.0,""unit"":""dB""}");
        var f = new ParameterFieldViewModel(s, "3.5");
        Assert.Equal("float", f.Kind);
        Assert.Equal(-20.0, f.Min);
        Assert.Equal(20.0, f.Max);
        Assert.Equal(3.5, f.Number);
        f.Number = -6.0;
        Assert.Equal("-6", f.ToJsonValue());
    }

    [Fact] public void Enum_field_exposes_options_and_quotes_value()
    {
        var s = Schema(@"root\app\reverb\mode:{""desc"":""Mode"",""value"":""ROOM"",""type"":""enum"",""options"":[""ROOM"",""HALL""]}");
        var f = new ParameterFieldViewModel(s, "\"ROOM\"");
        Assert.Equal("enum", f.Kind);
        Assert.Equal(new[] { "ROOM", "HALL" }, f.Options);
        f.Text = "HALL";
        Assert.Equal("\"HALL\"", f.ToJsonValue());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ParameterFieldViewModelTests`
Expected: FAIL — VM doesn't exist.

- [ ] **Step 3: Implement ParameterFieldViewModel**

`src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs`:
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
    }

    public string ToJsonValue() => Kind == "float"
        ? Number.ToString(CultureInfo.InvariantCulture)
        : "\"" + (Text ?? "") + "\"";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ParameterFieldViewModelTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): ParameterFieldViewModel (schema-driven field)"
```

---

### Task 5: ParameterEditorViewModel (load schema / save edits)

**Files:** Create `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`; Test `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`:
```csharp
using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class ParameterEditorViewModelTests
{
    static FakeSonuLink Dev()
    {
        var d = new FakeSonuLink();
        d.SeedScalar(@"root\app\amp\on_off", "\"ON\"");
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"unit\":\"dB\"}");
        return d;
    }

    [Fact] public async Task Load_builds_editable_fields_from_browse()
    {
        var link = Dev(); await link.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(link));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Fields.Count);
        Assert.Contains(vm.Fields, f => f.Path == @"root\app\amp\gain" && f.Kind == "float");
    }

    [Fact] public async Task Save_writes_changed_fields_then_save_command()
    {
        var link = Dev(); await link.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(link)) { PresetName = "MyPreset" };
        await vm.LoadCommand.ExecuteAsync(null);
        var gain = vm.Fields.First(f => f.Path == @"root\app\amp\gain");
        gain.Number = -6.0;
        await vm.SaveCommand.ExecuteAsync(null);
        // FakeSonuLink stored the write; reading it back reflects the change
        Assert.Equal("-6", await new SonuClient(link).ReadValueAsync(@"root\app\amp\gain"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ParameterEditorViewModelTests`
Expected: FAIL — VM doesn't exist.

- [ ] **Step 3: Implement ParameterEditorViewModel**

`src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`:
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
            Fields.Add(new ParameterFieldViewModel(schema, value));
        }
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in Fields)
            await _client.WriteAsync(f.Path, f.ToJsonValue());
        if (!string.IsNullOrEmpty(PresetName))
            await _client.SaveAsync(@"root\app\preset", PresetName);
        IsDirty = false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ParameterEditorViewModelTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — Core + App VM tests all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): ParameterEditorViewModel (schema-driven load/save)"
```

---

### Task 6: Views + MainWindow wiring (manual verification)

**Files:** Create `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.axaml(.cs)`, `Views/PresetListView.axaml(.cs)`, `Views/ParameterEditorView.axaml(.cs)`; wire `App.axaml.cs`. No unit tests (UI); verified by running the app.

- [ ] **Step 1: MainWindowViewModel** — `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

namespace Sonulab.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private ConnectionViewModel _connection;
    [ObservableProperty] private PresetListViewModel? _presets;
    [ObservableProperty] private ParameterEditorViewModel? _editor;

    public MainWindowViewModel()
    {
        var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
        var connector = new SonuConnector(() => new SystemSerialPort(), options);
        var session = new DeviceSession(connector, new CompatibilityChecker(FirmwareCatalog.Default));
        _connection = new ConnectionViewModel(session, new[] { "COM6" });
        _connection.Connected += (_, _) =>
        {
            Presets = new PresetListViewModel(_connection.Repository!, _connection.Reorder!, _connection.WritesAllowed);
            Editor = new ParameterEditorViewModel(_connection.Repository is null ? null! : new Sonulab.Core.SonuClient(/* see note */ default!));
        };
    }
}
```
> Implementation note: expose the connected `SonuClient` from `ConnectionViewModel` (add a `public SonuClient? Client => _session.Client;` property) and use it to build the `ParameterEditorViewModel`; the placeholder above must be replaced with `new ParameterEditorViewModel(_connection.Client!)`. Add the `Client` property to `ConnectionViewModel` in this task and adjust.

- [ ] **Step 2: MainWindow.axaml** — Fluent **dashboard**: a top status/connection bar + a `NavigationView` left rail (Presets / Amps / IRs / Settings) with card-style content. The Presets page hosts the list + editor side by side.
```xml
<Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:Sonulab.App.ViewModels" xmlns:ui="using:FluentAvalonia.UI.Controls"
        x:Class="Sonulab.App.Views.MainWindow" x:DataType="vm:MainWindowViewModel"
        Width="1040" Height="680" Title="StompStation Manager">
  <DockPanel>
    <!-- Top status/connection bar -->
    <Border DockPanel.Dock="Top" Padding="12,8" Background="{DynamicResource SolidBackgroundFillColorSecondaryBrush}">
      <StackPanel Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
        <Button Command="{Binding Connection.ConnectCommand}">
          <StackPanel Orientation="Horizontal" Spacing="6">
            <ui:SymbolIcon Symbol="Sync"/><TextBlock Text="Connect"/>
          </StackPanel>
        </Button>
        <ui:SymbolIcon Symbol="{Binding Connection.IsConnected, Converter={x:Static vm:Icons.PlugState}}"/>
        <TextBlock Text="{Binding Connection.Status}" VerticalAlignment="Center"
                   Theme="{StaticResource BodyStrongTextBlockStyle}"/>
      </StackPanel>
    </Border>
    <!-- Dashboard nav rail -->
    <ui:NavigationView PaneDisplayMode="Left" IsSettingsVisible="True" IsBackButtonVisible="False">
      <ui:NavigationView.MenuItems>
        <ui:NavigationViewItem Content="Presets" IconSource="List" IsSelected="True">
          <Grid ColumnDefinitions="360,*" Margin="12">
            <ContentControl Grid.Column="0" Content="{Binding Presets}"/>
            <ContentControl Grid.Column="1" Content="{Binding Editor}" Margin="12,0,0,0"/>
          </Grid>
        </ui:NavigationViewItem>
        <ui:NavigationViewItem Content="Amps" IconSource="Audio"/>
        <ui:NavigationViewItem Content="IRs" IconSource="Volume"/>
      </ui:NavigationView.MenuItems>
    </ui:NavigationView>
  </DockPanel>
</Window>
```
> Notes: `vm:Icons.PlugState` is a tiny `IValueConverter` returning a `Symbol` (`Accept` when connected, `Cancel` when not) — implement it alongside the `Eq` converters. The Amps/IRs nav items are placeholders showing read-only lists in v1 (wire later); Presets is the working page. FluentAvalonia `IconSource` accepts built-in symbol names; adjust to available symbols if a name differs in the installed FluentAvalonia version.

- [ ] **Step 3: PresetListView.axaml** — list with move up/down/duplicate/delete + a busy overlay:
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Sonulab.App.ViewModels" xmlns:ui="using:FluentAvalonia.UI.Controls"
             x:Class="Sonulab.App.Views.PresetListView" x:DataType="vm:PresetListViewModel">
  <DockPanel>
    <!-- Icon-forward Fluent command bar (xmlns:ui="using:FluentAvalonia.UI.Controls") -->
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="4" Margin="4">
      <Button Command="{Binding MoveUpCommand}" ToolTip.Tip="Move up"><ui:SymbolIcon Symbol="ChevronUp"/></Button>
      <Button Command="{Binding MoveDownCommand}" ToolTip.Tip="Move down"><ui:SymbolIcon Symbol="ChevronDown"/></Button>
      <Button Command="{Binding DuplicateCommand}" ToolTip.Tip="Duplicate"><ui:SymbolIcon Symbol="Copy"/></Button>
      <Button Command="{Binding DeleteCommand}" ToolTip.Tip="Delete"><ui:SymbolIcon Symbol="Delete"/></Button>
      <Button Command="{Binding RefreshCommand}" ToolTip.Tip="Refresh"><ui:SymbolIcon Symbol="Refresh"/></Button>
    </StackPanel>
    <TextBlock DockPanel.Dock="Bottom" Text="{Binding BusyMessage}" IsVisible="{Binding IsBusy}" Margin="4"/>
    <ListBox ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:PresetItemViewModel">
          <TextBlock Text="{Binding DisplaySlot, StringFormat='{}{0:00}  '}"/>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```
> Note: the `DataTemplate` above shows only the slot number for brevity; bind a second `TextBlock` to `Name` in a horizontal `StackPanel` for the real layout. Provide a rename affordance (e.g. a TextBox + a "Rename" button bound to `RenameCommand` with the TextBox text as the command parameter).

- [ ] **Step 4: ParameterEditorView.axaml** — a scrollable list of fields with the right control per `Kind`:
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Sonulab.App.ViewModels" x:Class="Sonulab.App.Views.ParameterEditorView"
             x:DataType="vm:ParameterEditorViewModel">
  <DockPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="4">
      <Button Content="Load" Command="{Binding LoadCommand}"/>
      <Button Content="Save" Command="{Binding SaveCommand}"/>
    </StackPanel>
    <ScrollViewer>
      <ItemsControl ItemsSource="{Binding Fields}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:ParameterFieldViewModel">
            <Grid ColumnDefinitions="180,*" Margin="4,2">
              <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"/>
              <Panel Grid.Column="1">
                <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}"
                        IsVisible="{Binding Kind, Converter={x:Static vm:Eq.Float}}"/>
                <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"
                          IsVisible="{Binding Kind, Converter={x:Static vm:Eq.Enum}}"/>
                <TextBox Text="{Binding Text}"
                         IsVisible="{Binding Kind, Converter={x:Static vm:Eq.String}}"/>
              </Panel>
            </Grid>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
> Note: `vm:Eq.Float/Enum/String` are simple `IValueConverter`s comparing `Kind` to a constant (`plist` reuses the enum combo bound to a list fetched separately, or treat as string for v1). Implement a tiny `Eq` converter class, or replace the `IsVisible` bindings with a `DataTemplateSelector`/`Kind`-keyed templates — whichever the implementer finds cleaner in Avalonia 11. Wire `App.axaml.cs` to set `MainWindow.DataContext = new MainWindowViewModel()`.

- [ ] **Step 5: Build + run for manual verification**

Run: `dotnet build`
Then run the app (`dotnet run --project src/Sonulab.App`) with VoidX CLOSED and the pedal connected (operator step). Verify: Connect shows the banner; presets list populates; move up/down reorders (slow, busy message shows); duplicate/rename/delete work; the editor loads fields and Save persists. Record results in `docs/HARDWARE-VALIDATION-plan4.md`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): MainWindow + views wiring (manual-verified UI)"
```

---

## Self-review notes
- **Spec coverage (design spec §1,§5,§6):** connection/compat banner → Task 2/6; preset list + reorder/duplicate/rename/delete → Task 3/6; generic schema editor → Tasks 4–5/6; backup/restore is exposed via the existing `BackupService` (add buttons in Task 6 wiring or a follow-up). Amp/IR lists are read-only display (out of v1 scope per spec); `.nam` upload remains phase 2.
- **Testability:** all logic lives in ViewModels tested against `FakePresetDevice`/`FakeSonuLink`; only XAML Views need manual verification (Task 6).
- **Placeholder scan:** the only intentionally-open spots are Task 6's `MainWindowViewModel` `Client` wiring (a note tells the implementer to add `ConnectionViewModel.Client` and use it) and the `Eq` converters / template-selector choice — both are Avalonia-idiom decisions left to the implementer with explicit guidance. All ViewModel code (Tasks 2–5) is complete.
- **Type consistency:** VMs consume `DeviceRepository`/`ReorderService`/`SonuClient` exactly as defined in Plans 1–3b; `ParameterFieldViewModel`/`ParameterEditorViewModel` use `NodeSchema`/`NodeRecord` from Core.
- **Safety:** writes route through the verified services (reorder = atomic + rollback); the list VM no-ops writes when `writesAllowed` is false; long operations show a busy state.
```
