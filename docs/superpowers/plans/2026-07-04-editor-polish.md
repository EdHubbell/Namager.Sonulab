# Editor Polish Implementation Plan — ref-populated dropdowns + collapsed sections

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The editor's amp/IR picker params render populated dropdowns (options fetched from the device list the schema `ref`s), and editor blocks start collapsed with per-session expansion state preserved across preset switches.

**Architecture:** Three small changes along the existing browse→rebuild path: `ParameterFieldViewModel` accepts externally fetched ref options at construction; `ParameterEditorViewModel.LoadAsync` fetches each distinct `schema.Ref` list once per load via the existing `SonuClient.ReadListAsync`; `BlockSectionViewModel.IsExpanded` defaults false and the editor VM keeps a header-keyed expansion map it reapplies on every rebuild. No XAML changes, no new types beyond a dictionary.

**Tech Stack:** .NET 10, Avalonia 12 (untouched), CommunityToolkit.Mvvm, xUnit + `FakeSonuLink` (which already has `SeedList`/`SeedBrowse`).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-04-editor-polish-design.md` — binding decisions: options re-fetched on every preset load (bounded staleness, no cross-tab invalidation service); current value not in the fetched list is PREPENDED so the ComboBox still shows it; a failed/empty ref fetch degrades to today's rendering and never fails the editor load; expansion state is per-session (dictionary on the editor VM, no disk persistence); blocks are the only expanders (subgroups have none).
- No new NuGet packages; no XAML changes (the existing `Eq.EnumOrPlist` ComboBox template does the rendering).
- All commands run from repo root `C:\Development\Buckdrivers\Sonulab\StompStationManager` (PowerShell). `dotnet build` + `dotnet test` green after every task (~226 tests today). Commit prefix `editor-polish:`.
- Reference files (read before coding): `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs`, `ParameterFieldViewModel.cs`, `BlockSectionViewModel.cs`, `src/Sonulab.Core/Model/NodeSchema.cs` (has `Ref`), `src/Sonulab.Core/Transport/FakeSonuLink.cs` (`SeedList`), `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs` (test conventions to mirror).

## File Structure

```
src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs   (Task 1)  ctor gains refOptions
tests/Sonulab.App.Tests/ParameterFieldViewModelTests.cs (Task 1)  append tests
src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs  (Task 2 ref fetch; Task 3 expansion map)
tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs(Tasks 2-3) append tests
src/Sonulab.App/ViewModels/BlockSectionViewModel.cs     (Task 3)  IsExpanded default false
```

---

### Task 1: ParameterFieldViewModel accepts ref options

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ParameterFieldViewModel.cs`
- Test: `tests/Sonulab.App.Tests/ParameterFieldViewModelTests.cs` (append; file exists)

**Interfaces:**
- Consumes: `NodeSchema` (unchanged).
- Produces (Task 2 relies on): ctor `ParameterFieldViewModel(NodeSchema schema, string currentValueJson, IReadOnlyList<string>? refOptions = null)`. Rules: `refOptions` is ignored for `float` fields and when schema `Options` are non-empty; otherwise `Options` = refOptions with the current value PREPENDED if missing; a `string`-kind field (schema type `item`/unknown) with non-empty refOptions flips `Kind` to `"plist"` so the existing ComboBox template renders it; `refOptions` null/empty → behavior byte-identical to today.

- [ ] **Step 1: Write the failing tests (append to ParameterFieldViewModelTests.cs)**

