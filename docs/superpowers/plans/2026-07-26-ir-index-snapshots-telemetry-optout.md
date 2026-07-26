# IR Identity Index, Pedal Snapshots, and Telemetry Opt-Out — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give IRs the Tone3000 identity that amps already carry, add a whole-pedal snapshot file, and let users turn off the anonymous usage ping.

**Architecture:** Three independent pieces that share one idea — content, not slot position, identifies what's on the pedal. A 4096-byte IR blob has no room for embedded metadata, so its Tone3000 identity lives in a local index keyed by `sha256` of the blob; slots get reordered and overwritten, so content is the only stable handle. The `.namsnap` snapshot records the same identity per slot, so importing a snapshot rebuilds the index. Nothing here touches the network beyond the ping that already exists.

Amps have an SSMD metadata block with room to spare, and it already carries enough to identify them — just not in the form this plan needs. Amp identity is therefore out of scope here, for reasons worth reading before assuming otherwise. See Scope.

**Tech Stack:** .NET 10, C#, Avalonia 12 (built-in FluentTheme), xUnit, `System.IO.Compression` for ZIP, `System.Text.Json`.

## Global Constraints

- **No new NuGet dependencies.** ZIP and JSON come from the BCL.
- **`Sonulab.Core` stays UI-free and OS-path-free.** Anything that knows about `%APPDATA%` lives in `Namager.App/Services/`, alongside `UsageState` and `AppSettings`.
- **Device writes are destructive** and require explicit consent, read-back verification, and rollback. This plan performs **no device writes** — it only reads.
- **Telemetry must never surface an error to the user.** Any new code on that path follows the existing contract in `UsagePingService`: never throws, never blocks the UI, silent on every failure.
- **Never hardcode colors in `.axaml`.** Use `Sonulab.*Brush` tokens from `Styles/SonulabTheme.axaml`.
- **Device names cap at 31 characters** (`SlotBlobService.NameMaxChars`).
- Slot counts: 30 each for presets / amps / IRs. Sizes: preset 8192 B, amp 12288 B, IR 4096 B.
- All new tests must be pure and offline — no hardware, no network. Use `FakePresetDevice` for device-shaped tests.
- `dotnet test` must pass at every commit.

## Scope

**In:** the local IR identity index; capturing a `.namsnap` and writing it to disk; reading and validating a `.namsnap` and rebuilding the index from it; the usage-ping opt-out.

**Out — and deliberately so:**

- *Restoring a snapshot back onto the pedal.* That needs selective per-slot choice, a resumable and cancellable multi-minute write, cross-device refusal rules, and its own hardware-validation pass. It is a substantial piece of work that deserves its own plan; folding it in here would make this one unreviewable. Everything in this plan is useful without it — export is a backup users can keep, and the index improves the IR list immediately.
- *Tone3000 identity for amps.* Amp `T3k` is always `null` in a manifest produced by this plan — but not because the information is missing. Verified against real files in `NAMFiles/Distilled/`, an amp's SSMD block carries the **tone** id inside a URL slug (`url` = `…/tones/fender-vibroverb-64-43728`) and `source.sha256`, the hash of the `.nam` Tone3000 served. It does **not** carry a model id in any direct form. Populating amp `T3k` therefore means either parsing a slug or adding a source-hash index — both real work with their own decisions. See Task 6.

## File Structure

| File | Responsibility |
|---|---|
| `src/Namager.App/Services/IrIndex.cs` *(new)* | Content-hash → Tone3000 identity map, persisted at `%APPDATA%\Namager\ir-index.json`. Pure file I/O + lookup. |
| `src/Sonulab.Core/Model/SnapshotManifest.cs` *(new)* | The `manifest.json` record shape and its slot entries. No I/O. |
| `src/Sonulab.Core/Services/SnapshotArchive.cs` *(new)* | Reads and writes the `.namsnap` ZIP container. No device access. |
| `src/Sonulab.Core/Services/SnapshotService.cs` *(new)* | Captures a snapshot from a live device using `DeviceRepository` / `AmpService` / `IrService`. |
| `src/Namager.App/ViewModels/Tone3000ViewModel.cs` *(modify)* | Carry the Tone3000 identity on the send-to-pedal handoff. |
| `src/Namager.App/ViewModels/MainWindowViewModel.cs` *(modify)* | Thread that identity through to the IR upload; wire snapshot menu commands. |
| `src/Namager.App/ViewModels/IrListViewModel.cs` *(modify)* | Record an index entry after a successful IR upload. |
| `src/Namager.App/Services/AppSettings.cs` *(modify)* | Add the `ShareUsageData` preference. |
| `src/Namager.App/Services/UsagePingService.cs` *(modify)* | Honor the preference. |
| `src/Namager.App/Views/MainWindow.axaml` *(modify)* | File menu entries; Settings toggle. |
| `PRIVACY.md` *(modify)* | Document the opt-out and what a snapshot contains. |

---

### Task 1: The IR identity index

**Files:**
- Create: `src/Namager.App/Services/IrIndex.cs`
- Test: `tests/Namager.App.Tests/IrIndexTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `IrIndexEntry(string Sha, long ToneId, long ModelId, string? Title)`; `IrIndex` with `static string DefaultPath`, `static IrIndex Load(string? path = null)`, `void Save(string? path = null)`, `IrIndexEntry? Lookup(string sha)`, `IrIndex Record(IrIndexEntry entry)`, and `static string ShaOf(ReadOnlySpan<byte> blob)`.

`Record` returns a new `IrIndex` rather than mutating, matching the immutable-record style of `UsageState`. `ShaOf` is the single place a blob hash is computed, so every caller agrees on casing and encoding.

- [ ] **Step 1: Write the failing tests**

```csharp
using Namager.App.Services;

namespace Namager.App.Tests;

