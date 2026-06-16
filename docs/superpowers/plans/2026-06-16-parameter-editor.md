# Parameter Editor Implementation Plan (Feature A)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Group the parameter editor by effect block with headings, expose params via a forward-compatible blocklist, and label them from a swappable JSON translation file (device `desc` fallback).

**Architecture:** Two stateless services (`LabelService`, `ParameterExposure`) load embedded JSON. `ParameterEditorViewModel.LoadCommand` browses `root\app`, groups records into `BlockSectionViewModel`s (one per in-scope block; folder nodes become `SubGroupViewModel`s), applying the blocklist + labels. `SaveCommand` writes dirty fields across all blocks. All logic is unit-tested against `FakeSonuLink.SeedBrowse`; the View renders `Expander`s.

**Tech Stack:** .NET 10, Avalonia 12, CommunityToolkit.Mvvm, xUnit. Builds on the merged `Sonulab.App` (existing `ParameterEditorViewModel`/`ParameterFieldViewModel`) and `Sonulab.Core` (`SonuClient.BrowseRecordsAsync`, `NodeRecord`, `NodeSchema`).

**Spec:** `docs/superpowers/specs/2026-06-16-parameter-editor-design.md`. In-scope blocks: gate, exp, comp, amp, eq, ir, delay, reverb (Output skipped). Paths look like `root\app\<block>\<leaf>` or `root\app\<block>\<folder>\<leaf>`.

---

## Public API defined by this plan

```csharp
namespace Sonulab.App.Services;
public sealed class LabelService {
    public LabelService(IReadOnlyDictionary<string,string> map);
    public static LabelService Default { get; }                 // embedded labels.en.json
    public string Label(string path, string? deviceDesc);       // map -> desc -> prettified last segment
}
public sealed class ParameterExposure {
    public ParameterExposure(IReadOnlyList<string> hidden);
    public static ParameterExposure Default { get; }            // embedded hidden-params.json
    public bool IsHidden(string path);                          // exact | "prefix\..." | "*suffix"
}

namespace Sonulab.App.ViewModels;
public sealed partial class SubGroupViewModel : ObservableObject {
    public string Header { get; }
    public ObservableCollection<ParameterFieldViewModel> Fields { get; }
}
public sealed partial class BlockSectionViewModel : ObservableObject {
    public string Header { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<ParameterFieldViewModel> Fields { get; }      // block-level leaves
    public ObservableCollection<SubGroupViewModel> SubGroups { get; }
}
// ParameterEditorViewModel: Fields(flat) REPLACED by Blocks(grouped); ctor gains optional services.
public sealed partial class ParameterEditorViewModel : ObservableObject {
    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null, ParameterExposure? exposure = null);
    public ObservableCollection<BlockSectionViewModel> Blocks { get; }
    public bool IsDirty { get; }
    public string PresetName { get; set; }
    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
}
```

## File structure
```
src/Sonulab.App/
  Services/LabelService.cs          (create)
  Services/ParameterExposure.cs     (create)
  labels.en.json                    (create, EmbeddedResource)
  hidden-params.json                (create, EmbeddedResource)
  ViewModels/SubGroupViewModel.cs   (create)
  ViewModels/BlockSectionViewModel.cs (create)
  ViewModels/ParameterEditorViewModel.cs (modify: Fields -> Blocks)
  Views/ParameterEditorView.axaml(.cs)   (modify: Expander sections)
  Sonulab.App.csproj                (modify: embed the two json files)
tests/Sonulab.App.Tests/
  LabelServiceTests.cs
  ParameterExposureTests.cs
  ParameterEditorViewModelTests.cs  (modify: assert on Blocks)
```

The in-scope blocks and their order are a shared constant — define ONCE in `ParameterEditorViewModel`:
```csharp
public static readonly string[] Blocks = { "gate", "exp", "comp", "amp", "eq", "ir", "delay", "reverb" };
```

---

### Task 1: LabelService + labels.en.json