```csharp
    // ---- ref-populated options (editor-polish Task 1) ----

    static Sonulab.Core.Model.NodeSchema Schema(string json, string path = @"root\app\amp\amp")
    {
        Assert.True(Sonulab.Core.Model.NodeRecord.TryParse(path + ":" + json, out var r));
        return Sonulab.Core.Model.NodeSchema.FromRecord(r);
    }

    [Fact]
    public void RefOptions_fill_empty_plist_options()
    {
        var s = Schema("{\"desc\":\"Amp model\",\"value\":\"Lead\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}");
        var f = new ParameterFieldViewModel(s, "\"Lead\"", new[] { "Clean", "Lead", "Rhythm" });
        Assert.Equal("plist", f.Kind);
        Assert.Equal(new[] { "Clean", "Lead", "Rhythm" }, f.Options);
        Assert.Equal("Lead", f.Text);
    }

    [Fact]
    public void Current_value_missing_from_ref_list_is_prepended()
    {
        var s = Schema("{\"desc\":\"Amp model\",\"value\":\"Deleted Amp\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}");
        var f = new ParameterFieldViewModel(s, "\"Deleted Amp\"", new[] { "Clean", "Lead" });
        Assert.Equal(new[] { "Deleted Amp", "Clean", "Lead" }, f.Options);
    }

    [Fact]
    public void Item_kind_with_ref_options_becomes_plist()
    {
        var s = Schema("{\"desc\":\"IR file\",\"value\":\"Cab1\",\"type\":\"item\",\"ref\":\"root\\\\ir\"}");
        var f = new ParameterFieldViewModel(s, "\"Cab1\"", new[] { "Cab1", "Cab2" });
        Assert.Equal("plist", f.Kind);
        Assert.Equal(new[] { "Cab1", "Cab2" }, f.Options);
    }

    [Fact]
    public void Schema_options_win_over_ref_options_and_float_ignores_them()
    {
        var enumS = Schema("{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}");
        var e = new ParameterFieldViewModel(enumS, "\"ON\"", new[] { "ShouldNotAppear" });
        Assert.Equal(new[] { "ON", "OFF" }, e.Options);

        var floatS = Schema("{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        var g = new ParameterFieldViewModel(floatS, "0.0", new[] { "ShouldNotAppear" });
        Assert.Equal("float", g.Kind);
        Assert.Empty(g.Options);
    }

    [Fact]
    public void Null_ref_options_keep_todays_behavior()
    {
        var s = Schema("{\"desc\":\"IR file\",\"value\":\"Cab1\",\"type\":\"item\",\"ref\":\"root\\\\ir\"}");
        var f = new ParameterFieldViewModel(s, "\"Cab1\"");
        Assert.Equal("string", f.Kind);
        Assert.Empty(f.Options);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterFieldViewModelTests`
Expected: FAIL — no 3-arg ctor.

- [ ] **Step 3: Implement**

In `ParameterFieldViewModel.cs`, replace the ctor signature and the `Options`/`Kind` assignment block (rest of the ctor unchanged):

```csharp
    public ParameterFieldViewModel(NodeSchema schema, string currentValueJson,
        IReadOnlyList<string>? refOptions = null)
    {
        Path = schema.Path;
        _label = string.IsNullOrEmpty(schema.Desc) ? schema.Path : schema.Desc;
        Min = schema.Min ?? 0; Max = schema.Max ?? 1;

        Kind = schema.Type switch
        {
            "float" => "float",
            "enum" => "enum",
            "plist" => "plist",
            "item" => "string",
            _ => "string",
        };

        // Options priority: the schema's own options; else externally fetched ref-list names
        // (amp/IR pickers — see editor-polish spec). Never for floats.
        if (schema.Options.Count > 0 || Kind == "float" || refOptions is not { Count: > 0 })
        {
            Options = schema.Options;
        }
        else
        {
            Options = refOptions;
            if (Kind == "string") Kind = "plist";           // item-typed ref field -> ComboBox template
        }

        var trimmed = currentValueJson.Trim();
        if (Kind == "float" && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            _number = n;
        else
            _text = trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2 ? trimmed[1..^1] : trimmed;

        // A ref-listed field whose current value vanished from the device list (e.g. deleted amp)
        // still shows its value: prepend it so the ComboBox can display the selection.
        if (!ReferenceEquals(Options, schema.Options) && _text is { Length: > 0 } t && !Options.Contains(t))
            Options = new[] { t }.Concat(Options).ToArray();

        _originalJson = ToJsonValue();
    }
```

