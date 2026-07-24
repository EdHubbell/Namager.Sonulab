# Preset-Usage Highlight & Delete/Rename Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Highlight amp/IR files that are referenced by a preset, and block deleting or renaming a referenced file with a message naming the presets.

**Architecture:** A pure `PresetUsageMap` in `Sonulab.Core` builds a `name → [preset names]` index from occupied preset documents (matched by each node line's schema `ref`: `root\amp` / `root\ir`). An app-layer `PresetUsageService` reads presets off the device once (lazy, on first Amps/IR tab open), caches the map, and is invalidated when presets change. The amp/IR item VMs carry `UsedInPresets`; the list VMs highlight used rows, add a tooltip, and refuse delete/rename on used files.

**Tech Stack:** .NET 10, Avalonia 12 (built-in FluentTheme), CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- **.NET 10**; Avalonia **12** with the built-in `FluentTheme`. Do NOT add FluentAvalonia.
- **`Sonulab.Core` stays UI-free and fully unit-tested.** Highlighting logic that touches the device lives in the app layer.
- **Theme tokens only in `.axaml`** — use `Sonulab.*Brush` `DynamicResource`s (both light & dark are already defined in `Styles/SonulabTheme.axaml`); never hardcode hex.
- **Preset→amp/IR reference is by exact name** (trailing whitespace trimmed, case-sensitive) — this mirrors how the device resolves it. Names are unique per list.
- **Serial commands must not interleave.** The usage scan reads presets over the same link as amp/IR ops, so it only runs inside the existing busy-gated paths (`RunAsync`/`RefreshAsync`) or behind the `CanRefresh` gate — never concurrently with a write burst.
- **Device writes stay gated** by the existing `RunAsync`/`CanMutate` path. The new guards only *prevent* writes; they never introduce an unguarded one.
- Backward compatibility: every new constructor parameter is optional and defaults to a null-object, so existing tests keep compiling and passing.

---

### Task 1: `PresetUsageMap` (pure core)

**Files:**
- Create: `src/Sonulab.Core/Services/PresetUsageMap.cs`
- Test: `tests/Sonulab.Core.Tests/PresetUsageMapTests.cs`

**Interfaces:**
- Consumes: `Sonulab.Core.Model.PresetDocument` (`.Lines`), `NodeRecord.TryParse`, `NodeRecord.ValueString`, `NodeSchema.FromRecord(...).Ref`.
- Produces:
  - `readonly record struct PresetRef(int Index, string Name)` — `Index` is the 0-based slot.
  - `PresetUsageMap.Build(IEnumerable<(int SlotIndex, string PresetName, PresetDocument Doc)> occupiedPresets) → PresetUsageMap`
  - `PresetUsageMap.Empty` (static)
  - `IReadOnlyList<PresetRef> PresetsUsingAmp(string ampName)`
  - `IReadOnlyList<PresetRef> PresetsUsingIr(string irName)`
  - Presets are returned ordered by slot index **ascending**, distinct by slot.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Sonulab.Core.Tests/PresetUsageMapTests.cs
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageMapTests
{
    // Build a PresetDocument from raw node lines (as the device returns them).
    private static PresetDocument Doc(params string[] lines)
    {
        var text = string.Join("\r\n", lines);
        var blob = new byte[PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return PresetDocument.Parse(blob);
    }

    private const string AmpLine = @"root\app\amp\amp:{""desc"":""Amp model"",""value"":""{0}"",""type"":""plist"",""ref"":""root\\amp""}";
    private const string IrLine  = @"root\app\ir\ir:{""desc"":""Cab IR"",""value"":""{0}"",""type"":""plist"",""ref"":""root\\ir""}";

    private static string Amp(string name) => string.Format(AmpLine, name);
    private static string Ir(string name) => string.Format(IrLine, name);

    [Fact]
    public void Maps_amp_and_ir_names_to_the_presets_that_use_them()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "Lead",   Doc(Amp("Plexi"), Ir("V30"))),
            (6, "Rhythm", Doc(Amp("Plexi"), Ir("Greenback"))),
        });

        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(6, "Rhythm") }, map.PresetsUsingAmp("Plexi"));
        Assert.Equal(new[] { new PresetRef(0, "Lead") }, map.PresetsUsingIr("V30"));
        Assert.Equal(new[] { new PresetRef(6, "Rhythm") }, map.PresetsUsingIr("Greenback"));
        Assert.Empty(map.PresetsUsingAmp("Nonexistent"));
    }

    [Fact]
    public void Orders_presets_by_slot_ascending_regardless_of_input_order()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (11, "Solo",  Doc(Amp("Plexi"))),
            (2,  "Clean", Doc(Amp("Plexi"))),
            (6,  "Lead",  Doc(Amp("Plexi"))),
        });
        Assert.Equal(
            new[] { new PresetRef(2, "Clean"), new PresetRef(6, "Lead"), new PresetRef(11, "Solo") },
            map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public void Captures_multiple_ir_nodes_in_one_preset()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (3, "Big", Doc(
                @"root\app\ir\ir:{""value"":""CabA"",""ref"":""root\\ir""}",
                @"root\app\reverb\ir:{""value"":""RoomB"",""ref"":""root\\ir""}")),
        });
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("CabA"));
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("RoomB"));
    }

    [Fact]
    public void Dedupes_a_preset_that_references_the_same_amp_twice()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (4, "Dup", Doc(Amp("Plexi"), Amp("Plexi"))),
        });
        Assert.Equal(new[] { new PresetRef(4, "Dup") }, map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public void Skips_empty_values_and_non_ref_nodes()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "P", Doc(
                @"root\app\amp\amp:{""value"":"""",""ref"":""root\\amp""}",   // empty value
                @"root\app\gain\gain:{""value"":""5"",""type"":""num""}")),    // no ref
        });
        Assert.Empty(map.PresetsUsingAmp(""));
    }

    [Fact]
    public void Name_match_is_exact_but_trims_whitespace()
    {
        var map = PresetUsageMap.Build(new[] { (0, "P", Doc(Amp("Plexi"))) });
        Assert.Equal(new[] { new PresetRef(0, "P") }, map.PresetsUsingAmp("Plexi "));   // trimmed
        Assert.Empty(map.PresetsUsingAmp("plexi"));                                     // case-sensitive
    }

    [Fact]
    public void Empty_map_reports_nothing_used()
    {
        Assert.Empty(PresetUsageMap.Empty.PresetsUsingAmp("Plexi"));
        Assert.Empty(PresetUsageMap.Empty.PresetsUsingIr("V30"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetUsageMapTests`
Expected: FAIL to compile — `PresetUsageMap` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/Sonulab.Core/Services/PresetUsageMap.cs
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

/// <summary>One preset that references an amp/IR file. <see cref="Index"/> is the 0-based slot.</summary>
public readonly record struct PresetRef(int Index, string Name);

/// <summary>Which presets reference each amp / IR file, by NAME. Built once from the set of
/// occupied preset documents. Pure — no device I/O. A preset stores its amp/IR selection as a
/// node line whose schema <c>ref</c> is <c>root\amp</c> / <c>root\ir</c> and whose <c>value</c>
/// is the file name. Each result list is ordered by slot index ascending, distinct by slot.</summary>
public sealed class PresetUsageMap
{
    private const string AmpRef = @"root\amp";
    private const string IrRef = @"root\ir";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> _amp;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> _ir;

    private PresetUsageMap(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> amp,
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> ir)
    { _amp = amp; _ir = ir; }

    /// <summary>Nothing is used — the default before any scan.</summary>
    public static readonly PresetUsageMap Empty = new(
        new Dictionary<string, IReadOnlyList<PresetRef>>(),
        new Dictionary<string, IReadOnlyList<PresetRef>>());

    public IReadOnlyList<PresetRef> PresetsUsingAmp(string ampName) => Lookup(_amp, ampName);
    public IReadOnlyList<PresetRef> PresetsUsingIr(string irName) => Lookup(_ir, irName);

    private static IReadOnlyList<PresetRef> Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> map, string name) =>
        map.TryGetValue(name.Trim(), out var list) ? list : Array.Empty<PresetRef>();

    public static PresetUsageMap Build(IEnumerable<(int SlotIndex, string PresetName, PresetDocument Doc)> occupiedPresets)
    {
        var amp = new Dictionary<string, List<PresetRef>>();
        var ir = new Dictionary<string, List<PresetRef>>();

        foreach (var (slotIndex, presetName, doc) in occupiedPresets)
        {
            var entry = new PresetRef(slotIndex, presetName);
            foreach (var line in doc.Lines)
            {
                if (!NodeRecord.TryParse(line, out var rec)) continue;
                var reference = NodeSchema.FromRecord(rec).Ref;
                var target = reference switch { AmpRef => amp, IrRef => ir, _ => (Dictionary<string, List<PresetRef>>?)null };
                if (target is null) continue;

                var value = rec.ValueString?.Trim();
                if (string.IsNullOrEmpty(value)) continue;

                if (!target.TryGetValue(value, out var list)) target[value] = list = new List<PresetRef>();
                if (!list.Any(r => r.Index == slotIndex)) list.Add(entry);   // dedupe by slot
            }
        }

        return new PresetUsageMap(Freeze(amp), Freeze(ir));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PresetRef>> Freeze(Dictionary<string, List<PresetRef>> src)
    {
        var result = new Dictionary<string, IReadOnlyList<PresetRef>>(src.Count);
        foreach (var (k, v) in src)
        {
            v.Sort((a, b) => a.Index.CompareTo(b.Index));   // ascending by slot
            result[k] = v;
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetUsageMapTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/PresetUsageMap.cs tests/Sonulab.Core.Tests/PresetUsageMapTests.cs
git commit -m "feat(core): PresetUsageMap — index presets by amp/IR name"
```

---

### Task 2: `PresetUsageService` (app-layer caching)

**Files:**
- Create: `src/Namager.App/Services/PresetUsageService.cs`
- Test: `tests/Namager.App.Tests/PresetUsageServiceTests.cs`

**Interfaces:**
- Consumes: `Sonulab.Core.Services.DeviceRepository` (`ListPresetsAsync`, `ReadPresetAsync`), `PresetUsageMap.Build`, `Namager.App.Services.IStatusService`.
- Produces:
  - `interface IPresetUsageService { Task<PresetUsageMap> GetAsync(); void Invalidate(); }`
  - `class PresetUsageService(DeviceRepository repo, IStatusService? status = null)`
  - `class NullPresetUsageService` with `static NullPresetUsageService Instance` (returns `PresetUsageMap.Empty`, no-op `Invalidate`).
  - `static class PresetRefFormat { string Join(IReadOnlyList<PresetRef>) }` → `"03 Clean, 07 Lead"` (slot-ascending, shared by tooltips + block messages).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Namager.App.Tests/PresetUsageServiceTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageServiceTests
{
    private const string AmpNode = @"root\app\amp\amp:{""value"":""{0}"",""ref"":""root\\amp""}";
    private static string Amp(string name) => string.Format(AmpNode, name);

    private static (PresetUsageService svc, DeviceRepository repo, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        // slot 2 empty on purpose
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        return (new PresetUsageService(repo), repo, dev);
    }

    [Fact]
    public async Task GetAsync_builds_the_map_from_occupied_presets_with_slots()
    {
        var (svc, _, _) = Make();
        var map = await svc.GetAsync();
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task GetAsync_caches_and_does_not_reread_until_invalidated()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        await dev.OpenAsync();
        var link = new CountingLink(dev);
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(link)));

        await svc.GetAsync();
        int afterFirst = link.Dreads;
        Assert.True(afterFirst > 0, "first build must read preset content");

        await svc.GetAsync();
        Assert.Equal(afterFirst, link.Dreads);          // cache hit: no new reads

        svc.Invalidate();
        await svc.GetAsync();
        Assert.True(link.Dreads > afterFirst);          // rebuild after invalidation
    }

    [Fact]
    public async Task GetAsync_reports_a_status_scope()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        await dev.OpenAsync();
        var status = new FakeStatusService();
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(dev)), status);
        await svc.GetAsync();
        Assert.Contains(status.Begun, m => m.Contains("preset usage"));
    }

    // Counts content reads so we can prove caching.
    private sealed class CountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public CountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter PresetUsageServiceTests`
Expected: FAIL to compile — `PresetUsageService` / `IPresetUsageService` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// src/Namager.App/Services/PresetUsageService.cs
using System.Linq;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Namager.App.Services;

public interface IPresetUsageService
{
    /// <summary>Cached map of which presets use each amp/IR. Built on first call by reading
    /// every occupied preset document off the device; cached until <see cref="Invalidate"/>.</summary>
    Task<PresetUsageMap> GetAsync();

    /// <summary>Mark the cache stale — next <see cref="GetAsync"/> rebuilds. Call after any
    /// preset mutation (write/reorder/delete/duplicate/rename).</summary>
    void Invalidate();
}

/// <summary>Reads presets off the device once and caches the usage map. Shared by the preset,
/// amp and IR list VMs. Not concurrency-hardened: a double GetAsync may scan twice (idempotent,
/// harmless) — in practice calls are serialized by the busy-gated VM paths that invoke it.</summary>
public sealed class PresetUsageService : IPresetUsageService
{
    private readonly DeviceRepository _repo;
    private readonly IStatusService _status;
    private PresetUsageMap? _cache;

    public PresetUsageService(DeviceRepository repo, IStatusService? status = null)
    { _repo = repo; _status = status ?? NullStatusService.Instance; }

    public void Invalidate() => _cache = null;

    public async Task<PresetUsageMap> GetAsync()
    {
        if (_cache is { } cached) return cached;

        using var op = _status.BeginOperation("Checking preset usage…");
        var slots = await _repo.ListPresetsAsync();
        var docs = new List<(int, string, PresetDocument)>();
        foreach (var s in slots)
        {
            if (s.IsEmpty) continue;
            docs.Add((s.Index, s.Name, await _repo.ReadPresetAsync(s.Index)));
        }
        return _cache = PresetUsageMap.Build(docs);
    }
}

/// <summary>No-op fallback so a VM constructed without a usage service (existing tests) works —
/// nothing is ever "used".</summary>
public sealed class NullPresetUsageService : IPresetUsageService
{
    public static readonly NullPresetUsageService Instance = new();
    public Task<PresetUsageMap> GetAsync() => Task.FromResult(PresetUsageMap.Empty);
    public void Invalidate() { }
}

/// <summary>Formats preset references for display: "NN Name" (1-based, zero-padded slot to match
/// the preset list), joined by ", ", in the slot-ascending order the map already returns. Shared
/// by the amp/IR item tooltips and the delete/rename block message.</summary>
public static class PresetRefFormat
{
    public static string Join(IReadOnlyList<PresetRef> refs) =>
        string.Join(", ", refs.Select(r => $"{r.Index + 1:00} {r.Name}"));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter PresetUsageServiceTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/PresetUsageService.cs tests/Namager.App.Tests/PresetUsageServiceTests.cs
git commit -m "feat(app): PresetUsageService — lazy cached preset-usage index"
```

---

### Task 3: `UsedInPresets` on the amp/IR item VMs

**Files:**
- Modify: `src/Namager.App/ViewModels/AmpItemViewModel.cs`
- Modify: `src/Namager.App/ViewModels/IrItemViewModel.cs`
- Test: `tests/Namager.App.Tests/ItemUsageTests.cs` (new)

**Interfaces:**
- Consumes: `Sonulab.Core.Services.PresetRef`, `Namager.App.Services.PresetRefFormat.Join`.
- Produces (on both `AmpItemViewModel` and `IrItemViewModel`):
  - `IReadOnlyList<PresetRef> UsedInPresets` (settable `[ObservableProperty]`, default empty)
  - `bool IsUsed => UsedInPresets.Count > 0`
  - `string? UsedInTooltip` — `"Used in: 03 A, 07 B"` when used, else `null`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Namager.App.Tests/ItemUsageTests.cs
using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class ItemUsageTests
{
    [Fact]
    public void Amp_item_reports_used_state_and_tooltip_with_slots()
    {
        var item = new AmpItemViewModel(new AmpSlot(0, "Plexi"));
        Assert.False(item.IsUsed);
        Assert.Null(item.UsedInTooltip);

        item.UsedInPresets = new[] { new PresetRef(2, "Lead"), new PresetRef(6, "Rhythm") };
        Assert.True(item.IsUsed);
        Assert.Equal("Used in: 03 Lead, 07 Rhythm", item.UsedInTooltip);
    }

    [Fact]
    public void Ir_item_reports_used_state_and_tooltip_with_slots()
    {
        var item = new IrItemViewModel(new SlotEntry(0, "V30"));
        Assert.False(item.IsUsed);

        item.UsedInPresets = new[] { new PresetRef(0, "Clean") };
        Assert.True(item.IsUsed);
        Assert.Equal("Used in: 01 Clean", item.UsedInTooltip);
    }

    [Fact]
    public void Setting_used_presets_raises_change_notifications()
    {
        var item = new AmpItemViewModel(new AmpSlot(0, "Plexi"));
        var changed = new System.Collections.Generic.List<string>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        item.UsedInPresets = new[] { new PresetRef(0, "Lead") };
        Assert.Contains(nameof(item.IsUsed), changed);
        Assert.Contains(nameof(item.UsedInTooltip), changed);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter ItemUsageTests`
Expected: FAIL to compile — `UsedInPresets` / `IsUsed` / `UsedInTooltip` do not exist.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/AmpItemViewModel.cs`, first add these usings under the existing `using Sonulab.Core.Model;`:

```csharp
using Sonulab.Core.Services;      // PresetRef
using Namager.App.Services;       // PresetRefFormat
```

Then add these members inside the class (after the `_editName` property, before `BeginRename`):

```csharp
    /// <summary>Presets that reference this amp (set by the list VM after a usage scan). Empty = unused.</summary>
    [ObservableProperty] private IReadOnlyList<PresetRef> _usedInPresets = System.Array.Empty<PresetRef>();
    public bool IsUsed => UsedInPresets.Count > 0;
    public string? UsedInTooltip => IsUsed ? "Used in: " + PresetRefFormat.Join(UsedInPresets) : null;
    partial void OnUsedInPresetsChanged(IReadOnlyList<PresetRef> value)
    { OnPropertyChanged(nameof(IsUsed)); OnPropertyChanged(nameof(UsedInTooltip)); }
```

Apply the identical usings + block to `src/Namager.App/ViewModels/IrItemViewModel.cs` (same code — the property is type-agnostic).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter ItemUsageTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/AmpItemViewModel.cs src/Namager.App/ViewModels/IrItemViewModel.cs tests/Namager.App.Tests/ItemUsageTests.cs
git commit -m "feat(app): amp/IR item VMs carry UsedInPresets + tooltip"
```

---

### Task 4: AmpListViewModel — highlight + delete/rename guards

**Files:**
- Modify: `src/Namager.App/ViewModels/AmpListViewModel.cs`
- Create: `tests/Namager.App.Tests/FakePresetUsageService.cs`
- Test: `tests/Namager.App.Tests/AmpListViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `Namager.App.Services.IPresetUsageService`, `PresetUsageMap.PresetsUsingAmp`, `AmpItemViewModel.UsedInPresets`.
- Produces: new optional ctor param `IPresetUsageService? usage = null` (appended, last); `public Task RefreshUsageAsync()`.

- [ ] **Step 1: Write the failing test**

First create the shared fake:

```csharp
// tests/Namager.App.Tests/FakePresetUsageService.cs
using Namager.App.Services;
using Sonulab.Core.Services;

/// <summary>Controllable usage service for VM tests: set <see cref="Map"/>, observe calls.</summary>
public sealed class FakePresetUsageService : IPresetUsageService
{
    public PresetUsageMap Map { get; set; } = PresetUsageMap.Empty;
    public int InvalidateCount { get; private set; }
    public int GetCount { get; private set; }
    public System.Threading.Tasks.Task<PresetUsageMap> GetAsync()
    { GetCount++; return System.Threading.Tasks.Task.FromResult(Map); }
    public void Invalidate() { InvalidateCount++; }

    // Build a map from raw amp/IR node lines. Each preset carries its 0-based slot.
    public static PresetUsageMap MapFor(params (int Slot, string Preset, string[] Lines)[] presets)
    {
        var docs = new System.Collections.Generic.List<(int, string, Sonulab.Core.Model.PresetDocument)>();
        foreach (var (slot, preset, lines) in presets)
        {
            var text = string.Join("\r\n", lines);
            var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
            System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
            docs.Add((slot, preset, Sonulab.Core.Model.PresetDocument.Parse(blob)));
        }
        return PresetUsageMap.Build(docs);
    }

    public static string AmpLine(string name) => $@"root\app\amp\amp:{{""value"":""{name}"",""ref"":""root\\amp""}}";
    public static string IrLine(string name) => $@"root\app\ir\ir:{{""value"":""{name}"",""ref"":""root\\ir""}}";
}
```

Then add these cases to `tests/Namager.App.Tests/AmpListViewModelTests.cs`. Add a helper that injects a usage service, then the tests:

```csharp
    // ---- preset-usage highlight & guards (Task 4) ----

    private (AmpListViewModel vm, FakeAmpDevice dev, FakePresetUsageService usage) MakeWithUsage(
        FakePresetUsageService usage)
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", RealisticBlob(1));
        dev.SeedAmp(1, "Crunch", RealisticBlob(2));
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new AmpListViewModel(svc, writesAllowed: true, usage: usage), dev, usage);
    }

    [Fact]
    public async Task Refresh_marks_used_amps()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }))
        };
        var (vm, _, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.Items[0].IsUsed);                          // "Clean" used by "Lead"
        Assert.Equal(new[] { new PresetRef(6, "Lead") }, vm.Items[0].UsedInPresets);
        Assert.False(vm.Items[1].IsUsed);                         // "Crunch" unused
    }

    [Fact]
    public async Task Delete_of_a_used_amp_is_blocked_and_lists_slots_ascending()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor(
                (11, "Solo", new[] { FakePresetUsageService.AmpLine("Clean") }),   // higher slot, listed second
                (6,  "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }))
        };
        var (vm, dev, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                                // "Clean"

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Clean", dev.SlotNames[0]);                 // NOT deleted
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("used in the following presets", vm.ErrorMessage);
        // slot number + name, ascending: "07 Lead" before "12 Solo"
        Assert.Contains("07 Lead, 12 Solo", vm.ErrorMessage);
    }

    [Fact]
    public async Task Delete_of_an_unused_amp_proceeds()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }))
        };
        var (vm, dev, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];                                // "Crunch" — unused

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Null(dev.SlotNames[1]);                           // deleted
        Assert.True(vm.Items[1].IsEmpty);
    }

    [Fact]
    public async Task Rename_of_a_used_amp_is_blocked()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }))
        };
        var (vm, dev, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];                                   // "Clean"
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Cleaner";

        await vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.Equal("Clean", dev.SlotNames[0]);                 // NOT renamed
        Assert.False(item.IsEditing);                            // left edit mode
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("07 Lead", vm.ErrorMessage);
    }

    [Fact]
    public async Task RefreshUsage_reapplies_without_relisting_amps()
    {
        var usage = new FakePresetUsageService();                // starts empty
        var (vm, dev, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.Items[0].IsUsed);
        int listReads = dev.CommandLog.Count(c => c == @"read root\amp");

        usage.Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }));
        await vm.RefreshUsageAsync();

        Assert.True(vm.Items[0].IsUsed);                         // highlight refreshed
        Assert.Equal(listReads, dev.CommandLog.Count(c => c == @"read root\amp"));  // no amp re-list
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter AmpListViewModelTests`
Expected: FAIL to compile — the `usage:` ctor argument, `RefreshUsageAsync`, and the guard behavior do not exist yet.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/AmpListViewModel.cs`:

3a. Add a field and extend the constructor. Change the field block near the top (after `private readonly Namager.App.Services.IStatusService _status;`) to add:

```csharp
    private readonly Namager.App.Services.IPresetUsageService _usage;
```

Change the constructor signature to append the parameter and assign it. Replace:

```csharp
    public AmpListViewModel(AmpService amps, bool writesAllowed,
        Namager.App.Services.IStatusService? status = null,
        DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null)
    {
        _amps = amps; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _distill = distill ?? Sonulab.Distill.Distiller.DistillAsync;
        _distilledDir = distilledDir ?? Path.Combine("NAMFiles", "Distilled");
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
    }
```

with:

```csharp
    public AmpListViewModel(AmpService amps, bool writesAllowed,
        Namager.App.Services.IStatusService? status = null,
        DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null,
        Namager.App.Services.IPresetUsageService? usage = null)
    {
        _amps = amps; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _distill = distill ?? Sonulab.Distill.Distiller.DistillAsync;
        _distilledDir = distilledDir ?? Path.Combine("NAMFiles", "Distilled");
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance;
    }
```

3b. Apply usage at the end of `ReloadAsync`. Replace:

```csharp
    private async Task ReloadAsync()
    {
        _detailsCts?.Cancel();      // an in-flight details read must not repopulate the cache below
        _detailsCache.Clear();
        var slots = await _amps.ListAmpsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new AmpItemViewModel(s));
    }
```

with:

```csharp
    private async Task ReloadAsync()
    {
        _detailsCts?.Cancel();      // an in-flight details read must not repopulate the cache below
        _detailsCache.Clear();
        var slots = await _amps.ListAmpsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new AmpItemViewModel(s));
        await ApplyUsageAsync();
    }

    /// <summary>Tag each item with the presets that use it. Highlighting is best-effort: a preset
    /// read failure must never break the amp list, so its errors are swallowed (logged).</summary>
    private async Task ApplyUsageAsync()
    {
        try
        {
            var map = await _usage.GetAsync();
            foreach (var item in Items)
                item.UsedInPresets = item.IsEmpty
                    ? System.Array.Empty<string>() : map.PresetsUsingAmp(item.Name);
        }
        catch (Exception ex) { Log.Warn(ex, "amp preset-usage lookup failed"); }
    }

    /// <summary>Re-apply preset-usage highlighting without re-listing amps (cheap: cached map, or a
    /// preset re-scan if the usage cache was invalidated). Called on tab revisit after preset edits.</summary>
    public async Task RefreshUsageAsync()
    {
        if (!CanRefresh) return;
        await ApplyUsageAsync();
    }
```

3c. Add the guards. Replace the `DeleteAsync` and `CommitRenameAsync` commands:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }
```

with:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        if (s.UsedInPresets.Count > 0) { BlockUsed(s, "delete"); return; }
        await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (s.UsedInPresets.Count > 0) { s.IsEditing = false; BlockUsed(s, "rename"); return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }

    /// <summary>Refuse a delete/rename of an amp a preset references, and say which presets.
    /// Renaming/deleting it would leave those presets pointing at a name the device can't resolve.</summary>
    private void BlockUsed(AmpItemViewModel s, string verb)
    {
        var presets = s.UsedInPresets;
        ErrorMessage =
            $"This amp file is used in the following presets: {Namager.App.Services.PresetRefFormat.Join(presets)}. " +
            $"You can only {verb} files that aren't in an active preset.";
        _status.Failure($"Can't {verb} '{s.Name}' — used by {presets.Count} preset{(presets.Count == 1 ? "" : "s")}.");
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter AmpListViewModelTests`
Expected: PASS — the new cases plus all pre-existing amp tests (existing tests use the default `NullPresetUsageService`, so nothing is "used" and their behavior is unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/AmpListViewModel.cs tests/Namager.App.Tests/FakePresetUsageService.cs tests/Namager.App.Tests/AmpListViewModelTests.cs
git commit -m "feat(app): amp list highlights used files, blocks delete/rename of them"
```

---

### Task 5: IrListViewModel — highlight + delete/rename guards

**Files:**
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs`
- Test: `tests/Namager.App.Tests/IrListViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `IPresetUsageService`, `PresetUsageMap.PresetsUsingIr`, `IrItemViewModel.UsedInPresets`, the `FakePresetUsageService` from Task 4.
- Produces: new optional ctor param `IPresetUsageService? usage = null` (appended, last); `public Task RefreshUsageAsync()`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Namager.App.Tests/IrListViewModelTests.cs`:

```csharp
    // ---- preset-usage highlight & guards (Task 5) ----

    private (IrListViewModel vm, FakeIrDevice dev) MakeWithUsage(FakePresetUsageService usage)
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "V30", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.SeedIr(1, "Greenback", Enumerable.Repeat((byte)2, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new IrListViewModel(svc, writesAllowed: true, usage: usage), dev);
    }

    [Fact] public async Task Refresh_marks_used_irs()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.Items[0].IsUsed);                          // "V30" used by "Clean"
        Assert.False(vm.Items[1].IsUsed);                         // "Greenback" unused
    }

    [Fact] public async Task Delete_of_a_used_ir_is_blocked_with_a_message()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("V30", dev.SlotNames[0]);                   // NOT deleted
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("IR file is used", vm.ErrorMessage);
        Assert.Contains("03 Clean", vm.ErrorMessage);            // slot number + name
    }

    [Fact] public async Task Delete_of_an_unused_ir_proceeds()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];                                // "Greenback" — unused
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(dev.SlotNames[1]);
        Assert.True(vm.Items[1].IsEmpty);
    }

    [Fact] public async Task Rename_of_a_used_ir_is_blocked()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "V-30";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("V30", dev.SlotNames[0]);                   // NOT renamed
        Assert.False(item.IsEditing);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("03 Clean", vm.ErrorMessage);
    }

    [Fact] public async Task RefreshUsage_reapplies_without_relisting_irs()
    {
        var usage = new FakePresetUsageService();
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.Items[0].IsUsed);
        int listReads = dev.CommandLog.Count(c => c == @"read root\ir");

        usage.Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }));
        await vm.RefreshUsageAsync();

        Assert.True(vm.Items[0].IsUsed);
        Assert.Equal(listReads, dev.CommandLog.Count(c => c == @"read root\ir"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter IrListViewModelTests`
Expected: FAIL to compile — `usage:` param, `RefreshUsageAsync`, and guard behavior don't exist yet.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/IrListViewModel.cs`:

3a. Add the field after `private readonly Namager.App.Services.IStatusService _status;`:

```csharp
    private readonly Namager.App.Services.IPresetUsageService _usage;
```

Extend the constructor. Replace:

```csharp
    public IrListViewModel(IrService irs, bool writesAllowed,
                           Namager.App.Services.IStatusService? status = null,
                           Func<string, byte[]>? convertWav = null)
    {
        _irs = irs; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _convertWav = convertWav ?? Sonulab.Distill.WavToIr.Convert;
    }
```

with:

```csharp
    public IrListViewModel(IrService irs, bool writesAllowed,
                           Namager.App.Services.IStatusService? status = null,
                           Func<string, byte[]>? convertWav = null,
                           Namager.App.Services.IPresetUsageService? usage = null)
    {
        _irs = irs; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _convertWav = convertWav ?? Sonulab.Distill.WavToIr.Convert;
        _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance;
    }
```

3b. Apply usage in `ReloadAsync` and add the helpers. Replace:

```csharp
    private async Task ReloadAsync()
    {
        var slots = await _irs.ListIrsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new IrItemViewModel(s));
    }
```

with:

```csharp
    private async Task ReloadAsync()
    {
        var slots = await _irs.ListIrsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new IrItemViewModel(s));
        await ApplyUsageAsync();
    }

    /// <summary>Tag each item with the presets that use it. Best-effort: a preset read failure must
    /// never break the IR list, so its errors are swallowed (logged).</summary>
    private async Task ApplyUsageAsync()
    {
        try
        {
            var map = await _usage.GetAsync();
            foreach (var item in Items)
                item.UsedInPresets = item.IsEmpty
                    ? System.Array.Empty<string>() : map.PresetsUsingIr(item.Name);
        }
        catch (Exception ex) { Log.Warn(ex, "IR preset-usage lookup failed"); }
    }

    /// <summary>Re-apply preset-usage highlighting without re-listing IRs (cached map, or a preset
    /// re-scan if invalidated). Called on tab revisit after preset edits.</summary>
    public async Task RefreshUsageAsync()
    {
        if (!CanRefresh) return;
        await ApplyUsageAsync();
    }
```

3c. Add the guards. Replace:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _irs.DeleteIrAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(IrItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _irs.RenameIrAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }
```

with:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        if (s.UsedInPresets.Count > 0) { BlockUsed(s, "delete"); return; }
        await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _irs.DeleteIrAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(IrItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (s.UsedInPresets.Count > 0) { s.IsEditing = false; BlockUsed(s, "rename"); return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _irs.RenameIrAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }

    /// <summary>Refuse a delete/rename of an IR a preset references, and say which presets.</summary>
    private void BlockUsed(IrItemViewModel s, string verb)
    {
        var presets = s.UsedInPresets;
        ErrorMessage =
            $"This IR file is used in the following presets: {Namager.App.Services.PresetRefFormat.Join(presets)}. " +
            $"You can only {verb} files that aren't in an active preset.";
        _status.Failure($"Can't {verb} '{s.Name}' — used by {presets.Count} preset{(presets.Count == 1 ? "" : "s")}.");
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter IrListViewModelTests`
Expected: PASS — new cases plus all pre-existing IR tests unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/IrListViewModel.cs tests/Namager.App.Tests/IrListViewModelTests.cs
git commit -m "feat(app): IR list highlights used files, blocks delete/rename of them"
```

---

### Task 6: PresetListViewModel invalidates the usage cache

**Files:**
- Modify: `src/Namager.App/ViewModels/PresetListViewModel.cs`
- Test: `tests/Namager.App.Tests/PresetListViewModelTests.cs` (add a case)

**Interfaces:**
- Consumes: `IPresetUsageService.Invalidate()`, `FakePresetUsageService` (Task 4).
- Produces: new optional ctor param `IPresetUsageService? usage = null` (appended, last). Invalidation fires on every successful preset mutation.

- [ ] **Step 1: Write the failing test**

Add to `tests/Namager.App.Tests/PresetListViewModelTests.cs`:

```csharp
    [Fact] public async Task Successful_mutation_invalidates_preset_usage()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var usage = new FakePresetUsageService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, usage: usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.True(usage.InvalidateCount >= 1);       // preset changed → usage cache is stale
    }

    [Fact] public async Task Refresh_does_not_invalidate_usage()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var usage = new FakePresetUsageService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, usage: usage);

        await vm.RefreshCommand.ExecuteAsync(null);     // read-only refresh

        Assert.Equal(0, usage.InvalidateCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter PresetListViewModelTests`
Expected: FAIL to compile — the `usage:` ctor argument does not exist.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/PresetListViewModel.cs`:

3a. Add a field after `private readonly Namager.App.Services.IStatusService _status;`:

```csharp
    private readonly Namager.App.Services.IPresetUsageService _usage;
```

Extend the constructor. Replace:

```csharp
    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed,
                               Namager.App.Services.IStatusService? status = null)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed; _status = status ?? Namager.App.Services.NullStatusService.Instance; }
```

with:

```csharp
    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed,
                               Namager.App.Services.IStatusService? status = null,
                               Namager.App.Services.IPresetUsageService? usage = null)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed;
      _status = status ?? Namager.App.Services.NullStatusService.Instance;
      _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance; }
```

3b. Invalidate on a successful mutation. In `RunAsync`, replace the success line:

```csharp
            await work();
            await ReloadAsync();
            _status.Success(success);
            return true;
```

with:

```csharp
            await work();
            await ReloadAsync();
            _usage.Invalidate();          // presets changed → amp/IR "used" highlights are now stale
            _status.Success(success);
            return true;
```

(`RefreshAsync` does NOT call `RunAsync`, so a read-only refresh correctly does not invalidate.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter PresetListViewModelTests`
Expected: PASS — new cases plus all pre-existing preset tests unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/PresetListViewModel.cs tests/Namager.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(app): preset mutations invalidate the usage cache"
```

---

### Task 7: Wire it together in MainWindowViewModel

**Files:**
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Namager.App.Tests/MainWindowViewModelTests.cs` (add a case)

**Interfaces:**
- Consumes: `PresetUsageService`, `_connection.Repository!` (a `DeviceRepository`), the amp/IR `RefreshUsageAsync()`.
- Produces: a single shared `PresetUsageService` handed to all three list VMs; `EnsureTabLoaded` re-applies usage on revisit.

- [ ] **Step 1: Write the failing test**

Add to `tests/Namager.App.Tests/MainWindowViewModelTests.cs`:

```csharp
    [Fact]
    public async Task EnsureTabLoaded_reapplies_amp_usage_on_revisit()
    {
        // A usage service whose map changes; revisiting the Amps tab must re-apply it without
        // re-listing amps (mirrors: user edits presets, returns to the Amps tab).
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), Path.Combine(Path.GetTempPath(), "mwvm-usage"), 0, 0);
        var usage = new FakePresetUsageService();
        var amps = new AmpListViewModel(svc, writesAllowed: true, usage: usage);

        var vm = new MainWindowViewModel { Amps = amps };
        vm.EnsureTabLoaded(1);                          // first visit: full refresh
        if (vm.PendingTabLoad is { } t1) await t1;
        Assert.False(amps.Items[0].IsUsed);

        usage.Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }));
        vm.EnsureTabLoaded(1);                          // revisit: re-apply usage
        if (vm.PendingTabLoad is { } t2) await t2;

        Assert.True(amps.Items[0].IsUsed);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter MainWindowViewModelTests`
Expected: FAIL — on revisit the current `EnsureTabLoaded` does nothing, so `IsUsed` stays false.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`:

3a. Re-apply usage on tab revisit. Replace `EnsureTabLoaded`:

```csharp
    public void EnsureTabLoaded(int navIndex)
    {
        if (navIndex == 1 && Amps is { } a && !_ampsLoaded) { _ampsLoaded = true; PendingTabLoad = TimedRefreshAsync(a.RefreshCommand, "amps-first-visit"); }
        else if (navIndex == 2 && Irs is { } i && !_irsLoaded) { _irsLoaded = true; PendingTabLoad = TimedRefreshAsync(i.RefreshCommand, "irs-first-visit"); }
    }
```

with:

```csharp
    public void EnsureTabLoaded(int navIndex)
    {
        if (navIndex == 1 && Amps is { } a)
        {
            if (!_ampsLoaded) { _ampsLoaded = true; PendingTabLoad = TimedRefreshAsync(a.RefreshCommand, "amps-first-visit"); }
            else PendingTabLoad = a.RefreshUsageAsync();   // revisit: refresh "used" highlights only
        }
        else if (navIndex == 2 && Irs is { } i)
        {
            if (!_irsLoaded) { _irsLoaded = true; PendingTabLoad = TimedRefreshAsync(i.RefreshCommand, "irs-first-visit"); }
            else PendingTabLoad = i.RefreshUsageAsync();
        }
    }
```

3b. Construct one shared `PresetUsageService` and hand it to all three VMs. In the `_connection.Connected` handler, replace this block:

```csharp
            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed,
                Status);
            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status);
```

with:

```csharp
            var usage = new PresetUsageService(_connection.Repository!, Status);

            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed,
                Status,
                usage);
            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status);
```

Then replace the amp VM construction:

```csharp
            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed, Status);
```

with:

```csharp
            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed, Status, usage: usage);
```

And the IR VM construction:

```csharp
            var irs = new IrListViewModel(irService, _connection.WritesAllowed, Status);
```

with:

```csharp
            var irs = new IrListViewModel(irService, _connection.WritesAllowed, Status, usage: usage);
```

(`PresetUsageService` and `NullPresetUsageService` are in `Namager.App.Services`, already imported via `using Namager.App.Services;` at the top of the file.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter MainWindowViewModelTests`
Expected: PASS — the new revisit case, and the existing `EnsureTabLoaded_refreshes_amps_once_on_first_visit_only` (its AmpVm uses the default `NullPresetUsageService`, so `RefreshUsageAsync` on revisit issues no `read root\amp` and the read-count assertion still holds).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/MainWindowViewModelTests.cs
git commit -m "feat(app): share a PresetUsageService; refresh highlights on tab revisit"
```

---

### Task 8: Views — highlight used rows + tooltip + amp error line

**Files:**
- Modify: `src/Namager.App/Views/IrListView.axaml`
- Modify: `src/Namager.App/Views/AmpListView.axaml`

**Interfaces:**
- Consumes: `IrItemViewModel`/`AmpItemViewModel` `IsUsed`, `UsedInTooltip`, and the list VMs' `ErrorMessage`.
- Produces: no code — visual only. Used rows show the name in the accent color (SemiBold) and a hover tooltip; the amp view gains the same wrapped `ErrorMessage` line the IR view already has (so the block message is visible).

No unit test (XAML). Verification is a successful build plus the manual visual check below.

- [ ] **Step 1: IR view — add the `used` style, tooltip, and class binding**

In `src/Namager.App/Views/IrListView.axaml`:

1a. Add a styles block. Immediately after the opening `<UserControl ...>` tag's `>` (before `<DockPanel ...>` on line 8), insert:

```xml
  <UserControl.Styles>
    <!-- A preset references this file → highlight it (theme accent, both variants). -->
    <Style Selector="TextBlock.used">
      <Setter Property="Foreground" Value="{DynamicResource Sonulab.AccentBrush}"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
  </UserControl.Styles>
```

1b. Add the tooltip to the item root. Change the item template's root `<DockPanel>` (line 76) to:

```xml
          <DockPanel ToolTip.Tip="{Binding UsedInTooltip}">
```

1c. Highlight the name. Change the name `TextBlock` (lines 85-88) to add the `used` class binding:

```xml
              <TextBlock VerticalAlignment="Center" IsVisible="{Binding !IsEditing}"
                         Classes.used="{Binding IsUsed}"
                         Opacity="{Binding IsEmpty, Converter={x:Static conv:BoolToOpacity.Instance}}"
                         FontStyle="{Binding IsEmpty, Converter={x:Static conv:BoolToItalic.Instance}}"
                         Text="{Binding Name}"/>
```

- [ ] **Step 2: Amp view — add the error line, `used` style, tooltip, and class binding**

In `src/Namager.App/Views/AmpListView.axaml`:

2a. Add the same styles block. Immediately after the opening `<UserControl ...>` tag's `>` (before `<Grid ...>` on line 9), insert:

```xml
  <UserControl.Styles>
    <Style Selector="TextBlock.used">
      <Setter Property="Foreground" Value="{DynamicResource Sonulab.AccentBrush}"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
  </UserControl.Styles>
```

2b. Add the wrapped `ErrorMessage` line (the amp view has none today, so the block message would be invisible otherwise). Inside the left `<DockPanel Grid.Column="0">`, immediately after the command-bar `</StackPanel>` (line 26) and before the `<ListBox ...>` (line 28), insert:

```xml
      <TextBlock DockPanel.Dock="Top" Margin="0,0,0,4" FontSize="11"
                 Foreground="{DynamicResource Sonulab.DangerBrush}"
                 Text="{Binding ErrorMessage}" TextWrapping="Wrap"
                 IsVisible="{Binding ErrorMessage, Converter={x:Static ObjectConverters.IsNotNull}}"/>
```

(`ObjectConverters` is a built-in Avalonia static, already used by the IR view; no new xmlns needed.)

2c. Add the tooltip to the item root. Change the item template's root `<DockPanel>` (line 35) to:

```xml
            <DockPanel ToolTip.Tip="{Binding UsedInTooltip}">
```

2d. Highlight the name. Change the name `TextBlock` (lines 44-47) to add the `used` class binding:

```xml
                <TextBlock VerticalAlignment="Center" IsVisible="{Binding !IsEditing}"
                           Classes.used="{Binding IsUsed}"
                           Opacity="{Binding IsEmpty, Converter={x:Static conv:BoolToOpacity.Instance}}"
                           FontStyle="{Binding IsEmpty, Converter={x:Static conv:BoolToItalic.Instance}}"
                           Text="{Binding Name}"/>
```

- [ ] **Step 3: Build to verify the XAML compiles**

Run: `dotnet build`
Expected: build succeeds (Avalonia compiles the XAML; a binding/typo error would fail here).

- [ ] **Step 4: Commit**

```bash
git add src/Namager.App/Views/IrListView.axaml src/Namager.App/Views/AmpListView.axaml
git commit -m "feat(ui): highlight preset-used amp/IR rows + tooltip; amp error line"
```

- [ ] **Step 5: Manual visual check (on device or with a connected pedal)**

Document results in `docs/HARDWARE-VALIDATION-preset-usage.md` (create it, following the style of the other `HARDWARE-VALIDATION-*.md` files). Verify:
1. Open the Amps tab: amps referenced by a preset show their name in the amber accent, SemiBold; unused amps look normal. Hovering a used amp shows "Used in: …".
2. Repeat on the IR tab.
3. Try to delete a used amp → no delete; the wrapped message lists the presets. Same for a used IR.
4. Try to rename (F2 / context-menu → Rename) a used amp and IR → blocked with the message; the row stays unchanged.
5. Delete/rename an unused amp and IR → works as before.
6. Edit a preset (e.g. delete one), return to the Amps/IR tab → highlights update to reflect the change.
7. Check both light and dark themes.

---

## Final verification

- [ ] **Full test suite:** `dotnet test` — all pass (was 490; this plan adds ~22 tests).
- [ ] **Build:** `dotnet build` — no warnings introduced by these files.

---

## Self-review notes (author)

- **Spec coverage:** §1 usage map → Task 1; §2 caching service + lazy/cached/invalidate → Tasks 2, 6, 7; §3 highlight + tooltip → Tasks 3, 8; §4 delete & rename guards + message via inline `ErrorMessage` → Tasks 4, 5 (+ amp error line added in Task 8); §5 testing → per-task. Data-flow "revisit rebuilds after preset edit" → Task 7 `RefreshUsageAsync`.
- **Serial-safety:** the usage scan only runs inside `ReloadAsync` (already busy-gated) or `RefreshUsageAsync` (`CanRefresh`-gated), never concurrently with a write burst.
- **Backward compatibility:** every new ctor param is last and optional, defaulting to `NullPresetUsageService.Instance`; existing tests compile and pass unchanged (nothing is "used" under the null service).
- **Type consistency:** `IPresetUsageService.GetAsync()/Invalidate()`, `PresetUsageMap.PresetsUsingAmp/PresetsUsingIr/Empty/Build`, `RefreshUsageAsync()`, and item `UsedInPresets/IsUsed/UsedInTooltip` are named identically across all tasks.