**Files:** Create `src/Sonulab.App/Services/LabelService.cs`, `src/Sonulab.App/labels.en.json`; Modify `src/Sonulab.App/Sonulab.App.csproj`; Test `tests/Sonulab.App.Tests/LabelServiceTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/LabelServiceTests.cs`:
```csharp
using Sonulab.App.Services;
using Xunit;

public class LabelServiceTests
{
    static LabelService Svc() => new(new Dictionary<string, string>
    {
        [@"root\app\delay\tcfolder"] = "Tone and Character",
    });

    [Fact] public void Uses_json_map_when_present() =>
        Assert.Equal("Tone and Character", Svc().Label(@"root\app\delay\tcfolder", "Tone and Character (dev)"));

    [Fact] public void Falls_back_to_device_desc() =>
        Assert.Equal("Threshold", Svc().Label(@"root\app\gate\threshold", "Threshold"));

    [Fact] public void Falls_back_to_prettified_segment_when_no_desc()
    {
        Assert.Equal("Lo Cut", Svc().Label(@"root\app\ir\lo_cut", null));
        Assert.Equal("Lo Cut", Svc().Label(@"root\app\ir\lo_cut", ""));
    }

    [Fact] public void Default_loads_embedded_map() =>
        Assert.False(string.IsNullOrEmpty(LabelService.Default.Label(@"root\app\gate", "Gate")));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter LabelServiceTests`
Expected: FAIL — `LabelService` does not exist.

- [ ] **Step 3: Create labels.en.json and embed it**

`src/Sonulab.App/labels.en.json` (starter overrides; device `desc` covers most — extend freely):
```json
{
  "root\\app\\exp": "Expression",
  "root\\app\\comp": "Compressor",
  "root\\app\\eq": "Equalizer",
  "root\\app\\ir": "Impulse Response",
  "root\\app\\delay\\tcfolder": "Tone and Character",
  "root\\app\\delay\\modfolder": "Modulation",
  "root\\app\\delay\\ddfolder": "Dual Delay",
  "root\\app\\mod\\tcfolder": "Tone and Character",
  "root\\app\\ir\\ir2": "Stereo IR"
}
```

In `src/Sonulab.App/Sonulab.App.csproj`, add inside an `<ItemGroup>`:
```xml
  <ItemGroup>
    <EmbeddedResource Include="labels.en.json" />
    <EmbeddedResource Include="hidden-params.json" />
  </ItemGroup>
```

- [ ] **Step 4: Implement LabelService**

`src/Sonulab.App/Services/LabelService.cs`:
```csharp
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Sonulab.App.Services;

public sealed class LabelService
{
    private readonly IReadOnlyDictionary<string, string> _map;
    public LabelService(IReadOnlyDictionary<string, string> map) => _map = map;

    public string Label(string path, string? deviceDesc)
    {
        if (_map.TryGetValue(path, out var mapped) && mapped.Length > 0) return mapped;
        if (!string.IsNullOrEmpty(deviceDesc)) return deviceDesc!;
        return Prettify(LastSegment(path));
    }

    private static string LastSegment(string path)
    {
        int i = path.LastIndexOf('\\');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string Prettify(string segment)
    {
        var sb = new StringBuilder();
        foreach (var word in segment.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1) sb.Append(word[1..]);
        }
        return sb.Length == 0 ? segment : sb.ToString();
    }

    private static readonly Lazy<LabelService> _default = new(() =>
    {
        var asm = typeof(LabelService).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("labels.en.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("labels.en.json not embedded — check Sonulab.App.csproj.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd()) ?? new();
        return new LabelService(map);
    });
    public static LabelService Default => _default.Value;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter LabelServiceTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): LabelService (json map -> device desc -> prettified) + labels.en.json"
```

---

### Task 2: ParameterExposure + hidden-params.json

**Files:** Create `src/Sonulab.App/Services/ParameterExposure.cs`, `src/Sonulab.App/hidden-params.json`; Test `tests/Sonulab.App.Tests/ParameterExposureTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/ParameterExposureTests.cs`:
```csharp
using Sonulab.App.Services;
using Xunit;

public class ParameterExposureTests
{
    static ParameterExposure Ex() => new(new[] { @"root\app\amp\sag", @"root\app\delay\ddfolder", @"*\_st" });

    [Fact] public void Exact_path_is_hidden() => Assert.True(Ex().IsHidden(@"root\app\amp\sag"));
    [Fact] public void Prefix_hides_descendants() =>
        Assert.True(Ex().IsHidden(@"root\app\delay\ddfolder\fdbkr"));
    [Fact] public void Prefix_does_not_hide_siblings() =>
        Assert.False(Ex().IsHidden(@"root\app\delay\fdbk"));
    [Fact] public void Suffix_glob_hides_by_ending() =>
        Assert.True(Ex().IsHidden(@"root\app\output\pst\ctl1\_st"));
    [Fact] public void Unlisted_is_shown() => Assert.False(Ex().IsHidden(@"root\app\gate\threshold"));
    [Fact] public void Default_loads_embedded() => Assert.NotNull(ParameterExposure.Default);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ParameterExposureTests`
Expected: FAIL — `ParameterExposure` does not exist.

- [ ] **Step 3: Create hidden-params.json**

