# Preset-Usage Cache (warm-start) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the preset→amp/IR usage map to disk per device id so a reconnect shows amp/IR usage highlights instantly (zero dreads), while the existing background scan revalidates and corrects.

**Architecture:** A new `PresetUsageCache` (IrIndex-pattern JSON file in `%APPDATA%\Namager\`) stores per-slot `{slot, preset, amp, irs[]}` rows keyed by `root\sys\_id`. `PresetUsageService` gains ctor seams (`deviceId`, `usageCachePath`); its scan pass seeds a working dictionary from the cache (filtered by per-slot name match against the fresh 30-name list), publishes that immediately as a provisional map, then replaces entries slot-by-slot as head reads complete and persists on completion. `IsComplete` semantics are untouched — cached data never satisfies the delete/rename guards. The amp detail pane gains a third `Verifying` usage state so provisional/partial data renders with a badge instead of hiding behind "Checking…".

**Tech Stack:** .NET 10, Avalonia 12 (built-in FluentTheme only), System.Text.Json, xUnit.

## Global Constraints

- `IPresetUsageService` gains NO new members — all cache behavior lives in the concrete `PresetUsageService` ctor seams, so `NullPresetUsageService`, `FakePresetUsageService`, and all consumers compile unchanged.
- Cached data must NEVER set `_isComplete` — guards (`EnsureCompleteAsync`) always wait for a real scan.
- Disk cache follows the IrIndex discipline exactly: `DefaultPath` computed inside `try` in a getter (never a static initializer), `schema` int, every Load failure → empty, every Save failure → silent no-op, optional path parameter as test seam.
- Caching is active only when the device id is non-blank (`!string.IsNullOrWhiteSpace`); the existing one-arg `PresetUsageService(repo)` construction gets zero cache behavior and zero disk I/O.
- No hex literals in .axaml — use `Sonulab.*` theme tokens from `Styles/SonulabTheme.axaml` (both theme variants).
- Tests never touch the developer's real `%APPDATA%` files — always pass temp paths.
- The cache file is `preset-usage-cache.json` (NOT `usage*.json` alone — `usage.json` is the telemetry ping state).
- Commit after each task; run the project's full `dotnet test` before each commit.

## Established protocol facts (context for implementers)

- The scan reads a windowed head (≤32 × 128-byte `dread` chunks, ~57 ms each) of every occupied preset slot to find three name-valued nodes: `root\app\amp\amp`, `root\app\ir\ir`, `root\app\ir\ir2\ir`. Full bank ≈ 15–30 s.
- The firmware exposes NO timestamps/counters/checksums; the only cheap change signal is the 30-name list (`read root\presets`, ~60–90 ms), which does NOT change on an in-place edit. Known accepted staleness: an in-place edit outside NAMager can show a wrong provisional highlight for up to one scan duration.
- Device identity: `ConnectionViewModel` connect handler has `state.Device!.Id` available (same object that provides `.Version` for `FirmwareVersion`).

---

### Task 1: `PresetUsageMap` export/import surface (`SlotUsage`)

**Files:**
- Modify: `src/Sonulab.Core/Services/PresetUsageMap.cs`
- Test: `tests/Sonulab.Core.Tests/PresetUsageMapTests.cs` (extend existing)

**Interfaces:**
- Produces (later tasks rely on these exact shapes):
  - `public sealed record SlotUsage(int Index, string PresetName, string? Amp, IReadOnlyList<string> Irs);` (top-level, next to `PresetRef`, namespace `Sonulab.Core.Services`)
  - `public IReadOnlyList<SlotUsage> ToSlotUsages()` — one row per slot that has ≥1 ref, ordered by slot index; `Irs` sorted ordinal for determinism.
  - `public static PresetUsageMap FromSlotUsages(IEnumerable<SlotUsage> slots)` — inverse; dedupes by slot (first wins), trims names, drops empty names.
  - `public static SlotUsage ExtractSlotUsage(int index, string presetName, PresetDocument doc)` — implemented VIA `Build` so it can never disagree with the scan's parsing.

- [ ] **Step 1: Write the failing tests** (append to `tests/Sonulab.Core.Tests/PresetUsageMapTests.cs`, matching its existing doc-line helpers; if it has none, use these literal node lines):

```csharp
[Fact]
public void SlotUsages_roundtrip_preserves_lookups()
{
    var doc0 = PresetDocument.FromLines(new[]
    {
        @"root\app\amp\amp:{""value"":""Plexi""}",
        @"root\app\ir\ir:{""value"":""Cab A""}",
        @"root\app\ir\ir2\ir:{""value"":""Cab B""}",
    });
    var doc1 = PresetDocument.FromLines(new[]
    {
        @"root\app\amp\amp:{""value"":""Plexi""}",
        @"root\app\ir\ir:{""value"":""Cab A""}",
    });
    var map = PresetUsageMap.Build(new[] { (0, "Lead", doc0), (3, "Rhythm", doc1) });

    var rows = map.ToSlotUsages();
    var back = PresetUsageMap.FromSlotUsages(rows);

    Assert.Equal(map.PresetsUsingAmp("Plexi"), back.PresetsUsingAmp("Plexi"));
    Assert.Equal(map.PresetsUsingIr("Cab A"), back.PresetsUsingIr("Cab A"));
    Assert.Equal(map.PresetsUsingIr("Cab B"), back.PresetsUsingIr("Cab B"));
}

[Fact]
public void ToSlotUsages_is_ordered_and_deterministic()
{
    var doc = PresetDocument.FromLines(new[]
    {
        @"root\app\amp\amp:{""value"":""Plexi""}",
        @"root\app\ir\ir:{""value"":""Zeta""}",
        @"root\app\ir\ir2\ir:{""value"":""Alpha""}",
    });
    var rows = PresetUsageMap.Build(new[] { (5, "Solo", doc), (2, "Clean", doc) }).ToSlotUsages();

    Assert.Equal(new[] { 2, 5 }, rows.Select(r => r.Index));
    Assert.Equal(new[] { "Alpha", "Zeta" }, rows[0].Irs);   // ordinal-sorted
    Assert.Equal("Plexi", rows[0].Amp);
    Assert.Equal("Clean", rows[0].PresetName);
}

[Fact]
public void ExtractSlotUsage_matches_Build_and_handles_refless_docs()
{
    var doc = PresetDocument.FromLines(new[]
    {
        @"root\app\amp\amp:{""value"":""Plexi""}",
        @"root\app\ir\ir:{""value"":""Cab A""}",
    });
    var one = PresetUsageMap.ExtractSlotUsage(4, "Lead", doc);
    Assert.Equal(4, one.Index);
    Assert.Equal("Lead", one.PresetName);
    Assert.Equal("Plexi", one.Amp);
    Assert.Equal(new[] { "Cab A" }, one.Irs);

    var refless = PresetUsageMap.ExtractSlotUsage(7, "Empty-ish",
        PresetDocument.FromLines(new[] { @"root\app\eq\low:{""value"":0}" }));
    Assert.Equal(7, refless.Index);
    Assert.Null(refless.Amp);
    Assert.Empty(refless.Irs);
}

[Fact]
public void FromSlotUsages_dedupes_by_slot_and_drops_blank_names()
{
    var back = PresetUsageMap.FromSlotUsages(new[]
    {
        new SlotUsage(1, "A", "Plexi", new[] { "Cab", "" }),
        new SlotUsage(1, "A-dupe", "Other", Array.Empty<string>()),
        new SlotUsage(2, "B", null, new[] { "Cab" }),
    });
    Assert.Equal(new[] { new PresetRef(1, "A") }, back.PresetsUsingAmp("Plexi"));
    Assert.Empty(back.PresetsUsingAmp("Other"));
    Assert.Equal(new[] { new PresetRef(1, "A"), new PresetRef(2, "B") }, back.PresetsUsingIr("Cab"));
}
```

NOTE: if `PresetDocument` has no `FromLines` factory, check the existing tests in this file for how they construct a `PresetDocument` from lines (there will be a helper — the `Build` tests already do this) and use that instead. Do not add a new factory to `PresetDocument`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~PresetUsageMapTests" 2>&1 | tail -5`
Expected: FAIL — `SlotUsage`/`ToSlotUsages`/`FromSlotUsages`/`ExtractSlotUsage` do not exist (compile error).

- [ ] **Step 3: Implement** — add to `PresetUsageMap.cs`, top-level record next to `PresetRef`:

```csharp
/// <summary>One slot's usage row: the preset in <see cref="Index"/> references <see cref="Amp"/>
/// (null = no amp ref) and each IR in <see cref="Irs"/>. The serialization shape of
/// <see cref="PresetUsageMap"/> — irs is a NAME LIST, not positional (the map does not preserve
/// which IR node a name came from).</summary>
public sealed record SlotUsage(int Index, string PresetName, string? Amp, IReadOnlyList<string> Irs);
```

and inside the class:

```csharp
/// <summary>Invert the map into per-slot rows (the disk-cache shape). One row per slot that has
/// at least one ref, slot-ascending; Irs ordinal-sorted so output is deterministic.</summary>
public IReadOnlyList<SlotUsage> ToSlotUsages()
{
    var names = new Dictionary<int, string>();
    var amps = new Dictionary<int, string>();
    var irs = new Dictionary<int, List<string>>();
    foreach (var (ampName, refs) in _amp)
        foreach (var r in refs) { names[r.Index] = r.Name; amps[r.Index] = ampName; }
    foreach (var (irName, refs) in _ir)
        foreach (var r in refs)
        {
            names[r.Index] = r.Name;
            if (!irs.TryGetValue(r.Index, out var list)) irs[r.Index] = list = new List<string>();
            list.Add(irName);
        }
    return names.Keys.OrderBy(i => i).Select(i => new SlotUsage(
        i, names[i],
        amps.TryGetValue(i, out var a) ? a : null,
        irs.TryGetValue(i, out var l)
            ? l.OrderBy(s => s, StringComparer.Ordinal).ToList()
            : (IReadOnlyList<string>)Array.Empty<string>())).ToList();
}

/// <summary>Rebuild a map from per-slot rows (the inverse of <see cref="ToSlotUsages"/>).
/// Dedupes by slot (first row wins), trims names, ignores blank names — mirrors what
/// <see cref="Collect"/> tolerates so a cache written by a future/buggy writer degrades
/// to fewer highlights, never to a throw.</summary>
public static PresetUsageMap FromSlotUsages(IEnumerable<SlotUsage> slots)
{
    var amp = new Dictionary<string, List<PresetRef>>();
    var ir = new Dictionary<string, List<PresetRef>>();
    var seen = new HashSet<int>();
    foreach (var s in slots)
    {
        if (!seen.Add(s.Index)) continue;
        var entry = new PresetRef(s.Index, s.PresetName);
        var a = s.Amp?.Trim();
        if (!string.IsNullOrEmpty(a)) AddRef(amp, a, entry);
        foreach (var irName in s.Irs ?? Array.Empty<string>())
        {
            var n = irName?.Trim();
            if (!string.IsNullOrEmpty(n)) AddRef(ir, n!, entry);
        }
    }
    return new PresetUsageMap(Freeze(amp), Freeze(ir));

    static void AddRef(Dictionary<string, List<PresetRef>> map, string key, PresetRef r)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<PresetRef>();
        if (!list.Any(x => x.Index == r.Index)) list.Add(r);
    }
}

/// <summary>One slot's row parsed from a document. Implemented via <see cref="Build"/> so this
/// can never disagree with the scan's own parsing. A doc with no refs yields an empty row
/// (null amp, no irs) rather than nothing — callers use it to overwrite a provisional row.</summary>
public static SlotUsage ExtractSlotUsage(int index, string presetName, PresetDocument doc)
{
    var rows = Build(new[] { (index, presetName, doc) }).ToSlotUsages();
    return rows.Count > 0 ? rows[0]
         : new SlotUsage(index, presetName, null, Array.Empty<string>());
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~PresetUsageMapTests" 2>&1 | tail -3`
Expected: PASS (all, including pre-existing).

- [ ] **Step 5: Run the FULL suite, then commit**

Run: `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`
Expected: all projects pass.

```bash
git add src/Sonulab.Core/Services/PresetUsageMap.cs tests/Sonulab.Core.Tests/PresetUsageMapTests.cs
git commit -m "feat(usage-cache): SlotUsage export/import surface on PresetUsageMap"
```

---

### Task 2: `PresetUsageCache` disk store

**Files:**
- Create: `src/Namager.App/Services/PresetUsageCache.cs`
- Test: `tests/Namager.App.Tests/PresetUsageCacheTests.cs` (new; mirror `IrIndexTests.cs` patterns)

**Interfaces:**
- Consumes: `SlotUsage` from Task 1.
- Produces:
  - `public const int Schema = 1; public const int MaxDevices = 8;`
  - `public static string DefaultPath { get; }` → `%APPDATA%\Namager\preset-usage-cache.json`
  - `public static PresetUsageCache Load(string? path = null)`
  - `public IReadOnlyList<SlotUsage> SlotsFor(string deviceId)` — empty list if unknown id.
  - `public PresetUsageCache WithDevice(string deviceId, IReadOnlyList<SlotUsage> slots)` — immutable; stamps `savedUtc`; prunes oldest beyond `MaxDevices`.
  - `public void Save(string? path = null)`

**File format:**

```json
{
  "schema": 1,
  "devices": [
    { "id": "<root\\sys\\_id>", "savedUtc": "2026-08-03T12:00:00Z",
      "slots": [ { "slot": 0, "preset": "Lead", "amp": "Plexi", "irs": ["Cab A"] } ] }
  ]
}
```

- [ ] **Step 1: Write the failing tests** (`tests/Namager.App.Tests/PresetUsageCacheTests.cs`):

```csharp
using Namager.App.Services;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageCacheTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"nmgr-usage-cache-{Guid.NewGuid():N}.json");

    private static readonly SlotUsage[] SampleSlots =
    {
        new(0, "Lead", "Plexi", new[] { "Cab A" }),
        new(3, "Rhythm", null, new[] { "Cab A", "Cab B" }),
    };

    [Fact]
    public void Roundtrips_slots_per_device()
    {
        var path = TempPath();
        try
        {
            PresetUsageCache.Load(path).WithDevice("dev-1", SampleSlots).Save(path);
            var loaded = PresetUsageCache.Load(path);
            Assert.Equal(SampleSlots.Select(s => (s.Index, s.PresetName, s.Amp)),
                         loaded.SlotsFor("dev-1").Select(s => (s.Index, s.PresetName, s.Amp)));
            Assert.Equal(new[] { "Cab A", "Cab B" }, loaded.SlotsFor("dev-1")[1].Irs);
            Assert.Empty(loaded.SlotsFor("dev-2"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_corrupt_and_future_schema_files_load_as_empty()
    {
        Assert.Empty(PresetUsageCache.Load(TempPath()).SlotsFor("dev-1"));

        var corrupt = TempPath();
        var future = TempPath();
        try
        {
            File.WriteAllText(corrupt, "{ not json");
            Assert.Empty(PresetUsageCache.Load(corrupt).SlotsFor("dev-1"));

            File.WriteAllText(future, """{ "schema": 99, "devices": [] }""");
            Assert.Empty(PresetUsageCache.Load(future).SlotsFor("dev-1"));
        }
        finally { File.Delete(corrupt); File.Delete(future); }
    }

    [Fact]
    public void Save_to_unwritable_path_does_not_throw()
    {
        var cache = PresetUsageCache.Load(TempPath()).WithDevice("dev-1", SampleSlots);
        cache.Save(Path.Combine(Path.GetTempPath(), "nmgr-no-such-dir-\0-x", "cache.json"));
    }

    [Fact]
    public void WithDevice_replaces_same_id_and_preserves_others()
    {
        var cache = PresetUsageCache.Load(TempPath())
            .WithDevice("dev-1", SampleSlots)
            .WithDevice("dev-2", new[] { new SlotUsage(9, "Other", "JCM", Array.Empty<string>()) })
            .WithDevice("dev-1", new[] { new SlotUsage(1, "New", "AC30", Array.Empty<string>()) });
        Assert.Equal("New", Assert.Single(cache.SlotsFor("dev-1")).PresetName);
        Assert.Equal("Other", Assert.Single(cache.SlotsFor("dev-2")).PresetName);
    }

    [Fact]
    public void Prunes_oldest_devices_beyond_MaxDevices()
    {
        var cache = PresetUsageCache.Load(TempPath());
        for (int i = 0; i < PresetUsageCache.MaxDevices + 2; i++)
            cache = cache.WithDevice($"dev-{i}", SampleSlots);
        Assert.Empty(cache.SlotsFor("dev-0"));                      // oldest pruned
        Assert.NotEmpty(cache.SlotsFor($"dev-{PresetUsageCache.MaxDevices + 1}"));
    }

    [Fact]
    public void Load_drops_malformed_slot_entries()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
            { "schema": 1, "devices": [ { "id": "dev-1", "savedUtc": "2026-08-03T00:00:00Z",
              "slots": [
                { "slot": 0, "preset": "Good", "amp": "Plexi", "irs": [] },
                { "slot": 30, "preset": "OutOfRange", "amp": "X", "irs": [] },
                { "slot": -1, "preset": "Negative", "amp": "X", "irs": [] },
                { "slot": 2, "preset": "", "amp": "X", "irs": [] },
                { "slot": 3, "preset": "NullIrs", "amp": null, "irs": null } ] } ] }
            """);
            var rows = PresetUsageCache.Load(path).SlotsFor("dev-1");
            Assert.Equal(new[] { "Good", "NullIrs" }, rows.Select(r => r.PresetName));
            Assert.Empty(rows[1].Irs);                              // null irs → empty, not null
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~PresetUsageCacheTests" 2>&1 | tail -5`
Expected: FAIL (compile error — `PresetUsageCache` does not exist).

- [ ] **Step 3: Implement** `src/Namager.App/Services/PresetUsageCache.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Sonulab.Core.Services;

namespace Namager.App.Services;

/// <summary>Per-device disk cache of the preset→amp/IR usage map, so a reconnect can show
/// highlights instantly (warm start) while the background scan revalidates.
///
/// Keyed by root\sys\_id because the map belongs to a pedal, not to this PC. Entries are
/// per-slot rows; on load the scanner keeps only rows whose slot still holds a preset of the
/// same name, so renames/deletes/reorders done outside the app drop out cheaply. An IN-PLACE
/// edit outside the app is undetectable until the scan reaches that slot — a provisional
/// highlight may be stale for up to one scan (~15-30 s). Guards never trust cached data
/// (PresetUsageService.IsComplete stays false until a real scan finishes).
///
/// Names stay local: this file is never transmitted. See PRIVACY.md.
///
/// Every failure mode (missing, corrupt, unknown schema, unwritable) degrades to "empty" /
/// no-op rather than throwing — losing the cache costs a warm start, never data.</summary>
public sealed class PresetUsageCache
{
    public const int Schema = 1;

    /// <summary>Devices kept, newest savedUtc first. 8 comfortably covers a multi-pedal bench
    /// without letting the file grow unbounded.</summary>
    public const int MaxDevices = 8;

    private readonly List<DeviceEntry> _devices;   // invariant: unique ids

    private PresetUsageCache(List<DeviceEntry> devices) => _devices = devices;

    /// <summary>%APPDATA%\Namager\preset-usage-cache.json — same directory as settings.json /
    /// ir-index.json. Guarded like IrIndex.DefaultPath: a throwing folder lookup must not
    /// poison the type initializer.</summary>
    public static string DefaultPath
    {
        get
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Namager", "preset-usage-cache.json");
            }
            catch { return "preset-usage-cache.json"; }
        }
    }

    public IReadOnlyList<SlotUsage> SlotsFor(string deviceId) =>
        _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            ?.ToSlotUsages() ?? Array.Empty<SlotUsage>();

    /// <summary>Returns a new cache with <paramref name="deviceId"/>'s rows replaced and its
    /// savedUtc stamped now; devices beyond <see cref="MaxDevices"/> are pruned oldest-first.
    /// Does not write to disk — call Save.</summary>
    public PresetUsageCache WithDevice(string deviceId, IReadOnlyList<SlotUsage> slots)
    {
        var next = _devices.Where(d => !string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            .Append(DeviceEntry.From(deviceId, DateTime.UtcNow, slots))
            .OrderByDescending(d => d.SavedUtc)
            .Take(MaxDevices)
            .ToList();
        return new PresetUsageCache(next);
    }

    public static PresetUsageCache Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new PresetUsageCache([]);

            var doc = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file));
            // A file from a future writer is not safely readable — empty beats guessing.
            if (doc is null || doc.Schema != Schema || doc.Devices is null)
                return new PresetUsageCache([]);

            var devices = doc.Devices
                .Where(d => d is not null && !string.IsNullOrEmpty(d.Id))
                .GroupBy(d => d!.Id, StringComparer.Ordinal)
                .Select(g => g.First()!.Sanitized())
                .ToList();
            return new PresetUsageCache(devices);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return new PresetUsageCache([]);
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(file, JsonSerializer.Serialize(
                new CacheFile(Schema, [.. _devices]),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // Losing the cache costs a warm start, never data.
        }
    }

    private sealed record CacheFile(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("devices")] DeviceEntry[]? Devices);

    private sealed record DeviceEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("savedUtc")] DateTime SavedUtc,
        [property: JsonPropertyName("slots")] SlotDto[]? Slots)
    {
        public static DeviceEntry From(string id, DateTime savedUtc, IReadOnlyList<SlotUsage> slots) =>
            new(id, savedUtc,
                slots.Select(s => new SlotDto(s.Index, s.PresetName, s.Amp, s.Irs.ToArray())).ToArray());

        /// <summary>Drop rows a well-behaved writer would never produce (slot out of range,
        /// blank preset name); normalize null irs to empty.</summary>
        public DeviceEntry Sanitized() =>
            this with
            {
                Slots = (Slots ?? []).Where(s =>
                    s is not null && s.Slot is >= 0 and < 30 && !string.IsNullOrEmpty(s.Preset))
                    .ToArray(),
            };

        public IReadOnlyList<SlotUsage> ToSlotUsages() =>
            (Slots ?? []).Select(s => new SlotUsage(
                s.Slot, s.Preset, s.Amp,
                s.Irs is null ? Array.Empty<string>() : s.Irs)).ToList();
    }

    private sealed record SlotDto(
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("preset")] string Preset,
        [property: JsonPropertyName("amp")] string? Amp,
        [property: JsonPropertyName("irs")] string[]? Irs);
}
```

NOTE (prune test): `WithDevice` stamps `DateTime.UtcNow`, so consecutive calls in a tight loop may collide on the timestamp. `OrderByDescending(SavedUtc).Take(MaxDevices)` with equal stamps makes the prune order unstable. Make the sort stable by appending the new device LAST and using `.OrderByDescending(d => d.SavedUtc)` only — LINQ's OrderBy is stable, so equal stamps preserve insertion order (oldest-inserted sorts last and is pruned first). If the prune test still flakes, sort by `(SavedUtc, insertion order)` explicitly rather than adding sleeps to the test.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~PresetUsageCacheTests" 2>&1 | tail -3`
Expected: PASS.

- [ ] **Step 5: Full suite, commit**

Run: `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`

```bash
git add src/Namager.App/Services/PresetUsageCache.cs tests/Namager.App.Tests/PresetUsageCacheTests.cs
git commit -m "feat(usage-cache): per-device disk cache for the preset-usage map"
```

---

### Task 3: `PresetUsageService` warm start + persist

**Files:**
- Modify: `src/Namager.App/Services/PresetUsageService.cs`
- Test: `tests/Namager.App.Tests/PresetUsageServiceTests.cs` (extend)

**Interfaces:**
- Consumes: `PresetUsageCache` (Task 2), `SlotUsage` / `ToSlotUsages` / `FromSlotUsages` / `ExtractSlotUsage` (Task 1).
- Produces: `public PresetUsageService(DeviceRepository repo, string? deviceId = null, string? usageCachePath = null)` — Task 4 wires this. `IPresetUsageService` is UNCHANGED.

**Design notes for the implementer:**
- `RunScanPassAsync` currently rebuilds `_current` from a per-pass `resolved` list (`PresetUsageMap.Build(resolved)`); rework it around a `Dictionary<int, SlotUsage> working` that SURVIVES version-restarts within the pass loop. Side benefit: fixes the current minor highlight-flash where the first progressive publish of a restarted pass shrinks the map to one slot.
- Keep the version/restart logic structure otherwise byte-for-byte — it is covered by `Invalidate_during_a_scan_restarts_it` and the torn-read tests.
- Completion still requires every occupied slot to have been freshly read in the final stable-version pass — the loop below overwrites every occupied slot's row before `_isComplete = true`, so no provisional entry can survive into a complete map. State this in a comment.

- [ ] **Step 1: Write the failing tests** (append to `PresetUsageServiceTests.cs`):

```csharp
// ---- warm start from the disk cache ----

private const string IrNode = @"root\app\ir\ir:{{""value"":""{0}""}}";
private static string Ir(string name) => string.Format(IrNode, name);

/// <summary>Make() variant with cache seams. Seeds the same two presets as Make().</summary>
private static (PresetUsageService svc, FakePresetDevice dev, CountingLink link) MakeCached(
    string? deviceId, string cachePath)
{
    var dev = new FakePresetDevice();
    dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
    dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
    dev.OpenAsync().GetAwaiter().GetResult();
    var link = new CountingLink(dev);
    var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
    return (new PresetUsageService(repo, deviceId, cachePath), dev, link);
}

private static string TempCachePath() =>
    Path.Combine(Path.GetTempPath(), $"nmgr-usage-svc-{Guid.NewGuid():N}.json");

private static void SeedCache(string path, string deviceId, params SlotUsage[] slots) =>
    PresetUsageCache.Load(path).WithDevice(deviceId, slots).Save(path);

[Fact]
public async Task Warm_start_publishes_cached_map_at_zero_dreads()
{
    var path = TempCachePath();
    try
    {
        SeedCache(path, "dev-1",
            new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()),
            new SlotUsage(1, "Rhythm", "Plexi", Array.Empty<string>()));
        var (svc, _, link) = MakeCached("dev-1", path);

        var snapshots = new List<(int Dreads, PresetUsageMap Map, bool Complete)>();
        svc.MapUpdated += () => { lock (snapshots) snapshots.Add((link.Dreads, svc.Current, svc.IsComplete)); }; 
        await svc.EnsureCompleteAsync();

        (int Dreads, PresetUsageMap Map, bool Complete) first;
        lock (snapshots) first = snapshots[0];
        Assert.Equal(0, first.Dreads);                       // provisional publish cost no dreads
        Assert.Equal(2, first.Map.PresetsUsingAmp("Plexi").Count);
        Assert.False(first.Complete);                        // cached data never completes
        Assert.True(svc.IsComplete);                         // ...but the real scan does
    }
    finally { File.Delete(path); }
}

[Fact]
public async Task Cached_slot_with_mismatched_name_is_dropped_from_the_provisional_map()
{
    var path = TempCachePath();
    try
    {
        SeedCache(path, "dev-1",
            new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()),
            new SlotUsage(1, "RENAMED-OUTSIDE", "Ghost", Array.Empty<string>()));
        var (svc, _, link) = MakeCached("dev-1", path);

        var firstMaps = new List<(int Dreads, PresetUsageMap Map)>();
        svc.MapUpdated += () => { lock (firstMaps) firstMaps.Add((link.Dreads, svc.Current)); };
        await svc.EnsureCompleteAsync();

        (int Dreads, PresetUsageMap Map) first;
        lock (firstMaps) first = firstMaps[0];
        Assert.Equal(0, first.Dreads);
        Assert.Empty(first.Map.PresetsUsingAmp("Ghost"));    // stale row dropped by name mismatch
        Assert.Single(first.Map.PresetsUsingAmp("Plexi"));   // matching row kept
    }
    finally { File.Delete(path); }
}

[Fact]
public async Task Cache_for_a_different_device_is_ignored()
{
    var path = TempCachePath();
    try
    {
        SeedCache(path, "other-pedal", new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()));
        var (svc, _, link) = MakeCached("dev-1", path);

        var snapshots = new List<int>();
        svc.MapUpdated += () => { lock (snapshots) snapshots.Add(link.Dreads); };
        await svc.EnsureCompleteAsync();

        int firstDreads; lock (snapshots) firstDreads = snapshots[0];
        Assert.True(firstDreads > 0, "no zero-dread provisional publish for a foreign device");
    }
    finally { File.Delete(path); }
}

[Fact]
public async Task Scan_corrects_a_lying_cache_and_persists_the_truth()
{
    var path = TempCachePath();
    try
    {
        SeedCache(path, "dev-1", new SlotUsage(0, "Lead", "WrongAmp", Array.Empty<string>()));
        var (svc, _, _) = MakeCached("dev-1", path);

        var map = await svc.EnsureCompleteAsync();
        Assert.Empty(map.PresetsUsingAmp("WrongAmp"));
        Assert.Equal(2, map.PresetsUsingAmp("Plexi").Count);

        var persisted = PresetUsageCache.Load(path).SlotsFor("dev-1");
        Assert.DoesNotContain(persisted, s => s.Amp == "WrongAmp");
        Assert.Equal(2, persisted.Count(s => s.Amp == "Plexi"));   // completion persisted truth
    }
    finally { File.Delete(path); }
}

[Fact]
public async Task Provisional_entries_survive_progressive_publishes()
{
    var path = TempCachePath();
    try
    {
        // Cache knows slot 1; the scan reads slot 0 first. The slot-0 publish must not
        // flash slot 1's provisional highlight off.
        SeedCache(path, "dev-1", new SlotUsage(1, "Rhythm", "Plexi", Array.Empty<string>()));
        var (svc, _, _) = MakeCached("dev-1", path);

        var everyMapHadSlot1 = true;
        svc.MapUpdated += () =>
        {
            if (!svc.Current.PresetsUsingAmp("Plexi").Any(r => r.Index == 1))
                everyMapHadSlot1 = false;
        };
        await svc.EnsureCompleteAsync();
        Assert.True(everyMapHadSlot1, "a progressive publish dropped the provisional slot-1 entry");
    }
    finally { File.Delete(path); }
}

[Fact]
public async Task No_deviceId_means_no_cache_reads_or_writes()
{
    var path = TempCachePath();
    try
    {
        var (svc, _, _) = MakeCached(deviceId: null, path);
        await svc.EnsureCompleteAsync();
        Assert.False(File.Exists(path), "cacheless service must not write the cache file");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

[Fact]
public async Task Targeted_notify_on_a_complete_map_updates_the_persisted_cache()
{
    var path = TempCachePath();
    try
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo, "dev-1", path);
        await svc.EnsureCompleteAsync();

        svc.NotifyPresetRenamed(0, "Lead V2");
        var persisted = PresetUsageCache.Load(path).SlotsFor("dev-1");
        Assert.Equal("Lead V2", Assert.Single(persisted).PresetName);
    }
    finally { File.Delete(path); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~PresetUsageServiceTests" 2>&1 | tail -5`
Expected: FAIL (compile error — no 3-arg ctor).

- [ ] **Step 3: Implement.** In `PresetUsageService`:

Replace the ctor and add fields:

```csharp
    private readonly string? _deviceId;
    private readonly string? _cachePath;      // null = PresetUsageCache.DefaultPath
    private readonly object _saveLock = new();
    private bool _cacheSeedConsumed;          // the disk cache is read at most once per service

    /// <summary>Cache seams: <paramref name="deviceId"/> (root\sys\_id) keys the per-device disk
    /// cache; blank/null disables ALL cache behavior (reads and writes) — existing callers and
    /// tests construct with repo only and see the old behavior exactly. <paramref name="usageCachePath"/>
    /// overrides the cache file location (tests; null = the real %APPDATA% file).</summary>
    public PresetUsageService(DeviceRepository repo, string? deviceId = null, string? usageCachePath = null)
    {
        _repo = repo;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _cachePath = usageCachePath;
    }
```

Replace `RunScanPassAsync` with the working-dictionary version:

```csharp
    /// <summary>One scan pass: list the occupied slots, then head-read each one, restarting from
    /// the top whenever Invalidate() bumps the version mid-pass. Exceptions propagate to the
    /// caller's retry loop; this method makes no attempt to swallow them.
    ///
    /// The pass accumulates per-slot rows in <c>working</c>. On the first list read the disk
    /// cache seeds provisional rows (slot+name must match the live list) and publishes them
    /// immediately — the warm start: highlights at zero dreads. Fresh head reads then OVERWRITE
    /// rows slot-by-slot; a version-restart re-filters existing rows against the fresh list
    /// instead of starting empty (no highlight flash). _isComplete is only set after every
    /// occupied slot was freshly read in a stable-version pass, so no provisional row can
    /// survive into a complete map — cached data never satisfies the guards.</summary>
    private async Task RunScanPassAsync(CancellationToken ct)
    {
        Dictionary<int, SlotUsage>? working = null;
        while (true)
        {
            int version; lock (_sync) version = _version;
            var slots = _urgent
                ? await _repo.ListPresetsAsync(ct)
                : await _repo.ListPresetsBackgroundAsync(ct);

            if (working is null)
            {
                working = new Dictionary<int, SlotUsage>();
                foreach (var row in LoadCacheRows(slots)) working[row.Index] = row;
                if (working.Count > 0)
                {
                    _current = PresetUsageMap.FromSlotUsages(working.Values);
                    MapUpdated?.Invoke();          // the warm start — zero dreads so far
                }
            }
            else
            {
                // Restart: keep rows still consistent with the fresh list, drop the rest.
                working = working.Values
                    .Where(r => Matches(slots, r))
                    .ToDictionary(r => r.Index);
            }

            bool restart = false;
            foreach (var s in slots)
            {
                if (s.IsEmpty) continue;
                ct.ThrowIfCancellationRequested();
                lock (_sync) { if (_version != version) { restart = true; } }
                if (restart) break;
                var doc = await _repo.ReadPresetHeadAsync(s.Index, background: !_urgent, ct);
                working[s.Index] = PresetUsageMap.ExtractSlotUsage(s.Index, s.Name, doc);
                _current = PresetUsageMap.FromSlotUsages(working.Values);
                MapUpdated?.Invoke();
            }
            if (restart) continue;                       // stale version: rescan from the top
            lock (_sync) { if (_version == version) _isComplete = true; else continue; }
            PersistCache();
            MapUpdated?.Invoke();
            return;
        }
    }

    /// <summary>The disk-cache rows valid for the CURRENT slot list: same device, slot still
    /// occupied by a preset of the same name. Read at most once per service instance — a retry
    /// or restart must not resurrect rows the scan already corrected.</summary>
    private IReadOnlyList<SlotUsage> LoadCacheRows(IReadOnlyList<PresetSlot> slots)
    {
        if (_deviceId is null || _cacheSeedConsumed) return Array.Empty<SlotUsage>();
        _cacheSeedConsumed = true;
        return PresetUsageCache.Load(_cachePath).SlotsFor(_deviceId)
            .Where(r => Matches(slots, r))
            .ToList();
    }

    private static bool Matches(IReadOnlyList<PresetSlot> slots, SlotUsage row) =>
        row.Index >= 0 && row.Index < slots.Count &&
        !slots[row.Index].IsEmpty &&
        string.Equals(slots[row.Index].Name, row.PresetName, StringComparison.Ordinal);

    /// <summary>Write Current's rows under this device id. Read-modify-write so other devices'
    /// entries survive; the lock serializes the scan-completion thread against UI-thread Apply
    /// saves. No-op when caching is disabled.</summary>
    private void PersistCache()
    {
        if (_deviceId is null) return;
        lock (_saveLock)
            PresetUsageCache.Load(_cachePath)
                .WithDevice(_deviceId, _current.ToSlotUsages())
                .Save(_cachePath);
    }
```

NOTE: `PresetSlot` above is whatever element type `_repo.ListPresetsAsync` returns (it exposes `.Index`, `.Name`, `.IsEmpty` — check `DeviceRepository` for the real type name and use that; also check whether it returns `IReadOnlyList` or array and adjust `Matches`'s parameter type accordingly).

Update `Apply` to persist targeted maintenance of a COMPLETE map (persisting a partial map would overwrite a previously complete cache with less data):

```csharp
    private void Apply(Func<PresetUsageMap, PresetUsageMap> transform)
    {
        _current = transform(_current);
        if (_isComplete) PersistCache();
        MapUpdated?.Invoke();
    }
```

`Invalidate()` stays disk-untouched (stale cache beats none; the next completed scan overwrites).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~PresetUsageServiceTests" 2>&1 | tail -3`
Expected: PASS — all new tests AND all pre-existing scan tests (`Invalidate_during_a_scan_restarts_it`, torn-read, progressive, cached-until-invalidated).

- [ ] **Step 5: Run the e2e warm-start test** — extend `UsageScanEndToEndTests.cs`:

```csharp
[Fact]
public async Task Warm_start_over_a_real_document_costs_zero_dreads_then_verifies_within_budget()
{
    var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
    var cachePath = Path.Combine(Path.GetTempPath(), $"nmgr-e2e-{Guid.NewGuid():N}.json");
    try
    {
        // First connection: scan to completion, which persists the cache.
        var dev1 = new FakePresetDevice();
        dev1.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
        await dev1.OpenAsync();
        var svc1 = new PresetUsageService(
            new DeviceRepository(new SonuClient(dev1, backgroundQuietMs: 0)), "dev-1", cachePath);
        await svc1.EnsureCompleteAsync();

        // Simulated reconnect: fresh device, fresh counting link, same cache.
        var dev2 = new FakePresetDevice();
        dev2.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
        await dev2.OpenAsync();
        var counter = new CountingLink(dev2);
        var svc2 = new PresetUsageService(
            new DeviceRepository(new SonuClient(counter, backgroundQuietMs: 0)), "dev-1", cachePath);

        (int Dreads, bool HasAmp)? first = null;
        svc2.MapUpdated += () => first ??= (counter.Dreads,
            svc2.Current.PresetsUsingAmp("Quad Reverb Randall Head SM57").Count == 1);
        await svc2.EnsureCompleteAsync();

        Assert.Equal(0, first!.Value.Dreads);
        Assert.True(first.Value.HasAmp);
        Assert.InRange(counter.Dreads, 1, DeviceRepository.HeadChunkCap);
    }
    finally { File.Delete(cachePath); }
}
```

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~UsageScanEndToEndTests" 2>&1 | tail -3`
Expected: PASS.

- [ ] **Step 6: Full suite, commit**

Run: `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`

```bash
git add src/Namager.App/Services/PresetUsageService.cs tests/Namager.App.Tests/PresetUsageServiceTests.cs tests/Namager.App.Tests/UsageScanEndToEndTests.cs
git commit -m "feat(usage-cache): warm-start PresetUsageService from the disk cache, persist on completion"
```

---

### Task 4: Wire-up — device id from the connection into the service

**Files:**
- Modify: `src/Namager.App/ViewModels/ConnectionViewModel.cs` (~line 47: next to `FirmwareVersion`; ~line 79: connect handler)
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs` (~line 194: path fields + ctors; ~line 246: service construction)
- Test: `tests/Namager.App.Tests/` — compile-only check; no test raises `Connected` with a real session today, so behavior is covered by Task 3's service tests. Verify no existing MainWindowViewModel test breaks.

**Interfaces:**
- Consumes: `PresetUsageService(repo, deviceId, usageCachePath)` from Task 3.
- Produces: `ConnectionViewModel.DeviceId` (`public string? DeviceId { get; private set; }`), `MainWindowViewModel(string? settingsPath, string? irIndexPath, string? usageCachePath)` ctor overload.

- [ ] **Step 1: `ConnectionViewModel`** — next to `FirmwareVersion` (line ~47) add:

```csharp
    /// <summary>The connected pedal's id (root\sys\_id), set alongside FirmwareVersion on a
    /// successful connect. Null until then; may be empty on firmware that reports no id —
    /// consumers must treat blank as "no id" (PresetUsageService disables caching for it).</summary>
    public string? DeviceId { get; private set; }
```

and in `ConnectAsync` directly under `FirmwareVersion = state.Device!.Version;`:

```csharp
            DeviceId = state.Device!.Id;
```

(Verify the property name on the device info record — the same `state.Device` object `CompatibilityChecker` builds from `root\sys\_id`; if the property is named differently, e.g. `DeviceId`, adjust.)

- [ ] **Step 2: `MainWindowViewModel`** — next to `_irIndexPath` (~line 194):

```csharp
    /// <summary>Where the preset-usage warm-start cache is read from and written to. Null = the
    /// real %APPDATA%\Namager\preset-usage-cache.json (PresetUsageCache.DefaultPath). Tests pass
    /// a temp path — a test that connects must never touch the developer's own cache.</summary>
    private readonly string? _usageCachePath;
```

Ctor chain (existing 0/1/2-arg call sites must compile unchanged):

```csharp
    public MainWindowViewModel() : this(null, null, null) { }

    public MainWindowViewModel(string? settingsPath) : this(settingsPath, null, null) { }

    public MainWindowViewModel(string? settingsPath, string? irIndexPath)
        : this(settingsPath, irIndexPath, null) { }

    public MainWindowViewModel(string? settingsPath, string? irIndexPath, string? usageCachePath)
    {
        _settingsPath = settingsPath;
        _irIndexPath = irIndexPath;
        _usageCachePath = usageCachePath;
        ... // existing body unchanged
```

In the `Connected` handler (~line 246) replace:

```csharp
            var usage = _usageService = new PresetUsageService(_connection.Repository!);
```

with:

```csharp
            // Device id keys the warm-start cache; a blank id (unknown firmware) disables it.
            var usage = _usageService = new PresetUsageService(
                _connection.Repository!, _connection.DeviceId, _usageCachePath);
```

- [ ] **Step 3: Full suite**

Run: `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`
Expected: all pass (this task is wiring; Task 3's tests cover the behavior).

- [ ] **Step 4: Commit**

```bash
git add src/Namager.App/ViewModels/ConnectionViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs
git commit -m "feat(usage-cache): key the warm-start cache by the connected pedal's id"
```

---

### Task 5: Amp detail pane — third `Verifying` usage state

**Files:**
- Modify: `src/Namager.App/ViewModels/AmpDetailViewModel.cs` (enum ~line 16, `RefreshUsage` ~line 192, `IsUsageChecking`/`IsUsageEmpty` ~line 188)
- Modify: the amp detail view (`src/Namager.App/Views/AmpDetailView.axaml` — locate with Glob if named differently; the usage section binds `IsUsageChecking`/`IsUsageEmpty`/`UsedInPresets`)
- Test: `tests/Namager.App.Tests/AmpDetailUsageTests.cs` (extend; if usage-state tests live elsewhere, e.g. `UsageStateTests.cs`, follow them there)

**Interfaces:**
- Consumes: `IPresetUsageService.Current` / `.IsComplete` (unchanged interface).
- Produces: `public enum AmpUsageState { Checking, Verifying, Complete }`, `public bool IsUsageVerifying`.

**Behavior:** when the map is incomplete but already has entries for this amp (cached warm start OR a mid-scan partial), show them with a "verifying…" badge instead of hiding behind "Checking…". An incomplete map with NO entries for the amp stays `Checking` — "cached-and-unused" is indistinguishable from "unknown", and rendering it as empty would read as "unused", which is exactly the wrong thing to tell someone deciding on a delete. Delete/rename guards are unaffected (they use `EnsureCompleteAsync`).

- [ ] **Step 1: Write the failing tests** (in the existing amp-detail usage test file, using its established helpers — read the file first and follow its construction pattern with `FakePresetUsageService`):

```csharp
[Fact]
public void Incomplete_map_with_entries_shows_them_as_verifying()
{
    var usage = new FakePresetUsageService
    {
        Map = FakePresetUsageService.MapFor((0, "Lead", "Plexi")),
        Complete = false,
    };
    var vm = MakeVm(usage);                       // this file's existing helper
    ShowAmp(vm, "Plexi");                         // this file's existing show/load helper

    Assert.Equal(AmpUsageState.Verifying, vm.UsageState);
    Assert.True(vm.IsUsageVerifying);
    Assert.False(vm.IsUsageChecking);
    Assert.False(vm.IsUsageEmpty);
    Assert.Equal("Lead", Assert.Single(vm.UsedInPresets).Name);
}

[Fact]
public void Incomplete_map_with_no_entries_for_this_amp_stays_checking()
{
    var usage = new FakePresetUsageService
    {
        Map = FakePresetUsageService.MapFor((0, "Lead", "SomeOtherAmp")),
        Complete = false,
    };
    var vm = MakeVm(usage);
    ShowAmp(vm, "Plexi");

    Assert.Equal(AmpUsageState.Checking, vm.UsageState);
    Assert.Empty(vm.UsedInPresets);
}

[Fact]
public void Verifying_entries_promote_to_complete_when_the_scan_finishes()
{
    var usage = new FakePresetUsageService
    {
        Map = FakePresetUsageService.MapFor((0, "Lead", "Plexi")),
        Complete = false,
    };
    var vm = MakeVm(usage);
    ShowAmp(vm, "Plexi");
    Assert.Equal(AmpUsageState.Verifying, vm.UsageState);

    usage.Complete = true;
    usage.RaiseMapUpdated();                      // this fake's existing raise helper (check name)

    Assert.Equal(AmpUsageState.Complete, vm.UsageState);
    Assert.Equal("Lead", Assert.Single(vm.UsedInPresets).Name);
}
```

(Adapt helper names — `MakeVm`, `ShowAmp`, `RaiseMapUpdated`, `MapFor` — to what the file actually provides; do NOT invent new fakes when `FakePresetUsageService` already has settable `Map`/`Complete` and builders.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~AmpDetail" 2>&1 | tail -5`
Expected: FAIL — `AmpUsageState.Verifying` / `IsUsageVerifying` do not exist.

- [ ] **Step 3: Implement.** Enum (keep the doc comment's spirit, extend it):

```csharp
/// <summary>Whether the preset-usage answer for an amp is known yet. <see cref="Checking"/> and an
/// empty <see cref="AmpDetailViewModel.UsedInPresets"/> mean "we don't know"; <see cref="Verifying"/>
/// means "here's what the cache / partial scan says, the scan hasn't confirmed it yet";
/// <see cref="Complete"/> with an empty list means "genuinely unused". Only Complete may be
/// trusted for a delete decision — and the guards independently enforce that via
/// EnsureCompleteAsync.</summary>
public enum AmpUsageState { Checking, Verifying, Complete }
```

`RefreshUsage` and the derived flags:

```csharp
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsageChecking))]
    [NotifyPropertyChangedFor(nameof(IsUsageVerifying))]
    [NotifyPropertyChangedFor(nameof(IsUsageEmpty))]
    private AmpUsageState _usageState = AmpUsageState.Checking;

    public bool IsUsageChecking => UsageState == AmpUsageState.Checking;
    public bool IsUsageVerifying => UsageState == AmpUsageState.Verifying;
    public bool IsUsageEmpty => UsageState == AmpUsageState.Complete && UsedInPresets.Count == 0;

    /// <summary>Recompute the usage section from the current map for whatever amp is displayed.
    /// Incomplete map + entries = Verifying (warm-start cache or mid-scan partial — show them,
    /// badged); incomplete + nothing = Checking (unknown ≠ unused); complete = the truth.</summary>
    private void RefreshUsage()
    {
        UsedInPresets.Clear();
        if (Name is not { Length: > 0 } name)
        {
            UsageState = AmpUsageState.Checking;
            OnPropertyChanged(nameof(IsUsageEmpty));
            return;
        }
        var refs = _usage.Current.PresetsUsingAmp(name);
        if (_usage.IsComplete)
        {
            foreach (var r in refs) UsedInPresets.Add(r);
            UsageState = AmpUsageState.Complete;
        }
        else if (refs.Count > 0)
        {
            foreach (var r in refs) UsedInPresets.Add(r);
            UsageState = AmpUsageState.Verifying;
        }
        else
        {
            UsageState = AmpUsageState.Checking;
        }
        OnPropertyChanged(nameof(IsUsageEmpty));
    }
```

XAML: in the usage section of the amp detail view, add a badge next to the section header (exact layout: match the sibling "Checking…" text's style), visible only while verifying:

```xml
<TextBlock Text="verifying…"
           IsVisible="{Binding IsUsageVerifying}"
           Foreground="{DynamicResource Sonulab.TextSecondaryBrush}"
           FontStyle="Italic" FontSize="11"/>
```

(Use the token the neighboring secondary/hint text in that view actually uses — read the view first; do not introduce a new brush and never a hex literal. The preset list itself renders in both states, so bind its ItemsControl visibility to include Verifying if it is currently gated on Complete.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~AmpDetail" 2>&1 | tail -3`
Expected: PASS, including all pre-existing three-state tests.

- [ ] **Step 5: Full suite, commit**

Run: `dotnet test 2>&1 | grep -E "(Passed!|Failed!)"`

```bash
git add src/Namager.App/ViewModels/AmpDetailViewModel.cs src/Namager.App/Views/ tests/Namager.App.Tests/
git commit -m "feat(usage-cache): amp detail shows cached/partial usage with a verifying badge"
```

---

### Task 6: Docs — STATUS, PRIVACY, hardware validation checklist

**Files:**
- Modify: `docs/STATUS.md` (the scan-cost / follow-ups area)
- Modify: `PRIVACY.md` (local-files section, next to the ir-index entry)
- Create: `docs/HARDWARE-VALIDATION-usage-cache.md`

- [ ] **Step 1: `docs/STATUS.md`** — in the section that mentions the preset-usage scan cost / pipelining follow-up, add one line:

```markdown
- Preset-usage warm start: reconnect seeds highlights from `%APPDATA%\Namager\preset-usage-cache.json`
  (keyed by pedal id, per-slot name match); the background scan still runs to completion and corrects.
  Pipelining the scan (groups of 4, ~14 s → ~8 s) remains open and composes with this.
```

- [ ] **Step 2: `PRIVACY.md`** — next to the existing ir-index/local-file statements add:

```markdown
- `preset-usage-cache.json` stores, per connected pedal id, each preset's name and the amp/IR
  names it references — so reconnecting shows usage highlights instantly. Local only; never
  transmitted.
```

(Match the file's existing list style and the scoping language the snapshot/hash entries use — read it first.)

- [ ] **Step 3: Create `docs/HARDWARE-VALIDATION-usage-cache.md`:**

```markdown
# Hardware validation — preset-usage warm-start cache

Feature: reconnect shows amp/IR usage highlights instantly from
`%APPDATA%\Namager\preset-usage-cache.json`; the background scan revalidates and corrects.
All checks are read-only for the pedal except where marked.

- [ ] Cold connect (cache file deleted): Amps/IRs tabs behave exactly as before — highlights
      fill progressively over the scan (~15–30 s), amp detail shows "Checking…" then results.
- [ ] Disconnect, restart NAMager, reconnect: highlights on the Amps and IRs tabs appear
      **< 1 s** after connect; amp detail shows entries with the "verifying…" badge; badge
      clears when the scan completes; highlights unchanged (cache agreed with the device).
- [ ] Stale in-place edit: with NAMager closed, change one preset's amp on the pedal
      (front panel) or in VoidX [DEVICE WRITE — needs the pedal owner]. Reconnect: the OLD
      highlight shows provisionally, then corrects itself when the scan reaches that slot;
      the cache file afterwards contains the new amp name.
- [ ] Rename/delete outside the app: with NAMager closed, rename or delete a preset in VoidX
      [DEVICE WRITE]. Reconnect: that slot contributes NO provisional highlight (name
      mismatch drops it); it reappears (or stays gone) once the scan covers it.
- [ ] Guard unchanged: during the provisional phase (badge visible), attempt an amp delete →
      the guard still blocks/waits on the real scan, not the cache.
- [ ] Second pedal (if available): connect pedal B → its map caches under its own id;
      reconnect pedal A → A's warm start unaffected.
```

- [ ] **Step 4: Full suite (docs don't affect it — sanity only), commit**

```bash
git add docs/STATUS.md PRIVACY.md docs/HARDWARE-VALIDATION-usage-cache.md
git commit -m "docs(usage-cache): STATUS/PRIVACY notes + hardware validation checklist"
```

---

### Task 7: Sonulab firmware-request memo

**Files:**
- Create: `docs/sonulab-firmware-request-usage-map.md`

Technical request only — no pricing/partner/strategy content (that would belong in `../Namager.Strategy/`). This is publishable: it describes the protocol and a proposed command, exactly the kind of technical design this repo's docs policy allows.

- [ ] **Step 1: Write the memo.** Content requirements (write it in full, in the repo's plain factual doc style; cite PROTOCOL.md sections rather than restating them at length):

  1. **Problem** — listing which presets use which amp/IR requires reading each preset's content head over `dread` (128 B/chunk, one chunk per round trip, ~57 ms lockstep): ~14–25 chunks × up to 30 presets ≈ 15–30 s per connect. NAMager needs this map to warn before an amp/IR rename or delete orphans presets (references are by name — PROTOCOL.md "Reference integrity").
  2. **Request A (preferred): a usage/refs read.** One command, e.g. `read root\presets\refs`, returning per occupied slot the three reference values the firmware already parses at preset load: `[{"index":0,"preset":"Lead","amp":"Plexi","ir":"Cab A","ir2":""}, …]`. Even as ~30 CRLF records it is one round trip per record at worst and ~1–2 s total — a 10–20× improvement. Fits the existing self-describing node convention (`browse` discoverable).
  3. **Request B (alternative): a change counter.** A `root\presets\_rev` node (u32, bumped on any preset save/rename/delete/swap) — lets hosts cache aggressively and re-scan only when it moves. Cheapest possible firmware change (no new parsing); per-slot counters (`"rev":[…30 ints]` on the list node) would be even better.
  4. **What we do today / without this** — windowed head reads + a PC-side per-device cache keyed on `root\sys\_id` with per-slot name matching; works, but an in-place edit made on the pedal is undetectable until a full re-scan (cite the warm-start feature).
  5. **Compatibility note** — both requests are additive (new nodes), invisible to VoidX, and NAMager feature-detects via `browse` so nothing breaks on older firmware.

- [ ] **Step 2: Commit**

```bash
git add docs/sonulab-firmware-request-usage-map.md
git commit -m "docs: firmware request — preset reference read / change counter"
```

---

## Verification (whole feature)

1. `dotnet test` at the solution root — all projects green (baseline was 961 + new tests).
2. `dotnet build` — no warnings introduced in touched files.
3. Read-only smoke on the connected bench pedal (allowed while Ed is remote):
   `dotnet run --project tools/HwCheck` — connects, lists presets (confirms nothing in Core broke the read path). Do NOT run any write/reorder/upload modes.
4. App-level hardware validation: the checklist in `docs/HARDWARE-VALIDATION-usage-cache.md` — the two [DEVICE WRITE] items and the app-UI items need Ed at the pedal; leave the checklist unchecked for him.

## Risks

- Highest regression surface: the `RunScanPassAsync` rework (Task 3). The pre-existing invalidate/torn-read/progressive tests are the guard rail — they must pass unmodified.
- Accepted staleness: in-place edits outside the app show stale provisional highlights for up to one scan (~15–30 s). Documented in PresetUsageCache's class comment, PRIVACY-adjacent docs, and the validation checklist.
- The cache cannot represent "occupied but references nothing" (the map has no row for such slots) — harmless today; a future skip-revalidation optimization would need a schema bump.
- Two app instances share the file last-writer-wins — acceptable; the file is advisory only.