public class IrIndexTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ir-index-{Guid.NewGuid():N}.json");

    [Fact]
    public void ShaOf_is_lowercase_hex_and_stable()
    {
        var blob = new byte[4096];
        blob[0] = 0xAB;
        var a = IrIndex.ShaOf(blob);
        var b = IrIndex.ShaOf(blob);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a.ToLowerInvariant(), a);
    }

    [Fact]
    public void ShaOf_differs_for_different_content()
    {
        var one = new byte[4096];
        var two = new byte[4096];
        two[4095] = 1;

        Assert.NotEqual(IrIndex.ShaOf(one), IrIndex.ShaOf(two));
    }

    [Fact]
    public void Record_then_Lookup_round_trips_through_a_file()
    {
        var path = TempPath();
        try
        {
            var entry = new IrIndexEntry("abc123", ToneId: 2468, ModelId: 1357, Title: "4x12 Greenback");
            IrIndex.Load(path).Record(entry).Save(path);

            var found = IrIndex.Load(path).Lookup("abc123");

            Assert.NotNull(found);
            Assert.Equal(2468, found!.ToneId);
            Assert.Equal(1357, found.ModelId);
            Assert.Equal("4x12 Greenback", found.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Lookup_returns_null_for_unknown_content()
    {
        Assert.Null(IrIndex.Load(TempPath()).Lookup("not-in-there"));
    }

    [Fact]
    public void Record_replaces_an_entry_with_the_same_sha()
    {
        var path = TempPath();
        try
        {
            IrIndex.Load(path)
                   .Record(new IrIndexEntry("dup", 1, 1, "old"))
                   .Record(new IrIndexEntry("dup", 2, 2, "new"))
                   .Save(path);

            var index = IrIndex.Load(path);

            Assert.Equal(1, index.Entries.Count);
            Assert.Equal("new", index.Lookup("dup")!.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_loads_as_empty_and_does_not_throw()
    {
        var index = IrIndex.Load(TempPath());
        Assert.Empty(index.Entries);
    }

    [Fact]
    public void Corrupt_file_loads_as_empty_and_does_not_throw()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Empty(IrIndex.Load(path).Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_schema_version_loads_as_empty_rather_than_guessing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{"schema":999,"entries":[{"sha":"x","toneId":1,"modelId":2}]}""");
            Assert.Empty(IrIndex.Load(path).Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_to_an_unwritable_path_does_not_throw()
    {
        var index = IrIndex.Load(TempPath()).Record(new IrIndexEntry("x", 1, 2, null));
        // A directory path is never a writable file target.
        index.Save(Path.GetTempPath());
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~IrIndexTests`
Expected: FAIL — `IrIndex` / `IrIndexEntry` do not exist (compile error).

- [ ] **Step 3: Implement `IrIndex`**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namager.App.Services;

/// <summary>One IR's Tone3000 identity, keyed by the SHA-256 of its 4096-byte pedal blob.</summary>
public sealed record IrIndexEntry(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("toneId")] long ToneId,
    [property: JsonPropertyName("modelId")] long ModelId,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>Maps IR blob content to the Tone3000 tone/model it came from.
///
/// A 4096-byte IR blob has no room for embedded metadata the way a 12288-byte vxamp does, so its
/// identity has to live on the PC instead.
///
/// Keyed by content rather than slot because slots move — NAMager reorders IR slots itself, and
/// VoidX can overwrite them. Content is the only stable handle.
///
/// The hash is a lookup key, not an identifier that travels: it is computed and consumed on the
/// same machine, and never sent anywhere. See PRIVACY.md.
///
/// Every failure mode (missing, corrupt, unknown schema, unwritable) degrades to "empty" rather
/// than throwing — a bad index file must never stop the app or an upload.</summary>
public sealed class IrIndex
{
    public const int Schema = 1;

    private readonly Dictionary<string, IrIndexEntry> _bySha;

    private IrIndex(IEnumerable<IrIndexEntry> entries) =>
        _bySha = entries.ToDictionary(e => e.Sha, StringComparer.Ordinal);

    public IReadOnlyCollection<IrIndexEntry> Entries => _bySha.Values;

    /// <summary>%APPDATA%\Namager\ir-index.json — the same directory as usage.json and
    /// settings.json. Guarded like AppSettingsStore.DefaultPath: a throwing folder lookup must
    /// not poison the type initializer.</summary>
    public static string DefaultPath
    {
        get
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Namager", "ir-index.json");
            }
            catch { return "ir-index.json"; }
        }
    }

    public static string ShaOf(ReadOnlySpan<byte> blob) =>
        Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

    public IrIndexEntry? Lookup(string sha) =>
        _bySha.TryGetValue(sha, out var e) ? e : null;

    /// <summary>Returns a new index with <paramref name="entry"/> added or replacing any entry
    /// with the same sha. Does not write to disk — call Save.</summary>
    public IrIndex Record(IrIndexEntry entry)
    {
        var next = new Dictionary<string, IrIndexEntry>(_bySha, StringComparer.Ordinal)
        {
            [entry.Sha] = entry,
        };
        return new IrIndex(next.Values);
    }

    public static IrIndex Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new IrIndex([]);

            var doc = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(file));
            // A file from a future writer is not safely readable — treat it as empty rather than
            // guessing at a shape we don't know.
            if (doc is null || doc.Schema != Schema || doc.Entries is null) return new IrIndex([]);

            return new IrIndex(doc.Entries.Where(e => !string.IsNullOrEmpty(e.Sha)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return new IrIndex([]);
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
                new IndexFile(Schema, [.. _bySha.Values]),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // Losing the index costs a title in the UI, never an upload or a user's data.
        }
    }

    private sealed record IndexFile(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("entries")] IrIndexEntry[]? Entries);
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~IrIndexTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/IrIndex.cs tests/Namager.App.Tests/IrIndexTests.cs
git commit -m "feat(ir): content-keyed Tone3000 identity index for IRs"
```

---

### Task 2: Carry Tone3000 identity to the IR upload

**Files:**
- Modify: `src/Namager.App/ViewModels/Tone3000ViewModel.cs:68` (event signature), `:245-262` (`SendToPedalAsync`)
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:175-176`, `:276`, `:283-291`
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs:211-230` (`BeginUpload`), `:232-266` (`StartUploadAsync`)
- Test: `tests/Namager.App.Tests/IrListViewModelTests.cs` (add), `tests/Namager.App.Tests/Tone3000ViewModelTests.cs` (add)

**Interfaces:**
- Consumes: `IrIndex`, `IrIndexEntry`, `IrIndex.ShaOf` from Task 1.
- Produces: `record T3kIrSource(long ToneId, long ModelId, string? Title)`; `Tone3000ViewModel.SendToPedalRequested` becomes `Action<string, string?, string?, bool, T3kIrSource?>`; `MainWindowViewModel.NavigateToUploadAsync(bool isIr, string path, string? notes, string? url, T3kIrSource? irSource)`; `IrListViewModel.BeginUploadFromTone3000(string path, T3kIrSource source)`.

`MainWindowViewModel.cs:290` currently comments *"IRs: name prefill via filename; no SSMD"* — that is exactly the gap being closed. Amps get identity via SSMD; IRs get it via the index.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Namager.App.Tests/IrListViewModelTests.cs` (follow the fixture/constructor pattern already used in that file for building an `IrListViewModel` over a fake service):

```csharp
[Fact]
public async Task Uploading_a_Tone3000_IR_records_an_index_entry()
{
    var indexPath = Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");
    var blob = new byte[4096]; blob[7] = 42;
    var vm = BuildVm(writes: true, irIndexPath: indexPath);   // see Step 3 for the new ctor arg
    try
    {
        vm.BeginUploadFromTone3000(WriteTempIrFile(blob), new T3kIrSource(2468, 1357, "4x12 Greenback"));
        vm.UploadName = "Greenback";
        await vm.StartUploadCommand.ExecuteAsync(null);

        var entry = IrIndex.Load(indexPath).Lookup(IrIndex.ShaOf(blob));
        Assert.NotNull(entry);
        Assert.Equal(2468, entry!.ToneId);
        Assert.Equal(1357, entry.ModelId);
        Assert.Equal("4x12 Greenback", entry.Title);
    }
    finally { File.Delete(indexPath); }
}

[Fact]
public async Task Uploading_a_local_file_records_nothing()
{
    var indexPath = Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");
    var vm = BuildVm(writes: true, irIndexPath: indexPath);
    try
    {
        vm.BeginUploadCommand.Execute(WriteTempIrFile(new byte[4096]));
        vm.UploadName = "Handmade";
        await vm.StartUploadCommand.ExecuteAsync(null);

        Assert.Empty(IrIndex.Load(indexPath).Entries);
    }
    finally { File.Delete(indexPath); }
}

[Fact]
public async Task A_failed_upload_records_nothing()
{
    var indexPath = Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");
    var vm = BuildVm(writes: true, irIndexPath: indexPath, uploadThrows: true);
    try
    {
        vm.BeginUploadFromTone3000(WriteTempIrFile(new byte[4096]), new T3kIrSource(1, 2, "x"));
        vm.UploadName = "Doomed";
        await vm.StartUploadCommand.ExecuteAsync(null);

        Assert.Empty(IrIndex.Load(indexPath).Entries);
    }
    finally { File.Delete(indexPath); }
}
```

Add to `tests/Namager.App.Tests/Tone3000ViewModelTests.cs`:

```csharp
[Fact]
public async Task SendToPedal_passes_the_tone_and_model_ids_for_an_IR()
{
    var vm = BuildVmWithTone(format: "ir", toneId: 2468, title: "4x12 Greenback");
    T3kIrSource? captured = null;
    vm.SendToPedalRequested += (_, _, _, _, src) => captured = src;

    await vm.SendToPedalCommand.ExecuteAsync(new T3kModel { Id = 1357 });

    Assert.NotNull(captured);
    Assert.Equal(2468, captured!.ToneId);
    Assert.Equal(1357, captured.ModelId);
    Assert.Equal("4x12 Greenback", captured.Title);
}

[Fact]
public async Task SendToPedal_passes_no_IR_source_for_a_NAM_amp()
{
    var vm = BuildVmWithTone(format: "nam", toneId: 11, title: "Dumble");
    T3kIrSource? captured = new(0, 0, null);
    vm.SendToPedalRequested += (_, _, _, _, src) => captured = src;

    await vm.SendToPedalCommand.ExecuteAsync(new T3kModel { Id = 22 });

    Assert.Null(captured);   // amps are not indexed — see the plan's Scope section
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~IrListViewModelTests|FullyQualifiedName~Tone3000ViewModelTests"`
Expected: FAIL — `T3kIrSource` and `BeginUploadFromTone3000` do not exist.

- [ ] **Step 3: Thread the identity through**

In `src/Namager.App/ViewModels/Tone3000ViewModel.cs`, add the record near the top of the namespace and widen the event:

```csharp
/// <summary>Tone3000 identity for an IR being sent to the pedal, recorded in the IR index once
/// the write succeeds. Amps are not indexed here — their SSMD block already carries a tone id in
/// its url and a source hash for the model, so covering them is a separate change.</summary>
public sealed record T3kIrSource(long ToneId, long ModelId, string? Title);
```

```csharp
/// <summary>Handoff to MainWindowViewModel: local file path, SSMD notes, SSMD url, isIr,
/// and (IRs only) the Tone3000 identity to record in the IR index.</summary>
public event Action<string, string?, string?, bool, T3kIrSource?>? SendToPedalRequested;
```

In `SendToPedalAsync`, replace the invoke:

```csharp
string? notes = tone is { } t ? $"{t.Title} by {t.Author} (Tone3000)" : null;
string? url = tone?.PageUrl;
// Only IRs are indexed; amp identity is a separate change (see the plan's Scope section).
var irSource = isIr && tone is { } src ? new T3kIrSource(src.Id, model.Id, src.Title) : null;
SendToPedalRequested?.Invoke(path, notes, url, isIr, irSource);
```

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, update the subscription and the navigation method:

```csharp
_tone3000.SendToPedalRequested += (path, notes, url, isIr, irSource) =>
    NavigateToUpload(isIr, path, notes, url, irSource);   // fire-and-forget wrapper
```

```csharp
private void NavigateToUpload(bool isIr, string path, string? notes, string? url, T3kIrSource? irSource) =>
    _ = NavigateToUploadAsync(isIr, path, notes, url, irSource);

public async Task NavigateToUploadAsync(bool isIr, string path, string? notes, string? url,
                                        T3kIrSource? irSource = null)
{
    if (isIr)
    {
        if (Irs is not { } irs) { Tone3000.Banner = "Connect to the pedal first."; return; }
        NavigateRequested?.Invoke(2);
        if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load */ } }
        if (irSource is { } src) irs.BeginUploadFromTone3000(path, src);
        else irs.BeginUploadCommand.Execute(path);
    }
    else
    {
        if (Amps is not { } amps) { Tone3000.Banner = "Connect to the pedal first."; return; }
        NavigateRequested?.Invoke(1);
        if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load */ } }
        amps.BeginUploadPrefilled(path, notes, url);
    }
}
```

In `src/Namager.App/ViewModels/IrListViewModel.cs`, add an index path field (constructor-injected with a null default so tests can point it at a temp file, matching how `UsagePingService` takes `statePath`), a pending-source field, the new entry point, and the record-on-success step:

```csharp
private readonly string? _irIndexPath;
private T3kIrSource? _pendingSource;
```

```csharp
/// <summary>Begins an upload that came from Tone3000, remembering the identity to record in the
/// IR index once the write succeeds. Prefills the name the same way BeginUpload does.</summary>
public void BeginUploadFromTone3000(string path, T3kIrSource source)
{
    BeginUploadCommand.Execute(path);
    _pendingSource = source;      // set AFTER BeginUpload, which clears it (see below)
}
```

In `BeginUpload`, clear the pending source so a later local-file upload cannot inherit a stale identity:

```csharp
[RelayCommand] private void BeginUpload(string? path)
{
    _pendingSource = null;
    // ... existing body unchanged ...
}
```

In `StartUploadAsync`, immediately after the successful `await _irs.UploadIrAsync(...)` and before `ReloadAsync()`:

```csharp
await _irs.UploadIrAsync(slot, bytes, name, uploadProgress);

// Record identity only after the device write succeeded: an index entry for content that never
// landed would resolve to a slot the user doesn't have.
if (_pendingSource is { } src)
{
    IrIndex.Load(_irIndexPath)
           .Record(new IrIndexEntry(IrIndex.ShaOf(bytes), src.ToneId, src.ModelId, src.Title))
           .Save(_irIndexPath);
    _pendingSource = null;
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~IrListViewModelTests|FullyQualifiedName~Tone3000ViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS. The event-signature change touches every subscriber; this catches any missed one.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ tests/Namager.App.Tests/
git commit -m "feat(ir): record Tone3000 identity when installing an IR from Tone3000"
```

---

### Task 3: REMOVED - showing Tone3000 titles in the IR list

**Do not implement. There is no Task 3; dispatch continues at Task 4.**

Dropped during pre-flight review, before any implementation. Resolving a title means hashing the
IR, and hashing means reading the whole 4096-byte blob - 32 chunks at the ~33 ms/chunk documented
in `SonuClient.cs:230`, so about 1.05 s per IR and about 32 s added to every IR tab reload with 30
slots filled.

That is the exact cost the app's lazy-tab-loading work exists to avoid, and amps deliberately
dodge it: `AmpListViewModel.cs:537` reads only the SSMD region rather than all 96 chunks. IRs have
no metadata region to read cheaply, so the same trick is unavailable.

Nothing downstream depends on this. The index is still written (Task 2) and still feeds snapshot
export (Tasks 6-7); only the list-view display is deferred. Reviving it needs a design that avoids
a full read per slot on every reload - progressive background resolution, or on-selection
resolution matching the amp details cache at `AmpListViewModel.cs:521-527`.


### Task 4: The `.namsnap` manifest model

**Files:**
- Create: `src/Sonulab.Core/Model/SnapshotManifest.cs`
- Test: `tests/Sonulab.Core.Tests/SnapshotManifestTests.cs`

**Interfaces:**
- Produces: `SnapshotSlotKind` enum (`Preset`, `Amp`, `Ir`); `SnapshotT3k(long ToneId, long ModelId)`; `SnapshotSlot(SnapshotSlotKind Kind, int Index, string Name, string Sha, SnapshotT3k? T3k)`; `SnapshotDevice(string Model, string Fw)`; `SnapshotManifest(int Schema, string CreatedUtc, string AppVersion, SnapshotDevice Device, IReadOnlyList<SnapshotSlot> Slots)` with `const int CurrentSchema = 1`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Sonulab.Core.Model;

namespace Sonulab.Core.Tests;

public class SnapshotManifestTests
{
    private static SnapshotManifest Sample() => new(
        SnapshotManifest.CurrentSchema, "2026-07-26T14:02:11Z", "0.9.7",
        new SnapshotDevice("StompStation", "2.5.1"),
        [
            new SnapshotSlot(SnapshotSlotKind.Preset, 0, "Steel Clean", "aa", null),
            new SnapshotSlot(SnapshotSlotKind.Ir, 11, "4x12 Green", "bb", new SnapshotT3k(2468, 1357)),
        ]);

    [Fact]
    public void Round_trips_through_json_preserving_every_field()
    {
        var json = JsonSerializer.Serialize(Sample());
        var back = JsonSerializer.Deserialize<SnapshotManifest>(json)!;

        Assert.Equal(1, back.Schema);
        Assert.Equal("StompStation", back.Device.Model);
        Assert.Equal(2, back.Slots.Count);
        Assert.Equal(SnapshotSlotKind.Ir, back.Slots[1].Kind);
        Assert.Equal(2468, back.Slots[1].T3k!.ToneId);
        Assert.Null(back.Slots[0].T3k);
    }

    [Fact]
    public void Kind_serializes_as_a_lowercase_string_not_an_integer()
    {
        Assert.Contains("\"ir\"", JsonSerializer.Serialize(Sample()));
        Assert.DoesNotContain("\"kind\":2", JsonSerializer.Serialize(Sample()));
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotManifestTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the model**

```csharp
using System.Text.Json.Serialization;

namespace Sonulab.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<SnapshotSlotKind>))]
public enum SnapshotSlotKind { Preset, Amp, Ir }

/// <summary>Tone3000 identity for a slot. Populated for IRs resolved through the local index;
/// null otherwise, including for every amp until amps carry machine-readable ids.</summary>
public sealed record SnapshotT3k(
    [property: JsonPropertyName("toneId")] long ToneId,
    [property: JsonPropertyName("modelId")] long ModelId);

public sealed record SnapshotSlot(
    [property: JsonPropertyName("kind")] SnapshotSlotKind Kind,
    [property: JsonPropertyName("idx")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("t3k")] SnapshotT3k? T3k);

public sealed record SnapshotDevice(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("fw")] string Fw);

/// <summary>manifest.json inside a .namsnap. Names ARE recorded here — this is the user's own
/// backup of their own pedal. It is the telemetry path that never sees names, not this file.</summary>
public sealed record SnapshotManifest(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("device")] SnapshotDevice Device,
    [property: JsonPropertyName("slots")] IReadOnlyList<SnapshotSlot> Slots)
{
    public const int CurrentSchema = 1;
}
```

`JsonStringEnumConverter` writes `"Ir"` by default; add `JsonNamingPolicy.CamelCase` to the converter attribute if the lowercase assertion fails — verify against the actual test run rather than assuming.

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotManifestTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Model/SnapshotManifest.cs tests/Sonulab.Core.Tests/SnapshotManifestTests.cs
git commit -m "feat(snapshot): manifest model for the .namsnap container"
```

---

### Task 5: The `.namsnap` archive reader/writer

**Files:**
- Create: `src/Sonulab.Core/Services/SnapshotArchive.cs`
- Test: `tests/Sonulab.Core.Tests/SnapshotArchiveTests.cs`

**Interfaces:**
- Consumes: `SnapshotManifest` and friends (Task 4).
- Produces: `SnapshotArchiveException(string message)`; `static void Write(Stream destination, SnapshotManifest manifest, IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs)`; `static (SnapshotManifest Manifest, IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs) Read(Stream source)`.

Entry paths: `presets/NN.pst`, `amps/NN.vxamp`, `irs/NN.irblob`, with `NN` zero-padded to two digits. Empty slots are absent from both the manifest and the archive.

- [ ] **Step 1: Write the failing tests**

```csharp
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Sonulab.Core.Tests;

public class SnapshotArchiveTests
{
    private static (SnapshotManifest, Dictionary<(SnapshotSlotKind, int), byte[]>) Sample()
    {
        var preset = new byte[8192]; preset[0] = 1;
        var amp = new byte[12288]; amp[0] = 2;
        var ir = new byte[4096]; ir[0] = 3;

        var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>
        {
            [(SnapshotSlotKind.Preset, 0)] = preset,
            [(SnapshotSlotKind.Amp, 3)] = amp,
            [(SnapshotSlotKind.Ir, 11)] = ir,
        };
        var manifest = new SnapshotManifest(
            SnapshotManifest.CurrentSchema, "2026-07-26T14:02:11Z", "0.9.7",
            new SnapshotDevice("StompStation", "2.5.1"),
            [
                new SnapshotSlot(SnapshotSlotKind.Preset, 0, "Steel Clean", Sha(preset), null),
                new SnapshotSlot(SnapshotSlotKind.Amp, 3, "Dumble SS", Sha(amp), new SnapshotT3k(11, 22)),
                new SnapshotSlot(SnapshotSlotKind.Ir, 11, "4x12", Sha(ir), new SnapshotT3k(2468, 1357)),
            ]);
        return (manifest, blobs);
    }

    private static string Sha(byte[] b) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Round_trips_blobs_byte_for_byte()
    {
        var (manifest, blobs) = Sample();
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest, blobs);
        ms.Position = 0;

        var (readManifest, readBlobs) = SnapshotArchive.Read(ms);

        Assert.Equal(3, readBlobs.Count);
        Assert.Equal(blobs[(SnapshotSlotKind.Preset, 0)], readBlobs[(SnapshotSlotKind.Preset, 0)]);
        Assert.Equal(blobs[(SnapshotSlotKind.Amp, 3)], readBlobs[(SnapshotSlotKind.Amp, 3)]);
        Assert.Equal(blobs[(SnapshotSlotKind.Ir, 11)], readBlobs[(SnapshotSlotKind.Ir, 11)]);
        Assert.Equal("Steel Clean", readManifest.Slots[0].Name);
        Assert.Equal(2468, readManifest.Slots[2].T3k!.ToneId);
    }

    [Fact]
    public void Refuses_an_unknown_schema_version_rather_than_guessing()
    {
        var (manifest, blobs) = Sample();
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest with { Schema = 999 }, blobs);
        ms.Position = 0;

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Refuses_a_blob_whose_hash_does_not_match_the_manifest()
    {
        var (manifest, blobs) = Sample();
        var tampered = manifest.Slots.ToList();
        tampered[0] = tampered[0] with { Sha = new string('0', 64) };
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest with { Slots = tampered }, blobs);
        ms.Position = 0;

        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }

    [Fact]
    public void Refuses_a_blob_of_the_wrong_length()
    {
        var (manifest, blobs) = Sample();
        blobs[(SnapshotSlotKind.Ir, 11)] = new byte[100];
        using var ms = new MemoryStream();
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Write(ms, manifest, blobs));
    }

    [Fact]
    public void Refuses_a_manifest_slot_with_no_matching_blob()
    {
        var (manifest, blobs) = Sample();
        blobs.Remove((SnapshotSlotKind.Amp, 3));
        using var ms = new MemoryStream();
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Write(ms, manifest, blobs));
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_zip()
    {
        using var ms = new MemoryStream("this is not a zip"u8.ToArray());
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }

    [Fact]
    public void Refuses_an_archive_with_no_manifest()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("presets/00.pst");
        ms.Position = 0;

        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotArchiveTests`
Expected: FAIL — `SnapshotArchive` does not exist.

- [ ] **Step 3: Implement the archive**

```csharp
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class SnapshotArchiveException(string message) : Exception(message);

/// <summary>Reads and writes the .namsnap container: a ZIP holding manifest.json plus one entry
/// per occupied slot. The same bytes go to disk on export and would go to any remote store —
/// there is one writer and one reader, so an exported file and a stored file cannot drift.
///
/// Validation is deliberately strict in both directions: a snapshot is a backup, and a backup
/// that silently loses or corrupts a slot is worse than one that refuses to be written.</summary>
public static class SnapshotArchive
{
    public const string ManifestEntry = "manifest.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static int ExpectedLength(SnapshotSlotKind kind) => kind switch
    {
        SnapshotSlotKind.Preset => 8192,
        SnapshotSlotKind.Amp => 12288,
        SnapshotSlotKind.Ir => 4096,
        _ => throw new SnapshotArchiveException($"unknown slot kind {kind}"),
    };

    private static string PathFor(SnapshotSlotKind kind, int index) => kind switch
    {
        SnapshotSlotKind.Preset => $"presets/{index:D2}.pst",
        SnapshotSlotKind.Amp => $"amps/{index:D2}.vxamp",
        SnapshotSlotKind.Ir => $"irs/{index:D2}.irblob",
        _ => throw new SnapshotArchiveException($"unknown slot kind {kind}"),
    };

    public static string ShaOf(ReadOnlySpan<byte> blob) =>
        Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

    public static void Write(Stream destination, SnapshotManifest manifest,
                             IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs)
    {
        foreach (var slot in manifest.Slots)
        {
            if (!blobs.TryGetValue((slot.Kind, slot.Index), out var blob))
                throw new SnapshotArchiveException(
                    $"manifest lists {slot.Kind} slot {slot.Index} but no blob was supplied");
            if (blob.Length != ExpectedLength(slot.Kind))
                throw new SnapshotArchiveException(
                    $"{slot.Kind} slot {slot.Index} is {blob.Length} bytes, expected {ExpectedLength(slot.Kind)}");
        }

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        using (var entry = zip.CreateEntry(ManifestEntry).Open())
            JsonSerializer.Serialize(entry, manifest, Json);

        foreach (var slot in manifest.Slots)
        {
            using var entry = zip.CreateEntry(PathFor(slot.Kind, slot.Index)).Open();
            entry.Write(blobs[(slot.Kind, slot.Index)]);
        }
    }

    public static (SnapshotManifest Manifest, IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs)
        Read(Stream source)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new SnapshotArchiveException("not a valid .namsnap file"); }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestEntry)
                ?? throw new SnapshotArchiveException("no manifest.json — not a .namsnap file");

            SnapshotManifest? manifest;
            try
            {
                using var s = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<SnapshotManifest>(s);
            }
            catch (JsonException) { throw new SnapshotArchiveException("manifest.json is not readable"); }

            if (manifest is null) throw new SnapshotArchiveException("manifest.json is empty");
            if (manifest.Schema != SnapshotManifest.CurrentSchema)
                throw new SnapshotArchiveException(
                    $"snapshot schema {manifest.Schema} was written by a newer version of NAMager " +
                    $"(this build reads schema {SnapshotManifest.CurrentSchema}).");

            var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>();
            foreach (var slot in manifest.Slots)
            {
                var entry = zip.GetEntry(PathFor(slot.Kind, slot.Index))
                    ?? throw new SnapshotArchiveException(
                        $"manifest lists {slot.Kind} slot {slot.Index} but the file is missing");

                using var s = entry.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                var blob = buf.ToArray();

                if (blob.Length != ExpectedLength(slot.Kind))
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} is {blob.Length} bytes, expected {ExpectedLength(slot.Kind)}");
                if (!string.Equals(ShaOf(blob), slot.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} does not match its recorded hash — the file is damaged");

                blobs[(slot.Kind, slot.Index)] = blob;
            }
            return (manifest, blobs);
        }
    }
}
```

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotArchiveTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/SnapshotArchive.cs tests/Sonulab.Core.Tests/SnapshotArchiveTests.cs
git commit -m "feat(snapshot): .namsnap ZIP container with strict validation"
```

---

### Task 6: Capture a snapshot from the device

**Files:**
- Create: `src/Sonulab.Core/Services/SnapshotService.cs`
- Test: `tests/Sonulab.Core.Tests/SnapshotServiceTests.cs`

**Interfaces:**
- Consumes: `SnapshotArchive` (Task 5), `DeviceRepository`, `AmpService`, `IrService`, `VxampMetadata.TryRead`.
- Produces: `SnapshotCaptureProgress(string Stage, int Done, int Total)`; `SnapshotService(DeviceRepository presets, AmpService amps, IrService irs)` with `Task<SnapshotManifest> CaptureAsync(Stream destination, SnapshotDevice device, string appVersion, string createdUtc, Func<byte[], SnapshotT3k?>? resolveIrIdentity = null, IProgress<SnapshotCaptureProgress>? progress = null, CancellationToken ct = default)`.

`createdUtc` is passed in rather than read from the clock so the test is deterministic.

`resolveIrIdentity` takes **the IR blob**, not a slot index, so the caller can hash it and look it up without re-reading the slot. It is a callback so `Sonulab.Core` never learns about `%APPDATA%` or `IrIndex` — the app supplies the lookup.

**Amp `T3k` is always `null` in this plan** — a scope decision, not an absence of information. What an amp's SSMD block actually holds, verified by dumping real files from `NAMFiles/Distilled/`:

| Field | Example | Use for identity |
|---|---|---|
| `url` | `https://www.tone3000.com/tones/fender-vibroverb-64-43728` | **tone** id as the slug's trailing number — parseable, but depends on a Tone3000 URL convention that is theirs to change |
| `source.sha256` | `0da521e5…d212d5` | hash of the `.nam` **Tone3000 served** (not the distilled blob). Pins the exact model. |
| `nam` | `{date, loudness, gain, modeled_by, gear_make, …}` | the `.nam` file's own training metadata — nothing from Tone3000's catalog |
| `notes` | `"Fender Vibroverb 64 by musicandovitor (Tone3000)"` | display only |

There is **no model id in any direct form** — `url` is tone-level, and a tone has several models (A1/A2/custom).

`source.sha256` is the interesting one and worth knowing before the follow-up is designed: because it hashes Tone3000's *input* file rather than NAMager's *computed output*, it is identical across machines for the same model. Blob-level hashes carry no such guarantee — `ParityTests.cs:25` asserts byte-equality on only the first 32 bytes of a distilled blob, and float math varies with SIMD width and FMA contraction across CPUs.

The cleanest follow-up is to stop deriving identity at all: the app holds both ids at download time, so thread them through the amp upload the way Task 2 does for IRs and add a field to `AmpMetadata`. The same index mechanism then covers both kinds — keyed on `source.sha256` for amps, on the pedal blob for IRs. Out of scope here.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Captures_every_occupied_slot_and_skips_empty_ones()
{
    var device = new FakePresetDevice();          // existing fixture
    // Arrange: 2 presets, 1 amp, 1 IR occupied; the rest empty. Follow the setup helpers
    // already used in ReorderServiceTests for populating FakePresetDevice.
    var svc = BuildSnapshotService(device);

    using var ms = new MemoryStream();
    var manifest = await svc.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                                          "0.9.7", "2026-07-26T14:02:11Z");

    Assert.Equal(4, manifest.Slots.Count);
    Assert.All(manifest.Slots, s => Assert.Equal(64, s.Sha.Length));

    ms.Position = 0;
    var (readBack, blobs) = SnapshotArchive.Read(ms);
    Assert.Equal(manifest.Slots.Count, blobs.Count);
    Assert.Equal("2026-07-26T14:02:11Z", readBack.CreatedUtc);
}

[Fact]
public async Task Amp_identity_is_null_until_amps_carry_Tone3000_ids()
{
    var svc = BuildSnapshotService(new FakePresetDevice());

    using var ms = new MemoryStream();
    var manifest = await svc.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                                          "0.9.7", "2026-07-26T14:02:11Z");

    // Scope decision, not an absence of data — see this task's notes. Asserted so that whoever
    // populates amp identity later has to come here and change it deliberately.
    Assert.All(manifest.Slots.Where(s => s.Kind == SnapshotSlotKind.Amp), s => Assert.Null(s.T3k));
}

[Fact]
public async Task Ir_identity_comes_from_the_supplied_resolver()
{
    var svc = BuildSnapshotService(new FakePresetDevice());

    using var ms = new MemoryStream();
    var manifest = await svc.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
        "0.9.7", "2026-07-26T14:02:11Z",
        resolveIrIdentity: _ => new SnapshotT3k(2468, 1357));

    var ir = manifest.Slots.First(s => s.Kind == SnapshotSlotKind.Ir);
    Assert.Equal(2468, ir.T3k!.ToneId);
}

[Fact]
public async Task Cancellation_mid_capture_leaves_no_partial_file_claim()
{
    var svc = BuildSnapshotService(new FakePresetDevice());
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    using var ms = new MemoryStream();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        svc.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                         "0.9.7", "2026-07-26T14:02:11Z", ct: cts.Token));
}

[Fact]
public async Task Reports_progress_for_each_slot_read()
{
    var svc = BuildSnapshotService(new FakePresetDevice());
    var seen = new List<SnapshotCaptureProgress>();

    using var ms = new MemoryStream();
    await svc.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"), "0.9.7",
                           "2026-07-26T14:02:11Z",
                           progress: new Progress<SnapshotCaptureProgress>(seen.Add));

    Assert.NotEmpty(seen);
    Assert.Equal(seen.Count, seen.Last().Done);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotServiceTests`
Expected: FAIL — `SnapshotService` does not exist.

- [ ] **Step 3: Implement the capture**

```csharp
using Sonulab.Core.Model;
using Sonulab.Distill;

namespace Sonulab.Core.Services;

public sealed record SnapshotCaptureProgress(string Stage, int Done, int Total);

/// <summary>Reads every occupied slot off the pedal and writes a .namsnap.
///
/// Read-only: this service never writes to the device. Restoring a snapshot back onto hardware is
/// a separate concern with its own consent, verification, and cancellation requirements.</summary>
public sealed class SnapshotService(DeviceRepository presets, AmpService amps, IrService irs)
{
    public async Task<SnapshotManifest> CaptureAsync(
        Stream destination, SnapshotDevice device, string appVersion, string createdUtc,
        Func<byte[], SnapshotT3k?>? resolveIrIdentity = null,
        IProgress<SnapshotCaptureProgress>? progress = null,
        CancellationToken ct = default)
    {
        var slots = new List<SnapshotSlot>();
        var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>();

        var presetList = (await presets.ListPresetsAsync(ct)).Where(p => !p.IsEmpty).ToList();
        var ampList = (await amps.ListAmpsAsync(ct)).Where(a => !string.IsNullOrEmpty(a.Name)).ToList();
        var irList = (await irs.ListIrsAsync(ct)).Where(i => !i.IsEmpty).ToList();
        int total = presetList.Count + ampList.Count + irList.Count, done = 0;

        foreach (var p in presetList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = (await presets.ReadPresetAsync(p.Index, ct)).ToBytes();
            blobs[(SnapshotSlotKind.Preset, p.Index)] = bytes;
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Preset, p.Index, p.Name,
                                       SnapshotArchive.ShaOf(bytes), null));
            progress?.Report(new SnapshotCaptureProgress("Presets", ++done, total));
        }

        foreach (var a in ampList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await amps.ReadAmpAsync(a.Index, ct);
            blobs[(SnapshotSlotKind.Amp, a.Index)] = bytes;
            // T3k is null for amps by scope decision. SSMD does carry identity — a tone id inside
            // the url slug, and source.sha256 for the exact model — but extracting it needs either
            // slug parsing or a source-hash index. See this task's notes.
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Amp, a.Index, a.Name,
                                       SnapshotArchive.ShaOf(bytes), null));
            progress?.Report(new SnapshotCaptureProgress("Amps", ++done, total));
        }

        foreach (var i in irList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await irs.ReadIrAsync(i.Index, ct);
            blobs[(SnapshotSlotKind.Ir, i.Index)] = bytes;
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Ir, i.Index, i.Name,
                                       SnapshotArchive.ShaOf(bytes),
                                       resolveIrIdentity?.Invoke(bytes)));
            progress?.Report(new SnapshotCaptureProgress("IRs", ++done, total));
        }

        var manifest = new SnapshotManifest(
            SnapshotManifest.CurrentSchema, createdUtc, appVersion, device, slots);
        SnapshotArchive.Write(destination, manifest, blobs);
        return manifest;
    }
}
```

Drop the `using Sonulab.Distill;` import — with amp identity out of scope, this service does not read SSMD at all.

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter FullyQualifiedName~SnapshotServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/SnapshotService.cs tests/Sonulab.Core.Tests/SnapshotServiceTests.cs
git commit -m "feat(snapshot): capture presets, amps, and IRs into a .namsnap"
```

---

### Task 7: Export and Import menu commands

**Files:**
- Modify: `src/Namager.App/Views/MainWindow.axaml:20-29` (File menu)
- Modify: `src/Namager.App/Views/MainWindow.axaml.cs` (file pickers)
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs` (commands)
- Test: `tests/Namager.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `SnapshotService` (Task 6), `SnapshotArchive` (Task 5), `IrIndex` (Task 1).
- Produces: `Task ExportSnapshotAsync(string path)`, `Task<SnapshotManifest> ImportSnapshotAsync(string path)` on `MainWindowViewModel`.

Import in this plan **validates and rebuilds the IR index** — it does not write to the pedal. The dialog reports what the file contains and how many IR identities were learned.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Importing_a_snapshot_rebuilds_IR_identities_in_the_index()
{
    var indexPath = Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");
    var snapPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
    try
    {
        WriteSampleSnapshot(snapPath, irToneId: 2468, irModelId: 1357, irBlob: SampleIrBlob());

        var vm = BuildMainWindowVm(irIndexPath: indexPath);
        var manifest = await vm.ImportSnapshotAsync(snapPath);

        Assert.Equal(SnapshotManifest.CurrentSchema, manifest.Schema);
        var entry = IrIndex.Load(indexPath).Lookup(IrIndex.ShaOf(SampleIrBlob()));
        Assert.NotNull(entry);
        Assert.Equal(2468, entry!.ToneId);
    }
    finally { File.Delete(indexPath); File.Delete(snapPath); }
}

[Fact]
public async Task Importing_a_damaged_snapshot_surfaces_a_readable_error()
{
    var snapPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
    try
    {
        File.WriteAllText(snapPath, "not a zip");
        var vm = BuildMainWindowVm();

        await Assert.ThrowsAsync<SnapshotArchiveException>(() => vm.ImportSnapshotAsync(snapPath));
    }
    finally { File.Delete(snapPath); }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~MainWindowViewModelTests`
Expected: FAIL — the methods do not exist.

- [ ] **Step 3: Implement the commands**

```csharp
/// <summary>Writes a .namsnap of the whole pedal. Read-only against the device.</summary>
public async Task ExportSnapshotAsync(string path)
{
    if (Presets is null || Amps is null || Irs is null) return;
    using var op = Status.BeginOperation("Exporting snapshot…");

    var index = IrIndex.Load(_irIndexPath);
    var svc = new SnapshotService(_repository!, _ampService!, _irService!);

    await using var file = File.Create(path);
    await svc.CaptureAsync(
        file,
        new SnapshotDevice("StompStation", _connection.FirmwareVersion ?? "unknown"),
        AppInfo.Version,
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        resolveIrIdentity: blob => index.Lookup(IrIndex.ShaOf(blob)) is { } e
            ? new SnapshotT3k(e.ToneId, e.ModelId)
            : null,
        progress: new Progress<SnapshotCaptureProgress>(p =>
            Status.Update($"{p.Stage} {p.Done}/{p.Total}")));

    Status.Success($"Snapshot written to {Path.GetFileName(path)}");
}

/// <summary>Reads and validates a .namsnap, and learns any IR identities it carries. Does not
/// write to the pedal.</summary>
public async Task<SnapshotManifest> ImportSnapshotAsync(string path)
{
    await using var file = File.OpenRead(path);
    var (manifest, blobs) = SnapshotArchive.Read(file);

    var index = IrIndex.Load(_irIndexPath);
    int learned = 0;
    foreach (var slot in manifest.Slots.Where(s => s.Kind == SnapshotSlotKind.Ir && s.T3k is not null))
    {
        var blob = blobs[(SnapshotSlotKind.Ir, slot.Index)];
        index = index.Record(new IrIndexEntry(IrIndex.ShaOf(blob),
                                              slot.T3k!.ToneId, slot.T3k.ModelId, slot.Name));
        learned++;
    }
    index.Save(_irIndexPath);

    Status.Success($"Read snapshot from {manifest.CreatedUtc} — learned {learned} IR identities");
    return manifest;
}
```

The resolver receives the blob `SnapshotService` has already read, so export costs no extra device round-trips — it hashes what it has and looks it up.

- [ ] **Step 4: Add the menu entries**

In `src/Namager.App/Views/MainWindow.axaml`, inside the existing `_File` menu after `RestorePresetMenuItem`:

```xml
<Separator/>
<MenuItem x:Name="ExportSnapshotMenuItem" Header="_Export Snapshot…"/>
<MenuItem x:Name="ImportSnapshotMenuItem" Header="_Import Snapshot…"/>
```

In `MainWindow.axaml.cs`, wire both to `StorageProvider` pickers using the `.namsnap` extension, following the pattern the existing Backup/Restore items already use.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ tests/Namager.App.Tests/
git commit -m "feat(snapshot): File > Export Snapshot and Import Snapshot"
```

---

### Task 8: Usage-ping opt-out

**Files:**
- Modify: `src/Namager.App/Services/AppSettings.cs:5-11`
- Modify: `src/Namager.App/Services/UsagePingService.cs:58-89`
- Modify: `src/Namager.App/Views/MainWindow.axaml:30-42` (Settings menu)
- Modify: `PRIVACY.md`
- Test: `tests/Namager.App.Tests/UsagePingServiceTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `AppSettingsStore`.
- Produces: `AppSettings.ShareUsageData` (`bool`, default `true`); `UsagePingService` gains a `Func<bool>? isEnabled` constructor parameter defaulting to reading `AppSettingsStore.Load().ShareUsageData`.

Default stays **on** — this adds a way out, it does not change what a silent user sends.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Does_not_send_when_sharing_is_disabled()
{
    var handler = new CountingHandler();
    var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                   appVersion: "1.0.0", statePath: TempStatePath(),
                                   isEnabled: () => false);

    await svc.PingAsync("2.5.1", "usb");

    Assert.Equal(0, handler.Calls);
}

[Fact]
public async Task Sends_when_sharing_is_enabled()
{
    var handler = new CountingHandler();
    var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                   appVersion: "1.0.0", statePath: TempStatePath(),
                                   isEnabled: () => true);

    await svc.PingAsync("2.5.1", "usb");

    Assert.Equal(1, handler.Calls);
}

[Fact]
public async Task A_throwing_settings_read_does_not_break_the_ping_path()
{
    var handler = new CountingHandler();
    var svc = new UsagePingService(handler, endpoint: "https://example.invalid/ping",
                                   appVersion: "1.0.0", statePath: TempStatePath(),
                                   isEnabled: () => throw new InvalidOperationException("boom"));

    await svc.PingAsync("2.5.1", "usb");   // must not throw

    Assert.Equal(0, handler.Calls);        // fail closed: no send when consent is unknown
}

[Fact]
public void ShareUsageData_defaults_to_true()
{
    Assert.True(new AppSettings().ShareUsageData);
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~UsagePingServiceTests`
Expected: FAIL — no `isEnabled` parameter, no `ShareUsageData`.

- [ ] **Step 3: Implement**

In `AppSettings`:

```csharp
/// <summary>Whether the anonymous connect ping is sent. Default true — see PRIVACY.md for
/// exactly what it contains.</summary>
public bool ShareUsageData { get; init; } = true;
```

In `UsagePingService`, add the constructor parameter and the guard. The existing `catch { }` in `PingAsync` already swallows everything, so put the consent check **inside** the try, before `UsageState.Load`:

```csharp
private readonly Func<bool> _isEnabled;

public UsagePingService(HttpMessageHandler? handler = null, string? endpoint = null,
                        string? appVersion = null, string? statePath = null,
                        Func<bool>? isEnabled = null)
{
    // ... existing assignments ...
    _isEnabled = isEnabled ?? (() => AppSettingsStore.Load().ShareUsageData);
}
```

```csharp
public async Task PingAsync(string firmware, string? transport, CancellationToken ct = default)
{
    if (_appVersion.Contains('-')) return;

    try
    {
        // Fails closed: if consent cannot be read, nothing is sent.
        if (!_isEnabled()) return;

        var state = UsageState.Load(_statePath);
        // ... rest unchanged ...
    }
    catch { /* unchanged */ }
}
```

- [ ] **Step 4: Add the Settings toggle**

In `MainWindow.axaml`'s `_Settings` menu, after the Theme submenu:

```xml
<Separator/>
<MenuItem x:Name="ShareUsageMenuItem" Header="Send _anonymous usage ping"
          ToggleType="CheckBox"/>
```

Wire it to load from and save to `AppSettingsStore`, matching how the Theme radio items already persist.

- [ ] **Step 5: Update `PRIVACY.md`**

Replace the paragraph beginning *"There is no opt-out toggle."* with:

```markdown
**Turning it off:** Settings ▸ Send anonymous usage ping. It is on by default. Turning it off
stops the ping immediately and permanently — nothing is queued or sent later. Deleting
`%APPDATA%\Namager\usage.json` additionally resets your install ID.
```

Add a new section documenting snapshots:

```markdown
## 5. Snapshots (.namsnap)

**File ▸ Export Snapshot** writes a copy of your pedal — presets, amps, and IRs, plus their
names — to a file you choose. It stays on your PC. Nothing is uploaded.

NAMager also keeps `%APPDATA%\Namager\ir-index.json`, which remembers which Tone3000 IR a piece
of content came from, so the app can show real names instead of whatever a slot was renamed to.
It is keyed by a hash of the IR's content. **That hash is used only on your PC to look up the
name — it is never sent anywhere.**
```

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namager.App/ tests/Namager.App.Tests/ PRIVACY.md
git commit -m "feat(privacy): opt-out toggle for the anonymous usage ping"
```

---

## Verification

- [ ] `dotnet test` — all projects pass (the suite was 863 tests before this plan).
- [ ] `dotnet build` — no new warnings.
- [ ] Manual: install an IR from Tone3000, confirm `%APPDATA%\Namager\ir-index.json` gains an entry with the right `toneId`/`modelId`.
- [ ] Manual: rename that IR on the pedal, then export a snapshot — the manifest still carries its `t3k` (content-keyed, not name-keyed).
- [ ] Manual: move the IR to a different slot, export again — `t3k` still resolves, now at the new index.
- [ ] Manual: export a snapshot from a real pedal, reopen it with Import, confirm the slot count and that IR identities are learned.
- [ ] Manual: toggle the usage ping off, reconnect the pedal, confirm no request to the worker (check with Fiddler or by blocking DNS and watching for silence).
- [ ] `PRIVACY.md` matches what the code actually sends — read both side by side.