`src/Sonulab.App/hidden-params.json` (start with status readouts hidden; extend freely):
```json
[
  "*\\_st"
]
```

- [ ] **Step 4: Implement ParameterExposure**

`src/Sonulab.App/Services/ParameterExposure.cs`:
```csharp
using System.Reflection;
using System.Text.Json;

namespace Sonulab.App.Services;

public sealed class ParameterExposure
{
    private readonly IReadOnlyList<string> _hidden;
    public ParameterExposure(IReadOnlyList<string> hidden) => _hidden = hidden;

    public bool IsHidden(string path)
    {
        foreach (var entry in _hidden)
        {
            if (entry.StartsWith("*", StringComparison.Ordinal))
            {
                if (path.EndsWith(entry[1..], StringComparison.Ordinal)) return true;
            }
            else if (path == entry || path.StartsWith(entry + "\\", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly Lazy<ParameterExposure> _default = new(() =>
    {
        var asm = typeof(ParameterExposure).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("hidden-params.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("hidden-params.json not embedded — check Sonulab.App.csproj.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var list = JsonSerializer.Deserialize<List<string>>(r.ReadToEnd()) ?? new();
        return new ParameterExposure(list);
    });
    public static ParameterExposure Default => _default.Value;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ParameterExposureTests`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): ParameterExposure blocklist (exact/prefix/suffix) + hidden-params.json"
```

---

### Task 3: BlockSectionViewModel + SubGroupViewModel

**Files:** Create `src/Sonulab.App/ViewModels/SubGroupViewModel.cs`, `src/Sonulab.App/ViewModels/BlockSectionViewModel.cs`. No separate test (exercised via `ParameterEditorViewModelTests` in Task 4).

- [ ] **Step 1: Implement the two container VMs**

`src/Sonulab.App/ViewModels/SubGroupViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sonulab.App.ViewModels;

public sealed partial class SubGroupViewModel : ObservableObject
{
    public string Header { get; }
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    public SubGroupViewModel(string header) => Header = header;
}
```

`src/Sonulab.App/ViewModels/BlockSectionViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sonulab.App.ViewModels;