(Note: `Options` must become a settable auto-property `public IReadOnlyList<string> Options { get; private set; }` — it is only assigned in the ctor, so no change notification is needed.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterFieldViewModelTests`
Expected: PASS (5 new + existing). Then the full App suite once — all green.

- [ ] **Step 5: Commit**

```powershell
git add src/Sonulab.App tests/Sonulab.App.Tests
git commit -m "editor-polish: ParameterFieldViewModel accepts ref-list options (current value preserved)"
```

---

### Task 2: LoadAsync fetches ref lists and feeds the fields

**Files:**
- Modify: `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs` (the `LoadAsync` body)
- Test: `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs` (append)

**Interfaces:**
- Consumes: Task 1's 3-arg ctor; `SonuClient.ReadListAsync(string path, CancellationToken)`; `NodeSchema.Ref` (`string?`).
- Produces: no new public surface — `LoadAsync` behavior only.

- [ ] **Step 1: Write the failing tests (append to ParameterEditorViewModelTests.cs)**

```csharp
    // ---- ref-populated dropdowns (editor-polish Task 2) ----

    static FakeSonuLink RefDev(params string[] ampNames)
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\amp:{\"desc\":\"Amp model\",\"value\":\"Lead\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        if (ampNames.Length > 0) d.SeedList(@"root\amp", ampNames);
        d.OpenAsync().GetAwaiter().GetResult();
        return d;
    }

    [Fact] public async Task Ref_field_gets_options_from_device_list()
    {
        var vm = VmFor(RefDev("Clean", "Lead", "", "", "Rhythm"));   // empties are slot padding
        await vm.LoadCommand.ExecuteAsync(null);
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Clean", "Lead", "Rhythm" }, field.Options);   // non-empty names only
        Assert.Equal("plist", field.Kind);
    }

    [Fact] public async Task Ref_field_with_deleted_current_value_still_shows_it()
    {
        var vm = VmFor(RefDev("Clean", "Rhythm"));                   // "Lead" not on the device
        await vm.LoadCommand.ExecuteAsync(null);
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Lead", "Clean", "Rhythm" }, field.Options);
    }

    [Fact] public async Task Missing_ref_list_degrades_without_failing_the_load()
    {
        var vm = VmFor(RefDev());                                    // no root\amp seeded at all
        await vm.LoadCommand.ExecuteAsync(null);                     // must not throw
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Empty(field.Options);                                 // renders as today
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterEditorViewModelTests`
Expected: FAIL — options empty on the first two tests (existing tests still pass).

- [ ] **Step 3: Implement**

In `ParameterEditorViewModel.LoadAsync`, after `var records = await _client.BrowseRecordsAsync(@"root\app");` insert the ref prefetch, and pass the options into the field ctor:

```csharp
        // Prefetch each distinct ref'd device list once per load (amp/IR pickers). A failed or
        // empty read degrades that field to today's rendering — the load itself never fails.
        var refOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var rec in records)
        {
            var schema = NodeSchema.FromRecord(rec);
            if (schema.Ref is not { Length: > 0 } r || refOptions.ContainsKey(r)) continue;
            if (!EditableTypes.Contains(schema.Type) && schema.Type != "item") continue;
            try
            {
                var names = await _client.ReadListAsync(r);
                refOptions[r] = names.Where(n => !string.IsNullOrEmpty(n)).ToArray();
            }
            catch { refOptions[r] = Array.Empty<string>(); }
        }
```

and change the field construction line to:

```csharp
                var labeled = new ParameterFieldViewModel(schema, value,
                    schema.Ref is { Length: > 0 } fr && refOptions.TryGetValue(fr, out var opts) && opts.Count > 0
                        ? opts : null);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterEditorViewModelTests`
Expected: PASS (3 new + existing 9). Full suite green.

- [ ] **Step 5: Commit**

```powershell
git add src/Sonulab.App tests/Sonulab.App.Tests
git commit -m "editor-polish: LoadAsync prefetches ref'd device lists to populate amp/IR dropdowns"
```

---

### Task 3: Collapsed by default + per-session expansion state

**Files:**
- Modify: `src/Sonulab.App/ViewModels/BlockSectionViewModel.cs:10` (default flip), `src/Sonulab.App/ViewModels/ParameterEditorViewModel.cs` (expansion map)
- Test: `tests/Sonulab.App.Tests/ParameterEditorViewModelTests.cs` (append)

**Interfaces:**
- Consumes: `BlockSectionViewModel.IsExpanded` (`[ObservableProperty]` — raises PropertyChanged).
- Produces: no new public surface.

- [ ] **Step 1: Write the failing tests (append to ParameterEditorViewModelTests.cs)**

```csharp
    // ---- collapsed-by-default + per-session expansion state (editor-polish Task 3) ----

    [Fact] public async Task Blocks_start_collapsed()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("P1");
        Assert.All(vm.Blocks, b => Assert.False(b.IsExpanded));
    }

    [Fact] public async Task Expansion_survives_preset_switch_per_block()
    {
        var dev = new FakeSonuLink();
        dev.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}",
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}");
        await dev.OpenAsync();
        var vm = VmFor(dev);
        await vm.LoadForCommand.ExecuteAsync("P1");
        vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded = true;

        await vm.LoadForCommand.ExecuteAsync("P2");          // rebuilds all sections
        Assert.True(vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded);
        Assert.False(vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase)).IsExpanded);
    }

    [Fact] public async Task Collapsing_again_is_also_remembered()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("P1");
        var block = vm.Blocks[0];
        block.IsExpanded = true;
        block.IsExpanded = false;
        await vm.LoadForCommand.ExecuteAsync("P2");
        Assert.False(vm.Blocks[0].IsExpanded);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterEditorViewModelTests`
Expected: `Blocks_start_collapsed` FAILS (default is true today); the survival test fails on the delay assertion.

- [ ] **Step 3: Implement**

`BlockSectionViewModel.cs:10`: change `private bool _isExpanded = true;` to `private bool _isExpanded;   // collapsed by default (editor-polish spec)`.