public sealed partial class BlockSectionViewModel : ObservableObject
{
    public string Header { get; }
    [ObservableProperty] private bool _isExpanded = true;
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    public ObservableCollection<SubGroupViewModel> SubGroups { get; } = new();
    public BlockSectionViewModel(string header) => Header = header;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Sonulab.App`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(app): BlockSection + SubGroup view models"
```

---

### Task 4: ParameterEditorViewModel — group into blocks (replace flat Fields)

**Files:** Modify `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`; Modify `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`.

- [ ] **Step 1: Replace the tests with grouping assertions**

Replace the entire contents of `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs`:
```csharp
using Sonulab.App.Services;
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
            // amp block: a hidden leaf (sag), a visible float (gain), an enum (on_off)
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"unit\":\"dB\"}",
            "root\\app\\amp\\sag:{\"desc\":\"Sag\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":1.0}",
            // delay block with a folder (tcfolder) holding a leaf, plus a brand-new unmapped leaf
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\tcfolder:{\"desc\":\"Tone and Character\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\"}",
            "root\\app\\delay\\tcfolder\\tape:{\"desc\":\"Tape\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\newknob:{\"desc\":\"New Knob\",\"value\":1.0,\"type\":\"float\",\"min\":0.0,\"max\":10.0}",
            // output block must be skipped (out of scope)
            "root\\app\\output\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}");
        return d;
    }

    static ParameterEditorViewModel Vm(FakeSonuLink d) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"root\app\amp\sag" }));

    [Fact] public async Task Load_groups_into_blocks_in_order()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "amp", "delay" }, vm.Blocks.Select(b => b.Header.ToLowerInvariant()).ToArray());
    }

    [Fact] public async Task Hidden_param_is_excluded_but_new_param_appears()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var amp = vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(amp.Fields, f => f.Path.EndsWith(@"\sag"));     // blocklisted
        Assert.Contains(amp.Fields, f => f.Path.EndsWith(@"\gain"));
        var delay = vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(delay.Fields, f => f.Path.EndsWith(@"\newknob"));     // new/unmapped still shown
    }

    [Fact] public async Task Folder_nodes_become_subgroups()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var delay = vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase));
        var sub = Assert.Single(delay.SubGroups);
        Assert.Contains(sub.Fields, f => f.Path.EndsWith(@"\tape"));
    }

    [Fact] public async Task Output_block_is_skipped()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Blocks, b => b.Header.Equals("output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Save_writes_only_dirty_fields_across_blocks()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        vm.PresetName = "P";
        await vm.LoadCommand.ExecuteAsync(null);
        var gain = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\gain"));
        gain.Number = -6.0;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("-6", await new SonuClient(d).ReadValueAsync(@"root\app\amp\gain"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ParameterEditorViewModelTests`
Expected: FAIL — `Blocks`/grouping API does not exist yet.

- [ ] **Step 3: Rewrite ParameterEditorViewModel**

Replace `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public sealed partial class ParameterEditorViewModel : ObservableObject
{
    public static readonly string[] Blocks_InScope = { "gate", "exp", "comp", "amp", "eq", "ir", "delay", "reverb" };

    private readonly SonuClient _client;
    private readonly LabelService _labels;
    private readonly ParameterExposure _exposure;

    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null, ParameterExposure? exposure = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
    }

    public ObservableCollection<BlockSectionViewModel> Blocks { get; } = new();
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _presetName = "";

    private static readonly string[] EditableTypes = { "float", "enum", "plist" };

    [RelayCommand]
    private async Task LoadAsync()
    {
        Blocks.Clear();
        var records = await _client.BrowseRecordsAsync(@"root\app");

        foreach (var block in Blocks_InScope)
        {
            var prefix = @"root\app\" + block;
            var section = new BlockSectionViewModel(_labels.Label(prefix, DescOf(records, prefix)));
            var subgroups = new Dictionary<string, SubGroupViewModel>();

            foreach (var rec in records)
            {
                if (rec.Path != prefix && !rec.Path.StartsWith(prefix + "\\", StringComparison.Ordinal)) continue;
                var schema = NodeSchema.FromRecord(rec);
                if (!EditableTypes.Contains(schema.Type)) continue;     // skip folders/containers/modules
                if (_exposure.IsHidden(rec.Path)) continue;

                var seg = rec.Path.Split('\\');                          // [root, app, block, (folder?), leaf]
                var value = rec.Json.TryGetProperty("value", out var v) ? v.GetRawText() : "\"\"";
                var field = new ParameterFieldViewModel(schema, value) { /* Label set below */ };
                field.PropertyChanged += (_, _) => IsDirty = true;
                // Re-label using the service (desc is in the schema):
                var labeled = new ParameterFieldViewModel(schema, value);
                labeled.PropertyChanged += (_, _) => IsDirty = true;

                if (seg.Length == 4)                                     // root\app\block\leaf
                {
                    section.Fields.Add(labeled);
                }
                else                                                     // root\app\block\folder\...\leaf
                {
                    var folderPath = prefix + "\\" + seg[3];
                    if (!subgroups.TryGetValue(folderPath, out var sub))
                    {
                        sub = new SubGroupViewModel(_labels.Label(folderPath, DescOf(records, folderPath)));
                        subgroups[folderPath] = sub;
                        section.SubGroups.Add(sub);
                    }
                    sub.Fields.Add(labeled);
                }
            }

            if (section.Fields.Count > 0 || section.SubGroups.Count > 0)
                Blocks.Add(section);
        }
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in AllFields().Where(f => f.IsDirty))
            await _client.WriteAsync(f.Path, f.ToJsonValue());
        if (!string.IsNullOrEmpty(PresetName))
            await _client.SaveAsync(@"root\app\preset", PresetName);
        foreach (var f in AllFields()) f.MarkClean();
        IsDirty = false;
    }

    private IEnumerable<ParameterFieldViewModel> AllFields() =>
        Blocks.SelectMany(b => b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields)));

    private static string? DescOf(IReadOnlyList<NodeRecord> recs, string path)
    {
        foreach (var r in recs)
            if (r.Path == path && r.Json.TryGetProperty("desc", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                return d.GetString();
        return null;
    }
}
```

> Implementation note: `ParameterFieldViewModel` does not currently take a label override (its `Label` derives from the schema's `desc`). To honor the JSON map, give `ParameterFieldViewModel` an optional label: add a settable `public string Label { get; set; }` (it already exposes `Label`) and, after constructing `labeled`, set `labeled.Label = _labels.Label(rec.Path, ...desc...)`. Remove the unused `field` variable above — keep only `labeled`. If `ParameterFieldViewModel.Label` is currently get-only, change it to `{ get; set; }` (it's a display string; safe).

- [ ] **Step 4: Adjust ParameterFieldViewModel.Label to be settable + remove the dead `field` var**

In `src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs`, ensure `Label` is `public string Label { get; set; }` (was likely `{ get; }` set in ctor from `schema.Desc`). Keep the ctor default. In `LoadAsync` above, delete the unused `field` declaration and set `labeled.Label = _labels.Label(rec.Path, schema.Desc.Length > 0 ? schema.Desc : null);` before adding it.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter ParameterEditorViewModelTests`
Expected: PASS (5 tests). Then `dotnet test` for the whole suite — the previous flat-`Fields` tests are now replaced.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): ParameterEditorViewModel groups params into block sections + subgroups (labels + blocklist)"
```

---

### Task 5: ParameterEditorView.axaml — Expander sections (manual verification)

**Files:** Modify `src/Sonulab.App/Views/ParameterEditorView.axaml`. No unit test (UI); verified by build + running the app.

- [ ] **Step 1: Rewrite the view to render Blocks**

Replace `src/Sonulab.App/Views/ParameterEditorView.axaml` body with an Expander-per-block layout:
```xml
<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:Sonulab.App.ViewModels" xmlns:conv="using:Sonulab.App.Converters"
             x:Class="Sonulab.App.Views.ParameterEditorView" x:DataType="vm:ParameterEditorViewModel">
  <DockPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="8">
      <Button Content="Load" Command="{Binding LoadCommand}"/>
      <Button Content="Save" Command="{Binding SaveCommand}"/>
    </StackPanel>
    <ScrollViewer>
      <ItemsControl ItemsSource="{Binding Blocks}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:BlockSectionViewModel">
            <Expander Header="{Binding Header}" IsExpanded="{Binding IsExpanded}" Margin="4,2">
              <StackPanel>
                <ItemsControl ItemsSource="{Binding Fields}">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="vm:ParameterFieldViewModel">
                      <Grid ColumnDefinitions="180,*" Margin="4,2">
                        <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"/>
                        <Panel Grid.Column="1">
                          <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}"
                                  IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Float}}"/>
                          <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"
                                    IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Enum}}"/>
                          <TextBox Text="{Binding Text}" IsVisible="{Binding Kind, Converter={x:Static conv:Eq.String}}"/>
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
                                <TextBlock Grid.Column="0" Text="{Binding Label}" VerticalAlignment="Center"/>
                                <Panel Grid.Column="1">
                                  <Slider Minimum="{Binding Min}" Maximum="{Binding Max}" Value="{Binding Number}"
                                          IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Float}}"/>
                                  <ComboBox ItemsSource="{Binding Options}" SelectedItem="{Binding Text}"
                                            IsVisible="{Binding Kind, Converter={x:Static conv:Eq.Enum}}"/>
                                  <TextBox Text="{Binding Text}" IsVisible="{Binding Kind, Converter={x:Static conv:Eq.String}}"/>
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
</UserControl>
```
> Note: `conv:Eq.Float/Enum/String` are the existing converters from Task-6 (`Sonulab.App.Converters`). If their exact static accessor names differ, match them. If the project's `ParameterEditorView.axaml.cs` referenced the old flat `Fields`, it needs no code changes (pure XAML binding).

- [ ] **Step 2: Build**

Run: `dotnet build src/Sonulab.App`
Expected: `Build succeeded.` 0 errors.

- [ ] **Step 3: Full suite**

Run: `dotnet test`
Expected: PASS — Core + App (with the new editor tests).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(app): ParameterEditorView renders collapsible block sections + subgroups"
```

- [ ] **Step 5: (Operator) launch + eyeball** — `dotnet run --project src/Sonulab.App` (VoidX closed), Connect, open the editor: confirm one Expander per block with headings, folder sub-groups labeled, blocklisted params absent, Save persists. Record in a short note.

---

## Self-review notes
- **Spec coverage:** collapsible sections → Tasks 3–5; blocklist exposure → Task 2 + used in Task 4; labels (json→desc→prettified) → Task 1 + used in Task 4; 8 in-scope blocks + Output skipped → `Blocks_InScope` + Task 4 (Output-skipped test); folder sub-groups → Task 4 + view. Save-only-dirty preserved.
- **Placeholder scan:** none — all code complete. The two implementation notes (settable `Label`, drop the dead `field` var) are explicit fixes, not placeholders.
- **Type consistency:** `Blocks`/`BlockSectionViewModel`/`SubGroupViewModel`/`ParameterFieldViewModel` used consistently; `ParameterEditorViewModel(client, labels?, exposure?)` keeps `MainWindowViewModel`'s `new ParameterEditorViewModel(client)` working (defaults to `.Default`). `Eq.Float/Enum/String` reused from the existing converters.
- **Note for executor:** the existing `ParameterEditorViewModelTests` (flat `Fields`) is fully replaced in Task 4 Step 1 — net App test count stays ~11 (−2 old +5 new, plus Label/Exposure tests).