`ParameterEditorViewModel.cs`: add the field

```csharp
    // Per-session expansion memory, keyed by block header; reapplied on every rebuild
    // (preset switch). Intentionally NOT persisted to disk (spec decision).
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);
```

and in `LoadAsync`, where a section is added (`Blocks.Add(section);`), insert just before it:

```csharp
                section.IsExpanded = _expansion.TryGetValue(section.Header, out var exp) && exp;
                section.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(BlockSectionViewModel.IsExpanded) && s is BlockSectionViewModel b)
                        _expansion[b.Header] = b.IsExpanded;
                };
```

**Ordering caveat:** the section's `IsExpanded` must be assigned BEFORE the PropertyChanged subscription (as shown) so applying remembered state doesn't re-write the map — harmless either way, but keeps cause and effect one-directional.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests --filter ParameterEditorViewModelTests`
Expected: PASS (3 new). Then `dotnet build` + full `dotnet test` — all green (~237 expected).

- [ ] **Step 5: Manual smoke (device-free)**

Run: `dotnet run --project src/Sonulab.App` — app starts; nothing to click without a device, close cleanly. (The real eyeball — dropdown contents, collapse feel — happens with the pedal; add nothing to the hardware checklist: these are covered by normal use.)

- [ ] **Step 6: Commit**

```powershell
git add src/Sonulab.App tests/Sonulab.App.Tests
git commit -m "editor-polish: blocks collapsed by default with per-session expansion memory"
```
