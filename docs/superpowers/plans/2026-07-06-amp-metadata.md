# Amp Slot Metadata (SSMD Block) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed source-NAM metadata (filename/size/date/sha256, .nam metadata passthrough, distill fidelity, user notes + URL) in the unused 4032-byte padding of amp slots; show it in an Amps-tab details pane; allow editing notes/URL.

**Architecture:** A new `VxampMetadata` codec in `Sonulab.Distill` reads/writes a tagged JSON block at slot offset 8256 (magic `SSMD` + u16 version + u16 length + UTF-8 JSON, max 4024 B). `Distiller` gains a fidelity-returning variant so upload can record distill quality. `AmpListViewModel` stamps the block during upload, reads it on selection (with a per-session cache), and rewrites it for notes/URL edits via the existing guarded `UploadAmpAsync`. The DSP payload bytes [0, 8256) are never modified.

**Tech Stack:** .NET 10, System.Text.Json (`JsonNode`/`JsonObject`), Avalonia 12 built-in FluentTheme, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-06-amp-metadata-design.md` — read it first.

## Global Constraints

- Avalonia 12 + built-in `FluentTheme`; do NOT add FluentAvalonia or any third-party UI/icon lib.
- Device names cap at 31 ASCII chars (existing `SlotBlobService.NameMaxChars`).
- Amp slot = 12288 B; DSP payload = first 8256 B and must pass through **byte-identical** in every code path this plan touches.
- SSMD JSON budget: **4024 bytes max**; trim priority on overflow: drop `nam`, then truncate `notes`.
- Malformed/absent SSMD block ⇒ "no metadata", never an exception, never blocks an operation.
- Metadata capture failures during upload degrade to omitting that field — never block the upload.
- Device writes only via the existing guarded `SlotBlobService.UploadAsync` path; no new wire-protocol code.
- All existing tests must keep passing (`dotnet test` — 276 tests before this plan).
- Run all commands from the repo root `C:\Development\Buckdrivers\Sonulab\StompStationManager`.

---

### Task 1: `AmpMetadata` model + `VxampMetadata` codec

**Files:**
- Create: `src/Sonulab.Distill/VxampMetadata.cs`
- Test: `tests/Sonulab.Distill.Tests/VxampMetadataTests.cs`

**Interfaces:**
- Consumes: `VxampFormat.SlotSize` (=12288), `VxampFormat.HeaderSize` (=32), `VxampFormat.BodySize` (=8224) from `src/Sonulab.Distill/VxampFormat.cs`.
- Produces (later tasks depend on these exact signatures):
  - `record AmpSourceInfo(string? File, long? Size, string? Modified, string? Sha256)`
  - `record AmpDistillInfo(string? Version, double? ShapeErr)`
  - `record AmpMetadata(AmpSourceInfo? Source, string? Uploaded, JsonObject? Nam, AmpDistillInfo? Distill, string? Notes, string? Url, JsonObject? Extra)` — all params optional with `= null` defaults
  - `static AmpMetadata? VxampMetadata.TryRead(ReadOnlySpan<byte> slot)`
  - `static void VxampMetadata.Write(byte[] slot, AmpMetadata meta)` (in-place stamp)
  - `static int VxampMetadata.JsonByteCount(AmpMetadata meta)`
  - `const int VxampMetadata.MaxJsonBytes` (=4024), `const int VxampMetadata.Offset` (=8256)

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Distill.Tests/VxampMetadataTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace Sonulab.Distill.Tests;

public class VxampMetadataTests
{
    private static byte[] Slot()
    {
        // Deterministic non-zero payload so "payload untouched" checks are meaningful.
        var s = new byte[VxampFormat.SlotSize];
        for (int i = 0; i < VxampMetadata.Offset; i++) s[i] = (byte)(i * 31 + 7);
        return s;
    }

    private static AmpMetadata Full() => new(
        Source: new AmpSourceInfo("Bassman 5F6A.nam", 1834024, "2026-05-01T14:22:00Z", "ab34cd"),
        Uploaded: "2026-07-06T09:15:00Z",
        Nam: new JsonObject { ["name"] = "Bassman", ["modeled_by"] = "somebody" },
        Distill: new AmpDistillInfo("1.0.0", 0.043),
        Notes: "warm clean tone",
        Url: "https://tonehunt.org/models/xyz");

    [Fact]
    public void Roundtrip_preserves_all_fields()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.Equal("Bassman 5F6A.nam", m!.Source!.File);
        Assert.Equal(1834024, m.Source.Size);
        Assert.Equal("2026-05-01T14:22:00Z", m.Source.Modified);
        Assert.Equal("ab34cd", m.Source.Sha256);
        Assert.Equal("2026-07-06T09:15:00Z", m.Uploaded);
        Assert.Equal("Bassman", (string?)m.Nam!["name"]);
        Assert.Equal("somebody", (string?)m.Nam["modeled_by"]);
        Assert.Equal("1.0.0", m.Distill!.Version);
        Assert.Equal(0.043, m.Distill.ShapeErr!.Value, 12);
        Assert.Equal("warm clean tone", m.Notes);
        Assert.Equal("https://tonehunt.org/models/xyz", m.Url);
    }

    [Fact]
    public void Write_never_touches_the_payload()
    {
        var slot = Slot();
        var before = slot[..VxampMetadata.Offset].ToArray();
        VxampMetadata.Write(slot, Full());
        Assert.Equal(before, slot[..VxampMetadata.Offset]);
    }

    [Fact]
    public void Write_zeroes_the_region_before_stamping()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());               // long block
        VxampMetadata.Write(slot, new AmpMetadata(Notes: "x"));   // much shorter block
        var m = VxampMetadata.TryRead(slot);
        Assert.Equal("x", m!.Notes);
        Assert.Null(m.Source);                            // no residue of the old block
    }

    [Theory]
    [InlineData(0)]   // all-zero padding (every VoidX-written slot)
    [InlineData(1)]   // bad magic
    [InlineData(2)]   // unsupported version
    [InlineData(3)]   // length overruns the region
    [InlineData(4)]   // invalid JSON
    [InlineData(5)]   // valid JSON but not an object
    public void TryRead_tolerates_garbage(int kind)
    {
        var slot = Slot();
        if (kind >= 1)
        {
            VxampMetadata.Write(slot, Full());
            switch (kind)
            {
                case 1: slot[VxampMetadata.Offset] = (byte)'X'; break;
                case 2: slot[VxampMetadata.Offset + 4] = 99; break;
                case 3:
                    slot[VxampMetadata.Offset + 6] = 0xFF;   // len = 0xFFFF > 4024
                    slot[VxampMetadata.Offset + 7] = 0xFF;
                    break;
                case 4:
                    Encoding.UTF8.GetBytes("{not json!").CopyTo(slot, VxampMetadata.Offset + 8);
                    break;
                case 5:
                    var arr = Encoding.UTF8.GetBytes("[1,2,3]");
                    Array.Clear(slot, VxampMetadata.Offset + 8, 64);
                    arr.CopyTo(slot, VxampMetadata.Offset + 8);
                    slot[VxampMetadata.Offset + 6] = (byte)arr.Length;
                    slot[VxampMetadata.Offset + 7] = 0;
                    break;
            }
        }
        Assert.Null(VxampMetadata.TryRead(slot));
    }

    [Fact]
    public void TryRead_rejects_wrong_slot_size()
    {
        Assert.Null(VxampMetadata.TryRead(new byte[100]));
    }

    [Fact]
    public void Overflow_drops_nam_first()
    {
        var bigNam = new JsonObject { ["blob"] = new string('n', 5000) };
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Nam = bigNam });
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.Null(m!.Nam);                              // dropped
        Assert.Equal("warm clean tone", m.Notes);          // kept intact
    }

    [Fact]
    public void Overflow_then_truncates_notes()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Nam = null, Notes = new string('a', 5000) });
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.NotNull(m!.Notes);
        Assert.True(m.Notes!.Length < 5000);
        Assert.True(VxampMetadata.JsonByteCount(m) <= VxampMetadata.MaxJsonBytes);
        Assert.Equal("https://tonehunt.org/models/xyz", m.Url);   // other fields survive
    }

    [Fact]
    public void Unknown_top_level_fields_are_preserved()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Extra = new JsonObject { ["future"] = 42 } });
        var m = VxampMetadata.TryRead(slot);
        Assert.Equal(42, (int?)m!.Extra!["future"]);
        // Re-writing (the edit path) keeps them too.
        VxampMetadata.Write(slot, m with { Notes = "edited" });
        var m2 = VxampMetadata.TryRead(slot);
        Assert.Equal(42, (int?)m2!.Extra!["future"]);
        Assert.Equal("edited", m2.Notes);
    }

    [Fact]
    public void JsonByteCount_matches_what_write_stores()
    {
        var slot = Slot();
        var meta = Full();
        VxampMetadata.Write(slot, meta);
        int stored = slot[VxampMetadata.Offset + 6] | (slot[VxampMetadata.Offset + 7] << 8);
        Assert.Equal(VxampMetadata.JsonByteCount(meta), stored);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampMetadataTests`
Expected: FAIL — compile errors, `AmpMetadata`/`VxampMetadata` do not exist.

- [ ] **Step 3: Implement the codec**

Create `src/Sonulab.Distill/VxampMetadata.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonulab.Distill;

public sealed record AmpSourceInfo(string? File = null, long? Size = null,
                                   string? Modified = null, string? Sha256 = null);

public sealed record AmpDistillInfo(string? Version = null, double? ShapeErr = null);

/// <summary>Slot metadata carried in the SSMD block. Dates are ISO-8601 UTC strings.
/// Extra holds unknown top-level JSON fields so future writers' data survives our edits.</summary>
public sealed record AmpMetadata(
    AmpSourceInfo? Source = null,
    string? Uploaded = null,
    JsonObject? Nam = null,
    AmpDistillInfo? Distill = null,
    string? Notes = null,
    string? Url = null,
    JsonObject? Extra = null);

/// <summary>Reads/writes the SSMD metadata block in the padding region of a 12288-byte
/// vxamp slot (spec: docs/superpowers/specs/2026-07-06-amp-metadata-design.md).
/// Layout at Offset: "SSMD" | u16 LE version=1 | u16 LE json length | UTF-8 JSON | zero fill.
/// The DSP payload [0, Offset) is never touched. Anything unparseable reads as null.</summary>
public static class VxampMetadata
{
    public const int Offset = VxampFormat.HeaderSize + VxampFormat.BodySize;   // 8256
    public const int RegionSize = VxampFormat.SlotSize - Offset;               // 4032
    public const int BlockHeaderSize = 8;
    public const int MaxJsonBytes = RegionSize - BlockHeaderSize;              // 4024
    public const ushort Version = 1;

    private static ReadOnlySpan<byte> Magic => "SSMD"u8;
    private static readonly string[] KnownKeys = ["source", "uploaded", "nam", "distill", "notes", "url"];

    public static AmpMetadata? TryRead(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != VxampFormat.SlotSize) return null;
        var r = slot.Slice(Offset, RegionSize);
        if (!r[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4, 2)) != Version) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6, 2));
        if (n > MaxJsonBytes) return null;
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(r.Slice(BlockHeaderSize, n)))
                is JsonObject o ? FromJson(o) : null;
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }        // Parse can throw this on bad input
    }

    /// <summary>Stamp the block in place. On budget overflow trims Nam first, then Notes
    /// (spec trim priority). Never modifies bytes below Offset.</summary>
    public static void Write(byte[] slot, AmpMetadata meta)
    {
        if (slot.Length != VxampFormat.SlotSize)
            throw new ArgumentException($"expected {VxampFormat.SlotSize}-byte slot, got {slot.Length}");
        var m = meta;
        var json = SerializeBytes(m);
        if (json.Length > MaxJsonBytes && m.Nam is not null)
        { m = m with { Nam = null }; json = SerializeBytes(m); }
        while (json.Length > MaxJsonBytes && !string.IsNullOrEmpty(m.Notes))
        {
            int cut = Math.Min(m.Notes.Length, Math.Max(1, json.Length - MaxJsonBytes));
            var notes = m.Notes[..^cut];
            if (notes.Length > 0 && char.IsHighSurrogate(notes[^1])) notes = notes[..^1];
            m = m with { Notes = notes.Length == 0 ? null : notes };
            json = SerializeBytes(m);
        }
        if (json.Length > MaxJsonBytes)
            throw new ArgumentException($"metadata is {json.Length} B even after trimming (max {MaxJsonBytes}).");

        Array.Clear(slot, Offset, RegionSize);
        Magic.CopyTo(slot.AsSpan(Offset));
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(Offset + 4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(Offset + 6), (ushort)json.Length);
        json.CopyTo(slot, Offset + BlockHeaderSize);
    }

    /// <summary>Serialized size in bytes of this metadata as-is (no trimming) — the UI's budget meter.</summary>
    public static int JsonByteCount(AmpMetadata meta) => SerializeBytes(meta).Length;

    private static byte[] SerializeBytes(AmpMetadata m) => Encoding.UTF8.GetBytes(ToJson(m).ToJsonString());

    private static JsonObject ToJson(AmpMetadata m)
    {
        var o = new JsonObject();
        if (m.Extra is not null)
            foreach (var kv in m.Extra)
                o[kv.Key] = kv.Value?.DeepClone();
        if (m.Source is { } src)
        {
            var s = new JsonObject();
            if (src.File is not null) s["file"] = src.File;
            if (src.Size is not null) s["size"] = src.Size;
            if (src.Modified is not null) s["modified"] = src.Modified;
            if (src.Sha256 is not null) s["sha256"] = src.Sha256;
            o["source"] = s;
        }
        if (m.Uploaded is not null) o["uploaded"] = m.Uploaded;
        if (m.Nam is not null) o["nam"] = m.Nam.DeepClone();
        if (m.Distill is { } d)
        {
            var j = new JsonObject();
            if (d.Version is not null) j["version"] = d.Version;
            if (d.ShapeErr is not null) j["shapeErr"] = d.ShapeErr;
            o["distill"] = j;
        }
        if (m.Notes is not null) o["notes"] = m.Notes;
        if (m.Url is not null) o["url"] = m.Url;
        return o;
    }

    private static AmpMetadata FromJson(JsonObject o)
    {
        AmpSourceInfo? source = null;
        if (o["source"] is JsonObject s)
            source = new AmpSourceInfo((string?)s["file"], (long?)s["size"],
                                       (string?)s["modified"], (string?)s["sha256"]);
        AmpDistillInfo? distill = null;
        if (o["distill"] is JsonObject d)
            distill = new AmpDistillInfo((string?)d["version"], (double?)d["shapeErr"]);
        JsonObject? extra = null;
        foreach (var kv in o)
        {
            if (KnownKeys.Contains(kv.Key)) continue;
            extra ??= new JsonObject();
            extra[kv.Key] = kv.Value?.DeepClone();
        }
        return new AmpMetadata(source, (string?)o["uploaded"], o["nam"] as JsonObject is { } nam
                ? (JsonObject)nam.DeepClone() : null,
            distill, (string?)o["notes"], (string?)o["url"], extra);
    }
}
```

Note: the `(long?)s["size"]` casts throw `FormatException`/`InvalidOperationException` on wrong JSON types (e.g. `"size": "big"`). Wrap the `FromJson(o)` call site in `TryRead` accordingly — extend its catch list: `catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException) { return null; }` (one catch clause replacing the two shown above).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampMetadataTests`
Expected: PASS (10 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all pass (276 existing + 10 new).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Distill/VxampMetadata.cs tests/Sonulab.Distill.Tests/VxampMetadataTests.cs
git commit -m "feat: SSMD metadata codec for the vxamp padding region"
```

---

### Task 2: Surface distill fidelity from `Distiller`

**Files:**
- Modify: `src/Sonulab.Distill/Distiller.cs`
- Test: `tests/Sonulab.Distill.Tests/DistillerTests.cs`

**Interfaces:**
- Consumes: `Fidelity.FidelityVsNam(INamProcessor model, WhTensors tensors)` (exists, `src/Sonulab.Distill/Fidelity.cs:61`).
- Produces:
  - `enum DistillStage` gains member `Fidelity` between `Normalize` and `Encode`.
  - `record DistillResult(byte[] Blob, double ShapeErr)`
  - `static DistillResult Distiller.DistillWithFidelity(string namPath, IProgress<DistillProgress>? progress = null, CancellationToken ct = default)`
  - `Distiller.DistillAsync` return type changes `Task` → **`Task<double>`** (returns ShapeErr). Existing awaiting callers keep compiling; Task 3 updates the App delegate to consume the value.
  - `static byte[] Distiller.Distill(...)` — unchanged signature and stage sequence (no Fidelity stage; parity tests stay fast).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Sonulab.Distill.Tests/DistillerTests.cs` (inside the class):

```csharp
[Fact]
public void DistillWithFidelity_reports_stage_and_a_sane_shape_err()
{
    var stages = new List<DistillStage>();
    var r = Distiller.DistillWithFidelity(Fixture("synthetic.nam"),
        new SyncProgress(p => stages.Add(p.Stage)));
    Assert.Equal(VxampFormat.SlotSize, r.Blob.Length);
    Assert.InRange(r.ShapeErr, 0.0, 2.0);              // ShapeErr is bounded by construction
    Assert.Equal(new[] { DistillStage.LoadModel, DistillStage.ProbeIr, DistillStage.FitLinear,
                         DistillStage.FitNonlinearity, DistillStage.Normalize,
                         DistillStage.Fidelity, DistillStage.Encode },
                 stages);
}

[Fact]
public async Task DistillAsync_returns_the_shape_err()
{
    var outPath = Path.Combine(Path.GetTempPath(), $"distill-fid-{Guid.NewGuid():N}.vxamp");
    try
    {
        double err = await Distiller.DistillAsync(Fixture("synthetic.nam"), outPath);
        Assert.InRange(err, 0.0, 2.0);
        Assert.Equal(VxampFormat.SlotSize, new FileInfo(outPath).Length);
    }
    finally { File.Delete(outPath); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter DistillerTests`
Expected: FAIL — `DistillWithFidelity` / `DistillStage.Fidelity` do not exist.

- [ ] **Step 3: Implement**

In `src/Sonulab.Distill/Distiller.cs`:

1. Change the enum (line 3):

```csharp
public enum DistillStage { LoadModel, ProbeIr, FitLinear, FitNonlinearity, Normalize, Fidelity, Encode, Done }
```

2. Add the result record after `DistillProgress` (line 5):

```csharp
public sealed record DistillResult(byte[] Blob, double ShapeErr);
```

3. Refactor `Distill` so both entry points share the fitting pipeline. Replace the existing `Distill` method body with a private core plus two fronts (the `catch` mapping stays identical to today's):

```csharp
private static (WhTensors Tensors, NamModel Model) Fit(string namPath,
    IProgress<DistillProgress>? progress, CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    progress?.Report(new(DistillStage.LoadModel, "Loading NAM model…"));
    var model = NamParser.Load(namPath);
    model.SampleRate ??= FirFitter.NamDefaultSampleRate;   // NAM ecosystem default

    ct.ThrowIfCancellationRequested();
    progress?.Report(new(DistillStage.ProbeIr, "Probing small-signal response…"));
    var ir = Dsp.ToDouble(Probe.LinearIrOfModel(model, n: 8192));
    if (model.SampleRate != DeviceSim.SampleRate)
        ir = Resampler.ResamplePoly(ir, DeviceSim.SampleRate, model.SampleRate.Value);

    ct.ThrowIfCancellationRequested();
    progress?.Report(new(DistillStage.FitLinear, "Designing FIR cascade…"));
    var (pre, g2) = FirFitter.DesignLinear(ir);

    ct.ThrowIfCancellationRequested();
    progress?.Report(new(DistillStage.FitNonlinearity, "Fitting nonlinearity…"));
    var (s, gain) = FirFitter.FitNl(model, pre, g2);
    var g2Cal = new float[g2.Length];
    for (int i = 0; i < g2.Length; i++) g2Cal[i] = (float)(g2[i] * gain);
    var tensors = new WhTensors(pre, VxampFormat.G2HeaderFloats(), g2Cal,
                                VxampFormat.NlmixHeaderFloats(), (float)s);

    ct.ThrowIfCancellationRequested();
    progress?.Report(new(DistillStage.Normalize, "Calibrating loudness…"));
    return (LoudnessNormalize(tensors), model);
}

public static byte[] Distill(string namPath, IProgress<DistillProgress>? progress = null,
                             CancellationToken ct = default)
{
    try
    {
        var (tensors, _) = Fit(namPath, progress, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(DistillStage.Encode, "Encoding .vxamp…"));
        return VxampCodec.Encode(tensors);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception e) { throw new DistillException($"Distillation failed: {e.Message}", e); }
}

/// <summary>Distill + measure how faithful the fit is (Fidelity.FidelityVsNam ShapeErr,
/// lower is better). Slower than Distill — runs one extra device-sim pass.</summary>
public static DistillResult DistillWithFidelity(string namPath,
    IProgress<DistillProgress>? progress = null, CancellationToken ct = default)
{
    try
    {
        var (tensors, model) = Fit(namPath, progress, ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(DistillStage.Fidelity, "Measuring fidelity…"));
        double err = Fidelity.FidelityVsNam(model, tensors);
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(DistillStage.Encode, "Encoding .vxamp…"));
        return new DistillResult(VxampCodec.Encode(tensors), err);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception e) { throw new DistillException($"Distillation failed: {e.Message}", e); }
}
```

4. Change `DistillAsync` to return the fidelity:

```csharp
public static Task<double> DistillAsync(string namPath, string outPath,
                                        IProgress<DistillProgress>? progress = null,
                                        CancellationToken ct = default) =>
    Task.Run(() =>
    {
        var r = DistillWithFidelity(namPath, progress, ct);
        try { File.WriteAllBytes(outPath, r.Blob); }
        catch (Exception e) { throw new DistillException($"Failed to write '{outPath}': {e.Message}", e); }
        progress?.Report(new(DistillStage.Done, "Done."));
        return r.ShapeErr;
    }, ct);
```

`Task<double>` derives from `Task`, so the App's current `DistillRunner` delegate assignment keeps compiling until Task 3 updates it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests`
Expected: PASS. Note `DistillAsync_writes_the_file_and_reports_done` now passes through the `Fidelity` stage — it only asserts the last stage is `Done`, so no change needed. `Distill_produces_a_valid_slot_with_stages_in_order` asserts the old sequence WITHOUT `Fidelity` — it must still pass because plain `Distill` skips the fidelity stage.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all pass (App compiles unchanged — covariant delegate assignment).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Distill/Distiller.cs tests/Sonulab.Distill.Tests/DistillerTests.cs
git commit -m "feat: DistillWithFidelity + DistillAsync returns ShapeErr"
```

---

### Task 3: Stamp metadata during upload (VM + upload panel UI)

**Files:**
- Modify: `src/Sonulab.App/ViewModels/AmpListViewModel.cs`
- Modify: `src/Sonulab.App/Views/AmpListView.axaml` (upload panel `StackPanel`, lines 38–69)
- Test: `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`

**Interfaces:**
- Consumes: `VxampMetadata.TryRead/Write/JsonByteCount/MaxJsonBytes`, `AmpMetadata`, `AmpSourceInfo`, `AmpDistillInfo` (Task 1); `Distiller.DistillAsync : Task<double>` (Task 2).
- Produces (Tasks 4–5 and the view rely on):
  - `delegate Task<double> AmpListViewModel.DistillRunner(string namPath, string outPath, IProgress<DistillProgress>? progress, CancellationToken ct)` (return type changed)
  - Observable props `UploadNotes`, `UploadUrl` (string), computed `NotesBudgetWarning` (string?)
  - `private static string NowIso()`, `private static AmpMetadata? TryReadNamMetadataFile(string namPath)` helpers

- [ ] **Step 1: Write the failing tests**

In `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`:

First fix the harness for the new delegate return type — in `MakeUpload` (line 94) change the default runner's last line from `return Task.CompletedTask;` to `return Task.FromResult(0.25);`, and the record/lambda types from `AmpListViewModel.DistillRunner` stay as-is (signature now returns `Task<double>`). Any other inline runner lambdas in this file get the same `Task.FromResult(0.25)` treatment (search the file for `Task.CompletedTask` inside `DistillRunner` lambdas).

Then add these tests (file-scope `using Sonulab.Distill;` and `using System.Security.Cryptography;` / `using System.Text;` as needed):

```csharp
[Fact]
public async Task Upload_nam_stamps_ssmd_metadata()
{
    var h = MakeUpload();                              // fake distiller returns ShapeErr 0.25
    await h.Vm.RefreshCommand.ExecuteAsync(null);
    var nam = Path.Combine(Path.GetTempPath(), $"Tweed Deluxe-{Guid.NewGuid():N}.nam");
    File.WriteAllText(nam, """{"architecture":"WaveNet","metadata":{"name":"Tweed Deluxe","modeled_by":"ed"}}""");
    try
    {
        h.Vm.BeginUploadCommand.Execute(nam);
        h.Vm.UploadName = "Tweed";
        h.Vm.UploadNotes = "bright, edge of breakup";
        h.Vm.UploadUrl = "https://tonehunt.org/x";
        await h.Vm.StartUploadCommand.ExecuteAsync(null);

        Assert.Null(h.Vm.UploadError);
        int slot = h.Vm.Items.First(i => i.Name == "Tweed").Index;
        var meta = VxampMetadata.TryRead(h.Dev.SlotBlobs[slot]!);
        Assert.NotNull(meta);
        Assert.Equal(Path.GetFileName(nam), meta!.Source!.File);
        Assert.Equal(new FileInfo(nam).Length, meta.Source.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nam))), meta.Source.Sha256);
        Assert.NotNull(meta.Uploaded);
        Assert.Equal("ed", (string?)meta.Nam!["modeled_by"]);
        Assert.Equal(0.25, meta.Distill!.ShapeErr!.Value, 12);
        Assert.NotNull(meta.Distill.Version);
        Assert.Equal("bright, edge of breakup", meta.Notes);
        Assert.Equal("https://tonehunt.org/x", meta.Url);
    }
    finally { File.Delete(nam); }
}

[Fact]
public async Task Upload_nam_persists_stamped_bytes_to_the_distilled_file()
{
    var h = MakeUpload();
    await h.Vm.RefreshCommand.ExecuteAsync(null);
    var nam = TempFile($"Persist-{Guid.NewGuid():N}.nam");
    try
    {
        h.Vm.BeginUploadCommand.Execute(nam);
        h.Vm.UploadName = "Persist";
        h.Vm.UploadNotes = "note";
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        var onDisk = File.ReadAllBytes(Path.Combine(h.DistilledDir, "Persist.vxamp"));
        Assert.Equal("note", VxampMetadata.TryRead(onDisk)!.Notes);
    }
    finally { File.Delete(nam); }
}

[Fact]
public async Task Upload_vxamp_preserves_existing_block_and_overlays_user_fields()
{
    var h = MakeUpload();
    await h.Vm.RefreshCommand.ExecuteAsync(null);
    var existing = new byte[12288];
    VxampMetadata.Write(existing, new AmpMetadata(
        Nam: new System.Text.Json.Nodes.JsonObject { ["name"] = "orig" },
        Notes: "old notes", Url: "https://old"));
    var vx = Path.Combine(Path.GetTempPath(), $"pre-{Guid.NewGuid():N}.vxamp");
    File.WriteAllBytes(vx, existing);
    try
    {
        h.Vm.BeginUploadCommand.Execute(vx);
        Assert.Equal("old notes", h.Vm.UploadNotes);       // prefilled from the block
        Assert.Equal("https://old", h.Vm.UploadUrl);
        h.Vm.UploadName = "Pre";
        h.Vm.UploadUrl = "https://new";                    // user overwrites one field
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        int slot = h.Vm.Items.First(i => i.Name == "Pre").Index;
        var meta = VxampMetadata.TryRead(h.Dev.SlotBlobs[slot]!)!;
        Assert.Equal("orig", (string?)meta.Nam!["name"]);  // passthrough kept
        Assert.Equal("old notes", meta.Notes);
        Assert.Equal("https://new", meta.Url);
        Assert.Equal(Path.GetFileName(vx), meta.Source!.File);
        Assert.NotNull(meta.Uploaded);
    }
    finally { File.Delete(vx); }
}

[Fact]
public async Task Upload_payload_bytes_reach_the_device_unchanged()
{
    var h = MakeUpload();                              // fake distiller writes 0xD1 * 12288
    await h.Vm.RefreshCommand.ExecuteAsync(null);
    var nam = TempFile($"Payload-{Guid.NewGuid():N}.nam");
    try
    {
        h.Vm.BeginUploadCommand.Execute(nam);
        h.Vm.UploadName = "Payload";
        h.Vm.UploadNotes = "anything";
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        int slot = h.Vm.Items.First(i => i.Name == "Payload").Index;
        Assert.All(h.Dev.SlotBlobs[slot]![..VxampMetadata.Offset], b => Assert.Equal(0xD1, b));
    }
    finally { File.Delete(nam); }
}

[Fact]
public void NotesBudgetWarning_appears_when_metadata_would_truncate()
{
    var h = MakeUpload();
    h.Vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    var nam = TempFile($"Budget-{Guid.NewGuid():N}.nam");
    try
    {
        h.Vm.BeginUploadCommand.Execute(nam);
        Assert.Null(h.Vm.NotesBudgetWarning);
        h.Vm.UploadNotes = new string('a', 4500);
        Assert.NotNull(h.Vm.NotesBudgetWarning);
    }
    finally { File.Delete(nam); }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter AmpListViewModelTests`
Expected: FAIL — `UploadNotes`/`UploadUrl`/`NotesBudgetWarning` do not exist; delegate return type mismatch compile errors.

- [ ] **Step 3: Implement in `AmpListViewModel`**

Add usings at the top of `src/Sonulab.App/ViewModels/AmpListViewModel.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sonulab.Distill;
```

1. Change the delegate (line 15):

```csharp
/// <summary>Distillation seam — Sonulab.Distill.Distiller.DistillAsync in the app,
/// a fake in tests. Returns the fidelity ShapeErr (lower is better).</summary>
public delegate Task<double> DistillRunner(string namPath, string outPath,
    IProgress<Sonulab.Distill.DistillProgress>? progress, CancellationToken ct);
```

2. Add upload-metadata state next to the other upload props (after line 95):

```csharp
[ObservableProperty] private string _uploadNotes = "";
[ObservableProperty] private string _uploadUrl = "";
private AmpSourceInfo? _pendingSource;                  // captured at BeginUpload
private JsonObject? _pendingNam;                        // .nam metadata passthrough
private AmpMetadata? _pendingExisting;                  // pre-existing block of a picked .vxamp

partial void OnUploadNotesChanged(string value) => OnPropertyChanged(nameof(NotesBudgetWarning));
partial void OnUploadUrlChanged(string value) => OnPropertyChanged(nameof(NotesBudgetWarning));

/// <summary>Live budget check: the SSMD JSON cap is 4024 B; warn (not block) when the
/// notes would be truncated. Uses a fixed-width ShapeErr placeholder pre-distillation.</summary>
public string? NotesBudgetWarning
{
    get
    {
        int total = VxampMetadata.JsonByteCount(BuildUploadMetadata(0.1234567890123456));
        int over = total - VxampMetadata.MaxJsonBytes;
        return over > 0 ? $"Metadata is {over} B over budget — notes will be truncated on upload." : null;
    }
}

private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
private static string? NullIfEmpty(string s) => s.Trim().Length == 0 ? null : s.Trim();

private static string? DistillerVersion() =>
    typeof(Sonulab.Distill.Distiller).Assembly.GetName().Version?.ToString(3);

/// <summary>Read the top-level "metadata" object of a .nam. Failures degrade to null —
/// metadata capture must never block an upload (spec §5).</summary>
private static JsonObject? TryReadNamMetadataFile(string namPath)
{
    try
    {
        return JsonNode.Parse(File.ReadAllText(namPath))?["metadata"] is JsonObject o
            ? (JsonObject)o.DeepClone() : null;
    }
    catch { return null; }
}

private static AmpSourceInfo? TryCaptureSource(string path)
{
    try
    {
        var fi = new FileInfo(path);
        return new AmpSourceInfo(fi.Name, fi.Length,
            fi.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
    catch { return new AmpSourceInfo(Path.GetFileName(path)); }
}

/// <summary>Merge captured + user-entered metadata for the pending upload. For a .vxamp
/// with an existing block, its fields are kept and only user-entered fields overwrite.</summary>
private AmpMetadata BuildUploadMetadata(double? shapeErr)
{
    bool isNam = Path.GetExtension(_uploadSourcePath).Equals(".nam", StringComparison.OrdinalIgnoreCase);
    var baseline = _pendingExisting ?? new AmpMetadata();
    return baseline with
    {
        Source = _pendingSource,
        Uploaded = NowIso(),
        Nam = isNam ? _pendingNam : baseline.Nam,
        Distill = isNam ? new AmpDistillInfo(DistillerVersion(), shapeErr) : baseline.Distill,
        Notes = NullIfEmpty(UploadNotes) ?? baseline.Notes,
        Url = NullIfEmpty(UploadUrl) ?? baseline.Url,
    };
}
```

3. In `BeginUpload` (line 99), after `UploadSourceFileName = Path.GetFileName(path);` add the capture + prefill:

```csharp
_pendingSource = TryCaptureSource(path);
_pendingNam = null; _pendingExisting = null;
UploadNotes = ""; UploadUrl = "";
if (Path.GetExtension(path).Equals(".nam", StringComparison.OrdinalIgnoreCase))
    _pendingNam = TryReadNamMetadataFile(path);
else
{
    try
    {
        _pendingExisting = VxampMetadata.TryRead(File.ReadAllBytes(path));
        UploadNotes = _pendingExisting?.Notes ?? "";
        UploadUrl = _pendingExisting?.Url ?? "";
    }
    catch { /* unreadable file will fail loudly at StartUpload; metadata never blocks */ }
}
```

4. In `StartUploadAsync`, capture the fidelity and stamp before the device write. Change the distill call (line 143) to:

```csharp
double? shapeErr = null;
// inside the .nam branch:
shapeErr = await _distill(_uploadSourcePath, vxampPath, distillProgress, _uploadCts.Token);
```

(declare `double? shapeErr = null;` just before the `if (.nam)` branch so it is in scope below), then after `var bytes = await File.ReadAllBytesAsync(vxampPath);` (line 148) insert:

```csharp
try
{
    VxampMetadata.Write(bytes, BuildUploadMetadata(shapeErr));
    await File.WriteAllBytesAsync(vxampPath, bytes);   // distilled/local copy carries the block too
}
catch { /* spec §5: metadata failure must never block the upload */ }
```

Note: for a direct `.vxamp` upload, `vxampPath` is the USER'S file — overwriting it with the stamped bytes is the intended behavior only for OUR distilled output. Guard the persist:

```csharp
try
{
    VxampMetadata.Write(bytes, BuildUploadMetadata(shapeErr));
    if (!vxampPath.Equals(_uploadSourcePath, StringComparison.OrdinalIgnoreCase))
        await File.WriteAllBytesAsync(vxampPath, bytes);   // only rewrite our own distilled copy
}
catch { /* spec §5: metadata failure must never block the upload */ }
```

(use this second form — never modify a user-picked source file.)

- [ ] **Step 4: Add the upload-panel fields to `AmpListView.axaml`**

Inside the upload panel `StackPanel` (line 38), after the existing name/slot `Grid` (line 55), insert:

```xml
<Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto" >
  <TextBlock Grid.Row="0" Grid.Column="0" Text="Link" VerticalAlignment="Center" Margin="0,0,8,0"/>
  <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding UploadUrl}" Watermark="https:// where the tone came from (optional)"
           IsEnabled="{Binding !IsUploading}" Margin="0,0,0,4"/>
  <TextBlock Grid.Row="1" Grid.Column="0" Text="Notes" VerticalAlignment="Top" Margin="0,4,8,0"/>
  <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding UploadNotes}" Watermark="Notes about this tone (optional)"
           AcceptsReturn="True" TextWrapping="Wrap" MaxHeight="72"
           IsEnabled="{Binding !IsUploading}"/>
</Grid>
<TextBlock Text="{Binding NotesBudgetWarning}" FontSize="11" Foreground="#D9820F" TextWrapping="Wrap"
           IsVisible="{Binding NotesBudgetWarning, Converter={x:Static ObjectConverters.IsNotNull}}"/>
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests`
Expected: PASS (new tests + all pre-existing upload tests with the updated harness).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.App/ViewModels/AmpListViewModel.cs src/Sonulab.App/Views/AmpListView.axaml tests/Sonulab.App.Tests/AmpListViewModelTests.cs
git commit -m "feat: stamp SSMD metadata during amp upload (notes + URL fields)"
```

---

### Task 4: Details pane — on-demand read with session cache

**Files:**
- Modify: `src/Sonulab.App/ViewModels/AmpListViewModel.cs`
- Modify: `src/Sonulab.App/Views/AmpListView.axaml`
- Test: `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`

**Interfaces:**
- Consumes: `AmpService.ReadAmpAsync(int index, CancellationToken)` (exists), `VxampMetadata.TryRead`, cache-count assertions via `FakeSlotBlobDevice.CommandLog`.
- Produces (Task 5 relies on):
  - `record MetadataField(string Label, string Value)` (new file-local type in the VM file, `namespace Sonulab.App.ViewModels`)
  - Observable props: `IsDetailsVisible`, `IsDetailsLoading`, `ShowNoMetadata` (bool), `DetailsNotes`, `DetailsUrl`, `DetailsError` (string?); `ObservableCollection<MetadataField> DetailsFields`
  - `public Task? DetailsLoadTask { get; private set; }` — test seam awaited after setting `Selected`
  - `private readonly Dictionary<int, (string Name, byte[] Slot, AmpMetadata? Meta)> _detailsCache` — Task 5 reads slot bytes from it
  - `private Task LoadDetailsCoreAsync(AmpItemViewModel? item)` — bypasses the busy guard (called internally after upload/save)

- [ ] **Step 1: Write the failing tests**

Add to `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`. Helper first (near `Make`):

```csharp
/// <summary>Seed a slot whose blob carries an SSMD block.</summary>
private static byte[] BlobWithMeta(AmpMetadata meta, byte fill = 3)
{
    var blob = Enumerable.Repeat(fill, 12288).ToArray();
    VxampMetadata.Write(blob, meta);
    return blob;
}

private static int DreadCount(FakeAmpDevice dev, int index) =>
    dev.CommandLog.Count(c => c.StartsWith($"dread root\\amp:{{\"index\":{index},"));
```

Tests:

```csharp
[Fact]
public async Task Selecting_an_amp_loads_its_metadata()
{
    var dev = new FakeAmpDevice();
    dev.SeedAmp(0, "Clean", BlobWithMeta(new AmpMetadata(
        Source: new AmpSourceInfo("Clean.nam", 1000, "2026-01-01T00:00:00Z", "aa"),
        Notes: "hi", Url: "https://x")));
    dev.OpenAsync().GetAwaiter().GetResult();
    var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
    await vm.RefreshCommand.ExecuteAsync(null);

    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;

    Assert.True(vm.IsDetailsVisible);
    Assert.False(vm.ShowNoMetadata);
    Assert.Equal("hi", vm.DetailsNotes);
    Assert.Equal("https://x", vm.DetailsUrl);
    Assert.Contains(vm.DetailsFields, f => f.Label == "Source file" && f.Value == "Clean.nam");
}

[Fact]
public async Task Slot_without_block_shows_no_metadata_state()
{
    var (vm, _) = Make();                               // seeded blobs have no SSMD block
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    Assert.True(vm.IsDetailsVisible);
    Assert.True(vm.ShowNoMetadata);
    Assert.Empty(vm.DetailsFields);
}

[Fact]
public async Task Selecting_an_empty_slot_hides_the_pane()
{
    var (vm, _) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[5];                          // empty
    if (vm.DetailsLoadTask is not null) await vm.DetailsLoadTask;
    Assert.False(vm.IsDetailsVisible);
}

[Fact]
public async Task Reselecting_hits_the_cache_not_the_device()
{
    var (vm, dev) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    Assert.Equal(96, DreadCount(dev, 0));               // one full read
    vm.Selected = vm.Items[1];
    await vm.DetailsLoadTask!;
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    Assert.Equal(96, DreadCount(dev, 0));               // still one — cache hit
}

[Fact]
public async Task Rename_invalidates_the_details_cache()
{
    var (vm, dev) = Make();
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    Assert.Equal(96, DreadCount(dev, 0));

    var item = vm.Items[0];
    item.BeginRenameCommand.Execute(null);
    item.EditName = "Cleaner";
    await vm.CommitRenameCommand.ExecuteAsync(item);    // RunAsync -> ReloadAsync clears cache

    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    Assert.Equal(192, DreadCount(dev, 0));              // re-read after invalidation
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter AmpListViewModelTests`
Expected: FAIL — `DetailsLoadTask`, `IsDetailsVisible` etc. do not exist.

- [ ] **Step 3: Implement the details read path in `AmpListViewModel`**

Add at the bottom of the file (outside the class, same namespace):

```csharp
/// <summary>One label/value row of the amp details pane.</summary>
public sealed record MetadataField(string Label, string Value);
```

Add inside the class:

```csharp
// ---- details pane (selected amp metadata) ----
[ObservableProperty] private bool _isDetailsVisible;
[ObservableProperty] private bool _isDetailsLoading;
[ObservableProperty] private bool _showNoMetadata;
[ObservableProperty] private string? _detailsNotes;
[ObservableProperty] private string? _detailsUrl;
[ObservableProperty] private string? _detailsError;
public ObservableCollection<MetadataField> DetailsFields { get; } = new();

/// <summary>Last details load — test seam: set Selected, then await this.</summary>
public Task? DetailsLoadTask { get; private set; }

private readonly Dictionary<int, (string Name, byte[] Slot, AmpMetadata? Meta)> _detailsCache = new();
private CancellationTokenSource? _detailsCts;

partial void OnSelectedChanged(AmpItemViewModel? value)
{
    // Never issue a read while another device operation may be in flight — serial
    // commands must not interleave. The pane just stays hidden; explicit callers
    // (post-upload, post-save) use LoadDetailsCoreAsync directly once idle.
    if (IsBusy || IsUploading) { IsDetailsVisible = false; return; }
    DetailsLoadTask = LoadDetailsCoreAsync(value);
}

private async Task LoadDetailsCoreAsync(AmpItemViewModel? item)
{
    _detailsCts?.Cancel();
    DetailsFields.Clear();
    DetailsNotes = null; DetailsUrl = null; DetailsError = null; ShowNoMetadata = false;
    if (item is null || item.IsEmpty) { IsDetailsVisible = false; return; }
    IsDetailsVisible = true;

    if (!_detailsCache.TryGetValue(item.Index, out var entry) || entry.Name != item.Name)
    {
        var cts = new CancellationTokenSource();
        _detailsCts = cts;
        IsDetailsLoading = true;
        try
        {
            var slot = await _amps.ReadAmpAsync(item.Index, cts.Token);
            entry = (item.Name, slot, VxampMetadata.TryRead(slot));
            _detailsCache[item.Index] = entry;
        }
        catch (OperationCanceledException) { return; }   // superseded by a newer selection
        catch (AmpServiceException ex) { DetailsError = ex.Message; return; }
        finally { if (_detailsCts == cts) IsDetailsLoading = false; }
        if (cts.IsCancellationRequested || Selected != item) return;
    }
    PopulateDetails(entry.Meta);
}

private void PopulateDetails(AmpMetadata? meta)
{
    DetailsFields.Clear();
    if (meta is null) { ShowNoMetadata = true; return; }
    ShowNoMetadata = false;
    if (meta.Source?.File is { } f) DetailsFields.Add(new("Source file", f));
    if (meta.Source?.Size is { } sz) DetailsFields.Add(new("Source size", FormatSize(sz)));
    if (meta.Source?.Modified is { } mo) DetailsFields.Add(new("Source date", mo));
    if (meta.Uploaded is { } up) DetailsFields.Add(new("Uploaded", up));
    if (meta.Nam is { } nam)
        foreach (var kv in nam)
            if (kv.Value is System.Text.Json.Nodes.JsonValue v)
                DetailsFields.Add(new($"NAM {kv.Key}", v.ToString()));
    if (meta.Distill?.Version is { } dv) DetailsFields.Add(new("Distilled by", $"v{dv}"));
    if (meta.Distill?.ShapeErr is { } se) DetailsFields.Add(new("Fit error", $"{se:F3} (lower is better)"));
    DetailsNotes = meta.Notes;
    DetailsUrl = meta.Url;
}

private static string FormatSize(long b) =>
    b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : b >= 1 << 10 ? $"{b / 1024.0:F1} KB" : $"{b} B";

[RelayCommand]
private void OpenDetailsUrl()
{
    if (DetailsUrl is { Length: > 0 } url &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
```

Invalidate the cache on every reload — add as the first line of `ReloadAsync()` (line 59):

```csharp
_detailsCache.Clear();
```

Refresh selected details after an upload — in `StartUploadAsync`, the existing line `Selected = Items.FirstOrDefault(i => i.Index == slot);` runs while `IsUploading` is still true, so `OnSelectedChanged` skips the read. Immediately after that line add:

```csharp
DetailsLoadTask = LoadDetailsCoreAsync(Selected);
await DetailsLoadTask;
```

(this is safe: `UploadAmpAsync` has fully completed; no other command is in flight.)

- [ ] **Step 4: Add the details pane to `AmpListView.axaml`**

After the busy-indicator `StackPanel` (line 76) and before the `ListBox`, insert:

```xml
<!-- Details pane (selected amp metadata) -->
<Border DockPanel.Dock="Bottom" Margin="8,4" Padding="10" CornerRadius="4"
        BorderThickness="1" BorderBrush="{DynamicResource SystemControlForegroundBaseLowBrush}"
        IsVisible="{Binding IsDetailsVisible}">
  <StackPanel Spacing="4">
    <TextBlock Text="Reading amp metadata…" FontSize="11" Opacity="0.7"
               IsVisible="{Binding IsDetailsLoading}"/>
    <TextBlock Text="No metadata — uploaded outside StompStation Manager." FontSize="11" Opacity="0.7"
               IsVisible="{Binding ShowNoMetadata}"/>
    <TextBlock Text="{Binding DetailsError}" FontSize="11" Foreground="#D9534F" TextWrapping="Wrap"
               IsVisible="{Binding DetailsError, Converter={x:Static ObjectConverters.IsNotNull}}"/>
    <ItemsControl ItemsSource="{Binding DetailsFields}">
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:MetadataField">
          <Grid ColumnDefinitions="130,*">
            <TextBlock Grid.Column="0" Text="{Binding Label}" FontSize="11" Opacity="0.6"/>
            <TextBlock Grid.Column="1" Text="{Binding Value}" FontSize="11" TextWrapping="Wrap"/>
          </Grid>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Text="{Binding DetailsNotes}" FontSize="11" TextWrapping="Wrap"
               IsVisible="{Binding DetailsNotes, Converter={x:Static ObjectConverters.IsNotNull}}"/>
    <Button Content="{Binding DetailsUrl}" Command="{Binding OpenDetailsUrlCommand}"
            Padding="0" Background="Transparent" Foreground="#3B82D6" FontSize="11" Cursor="Hand"
            IsVisible="{Binding DetailsUrl, Converter={x:Static ObjectConverters.IsNotNull}}"/>
  </StackPanel>
</Border>
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.App/ViewModels/AmpListViewModel.cs src/Sonulab.App/Views/AmpListView.axaml tests/Sonulab.App.Tests/AmpListViewModelTests.cs
git commit -m "feat: amp details pane reads SSMD metadata on demand with session cache"
```

---

### Task 5: Edit notes/URL on the pedal

**Files:**
- Modify: `src/Sonulab.App/ViewModels/AmpListViewModel.cs`
- Modify: `src/Sonulab.App/Views/AmpListView.axaml` (inside the Task-4 details pane)
- Test: `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`

**Interfaces:**
- Consumes: `_detailsCache` slot bytes + meta (Task 4), `VxampMetadata.Write`, `AmpService.UploadAmpAsync`, the `RunAsync` busy/error helper.
- Produces: observable props `IsEditingMetadata`, `EditNotes`, `EditUrl`; commands `BeginEditMetadata`, `CancelEditMetadata`, `SaveMetadataAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Edit_metadata_rewrites_padding_only_and_preserves_other_fields()
{
    var dev = new FakeAmpDevice();
    var original = BlobWithMeta(new AmpMetadata(
        Nam: new System.Text.Json.Nodes.JsonObject { ["name"] = "keepme" },
        Uploaded: "2026-01-01T00:00:00Z", Notes: "old", Url: "https://old"), fill: 9);
    dev.SeedAmp(0, "Clean", original);
    dev.OpenAsync().GetAwaiter().GetResult();
    var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
    await vm.RefreshCommand.ExecuteAsync(null);

    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    vm.BeginEditMetadataCommand.Execute(null);
    Assert.Equal("old", vm.EditNotes);                  // prefilled
    vm.EditNotes = "new notes";
    vm.EditUrl = "https://new";
    await vm.SaveMetadataCommand.ExecuteAsync(null);

    Assert.Null(vm.ErrorMessage);
    var blob = dev.SlotBlobs[0]!;
    Assert.Equal(original[..VxampMetadata.Offset], blob[..VxampMetadata.Offset]);   // DSP untouched
    var meta = VxampMetadata.TryRead(blob)!;
    Assert.Equal("new notes", meta.Notes);
    Assert.Equal("https://new", meta.Url);
    Assert.Equal("keepme", (string?)meta.Nam!["name"]); // preserved
    Assert.Equal("2026-01-01T00:00:00Z", meta.Uploaded); // NOT re-stamped on edit
    Assert.False(vm.IsEditingMetadata);
    Assert.Equal("new notes", vm.DetailsNotes);          // pane refreshed
}

[Fact]
public async Task Edit_creates_a_block_on_a_voidx_era_slot()
{
    var (vm, dev) = Make();                              // no SSMD blocks in seeds
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    vm.BeginEditMetadataCommand.Execute(null);
    vm.EditNotes = "annotated later";
    await vm.SaveMetadataCommand.ExecuteAsync(null);
    Assert.Equal("annotated later", VxampMetadata.TryRead(dev.SlotBlobs[0]!)!.Notes);
    // payload preserved:
    Assert.All(dev.SlotBlobs[0]![..VxampMetadata.Offset], b => Assert.Equal(1, b));
}

[Fact]
public async Task Edit_is_gated_when_writes_not_allowed()
{
    var (vm, dev) = Make(writes: false);
    await vm.RefreshCommand.ExecuteAsync(null);
    vm.Selected = vm.Items[0];
    await vm.DetailsLoadTask!;
    vm.BeginEditMetadataCommand.Execute(null);
    vm.EditNotes = "x";
    await vm.SaveMetadataCommand.ExecuteAsync(null);
    Assert.Null(VxampMetadata.TryRead(dev.SlotBlobs[0]!));   // device untouched
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter AmpListViewModelTests`
Expected: FAIL — edit commands/props do not exist.

- [ ] **Step 3: Implement in `AmpListViewModel`**

```csharp
// ---- metadata editing (notes/url only; auto-captured fields are read-only) ----
[ObservableProperty] private bool _isEditingMetadata;
[ObservableProperty] private string _editNotes = "";
[ObservableProperty] private string _editUrl = "";

[RelayCommand]
private void BeginEditMetadata()
{
    if (!CanMutate || Selected is not { IsEmpty: false } s) return;
    if (!_detailsCache.ContainsKey(s.Index)) return;    // details not loaded yet
    EditNotes = DetailsNotes ?? "";
    EditUrl = DetailsUrl ?? "";
    IsEditingMetadata = true;
}

[RelayCommand] private void CancelEditMetadata() => IsEditingMetadata = false;

/// <summary>Rewrites only the SSMD region of the cached slot bytes, then re-flashes the
/// slot through the guarded upload path (~3 s: backup -> acked chunks -> verify).</summary>
[RelayCommand]
private async Task SaveMetadataAsync()
{
    if (Selected is not { IsEmpty: false } s) return;
    if (!_detailsCache.TryGetValue(s.Index, out var entry)) return;
    int index = s.Index;
    var name = s.Name;
    var bytes = (byte[])entry.Slot.Clone();
    var meta = (entry.Meta ?? new AmpMetadata()) with
    {
        Notes = NullIfEmpty(EditNotes),
        Url = NullIfEmpty(EditUrl),
    };
    VxampMetadata.Write(bytes, meta);
    if (await RunAsync($"Saving metadata for '{name}'…",
            () => _amps.UploadAmpAsync(index, bytes, name)))
    {
        IsEditingMetadata = false;
        Selected = Items.FirstOrDefault(i => i.Index == index);
        DetailsLoadTask = LoadDetailsCoreAsync(Selected);
        await DetailsLoadTask;
    }
}
```

Note: `RunAsync` already gates on `_writes` (returns false untouched) and calls `ReloadAsync` (which clears `_detailsCache` and resets `Items`) — that is why `Selected` is re-established and details reloaded explicitly after a successful save. `OnSelectedChanged` fires during `IsBusy == false` at that point but `LoadDetailsCoreAsync` is idempotent; the explicit call guarantees the awaitable seam for tests.

Also handle the gated case leaving edit mode consistent — in the `writes: false` test the command returns early from `RunAsync`; `IsEditingMetadata` simply stays true, which is acceptable (the Save button is disabled in the UI via `CanMutate`; the programmatic call is a no-op on the device, which is what the test asserts).

- [ ] **Step 4: Add edit UI to the details pane in `AmpListView.axaml`**

Inside the details `StackPanel` from Task 4, after the URL `Button`, add:

```xml
<StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding !IsEditingMetadata}">
  <Button Content="Edit notes/link" Command="{Binding BeginEditMetadataCommand}"
          IsEnabled="{Binding CanMutate}" FontSize="11"/>
</StackPanel>
<StackPanel Spacing="4" IsVisible="{Binding IsEditingMetadata}">
  <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto">
    <TextBlock Grid.Row="0" Grid.Column="0" Text="Link" VerticalAlignment="Center" Margin="0,0,8,0" FontSize="11"/>
    <TextBox Grid.Row="0" Grid.Column="1" Text="{Binding EditUrl}" Margin="0,0,0,4"/>
    <TextBlock Grid.Row="1" Grid.Column="0" Text="Notes" VerticalAlignment="Top" Margin="0,4,8,0" FontSize="11"/>
    <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding EditNotes}" AcceptsReturn="True"
             TextWrapping="Wrap" MaxHeight="72"/>
  </Grid>
  <StackPanel Orientation="Horizontal" Spacing="8">
    <Button Content="Save to pedal" Command="{Binding SaveMetadataCommand}" IsEnabled="{Binding CanMutate}"
            ToolTip.Tip="Re-flashes the slot (~3 s): backup, write, verify — the amp sound is untouched"/>
    <Button Content="Cancel" Command="{Binding CancelEditMetadataCommand}"/>
  </StackPanel>
</StackPanel>
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.App/ViewModels/AmpListViewModel.cs src/Sonulab.App/Views/AmpListView.axaml tests/Sonulab.App.Tests/AmpListViewModelTests.cs
git commit -m "feat: edit amp metadata notes/URL and re-flash via guarded upload"
```

---

### Task 6: Hardware validation checklist + docs

**Files:**
- Create: `docs/HARDWARE-VALIDATION-amp-metadata.md`
- Modify: `CLAUDE.md` (architecture bullet + "Not done" note)
- Modify: `docs/vxamp-format.md` (padding row of the layout table)

**Interfaces:** none (documentation only).

- [ ] **Step 1: Write the hardware validation doc**

Create `docs/HARDWARE-VALIDATION-amp-metadata.md`:

```markdown
# Hardware validation — amp slot SSMD metadata

**Feature:** SSMD metadata block in the amp-slot padding region (spec:
`docs/superpowers/specs/2026-07-06-amp-metadata-design.md`).

**Open assumption to settle:** firmware ignores slot bytes [8256, 12288) rather than
validating/checksumming them. The corpus only ever contained zeros there.

Prereqs: VoidX-Control CLOSED; pedal on COM (auto-discovered); at least one EMPTY amp slot.

## Checklist

- [ ] 1. `dotnet run --project src/Sonulab.App` — connect to the pedal.
- [ ] 2. Amps tab → Upload .nam… → pick any known-good `.nam` from `NAMFiles/`.
       Fill Notes ("hw validation test") and Link (any URL). Upload to an empty slot.
       Expect: upload completes, read-back verify passes (it compares all 12288 B,
       so the SSMD block already survived one flash round-trip).
- [ ] 3. Select the new amp in the list. Expect: details pane shows source file, size,
       date, sha256-backed fields, fit error, notes, and the link.
- [ ] 4. **Power-cycle the pedal** (unplug USB + power, replug). Reconnect the app.
- [ ] 5. On the PEDAL, select the test amp in a preset and PLAY through it.
       Expect: loads normally, sounds like the source model, no glitching/reboot.
- [ ] 6. In the app, select the test amp again. Expect: metadata intact after power cycle.
- [ ] 7. Edit notes ("edited after power cycle") → Save to pedal. Expect: ~3 s guarded
       write succeeds; details pane shows the new note; amp still plays fine.
- [ ] 8. Rename the test amp on the pedal (F2 in the app). Re-select. Expect: metadata
       survives a rename (rename is a chunk:-1 name write; blob untouched).
- [ ] 9. Delete the test amp slot. Confirm `docs/backups/` got the pre-delete backup and
       `VxampMetadata.TryRead` on that backup file returns the block (spot-check via
       a REPL or by re-uploading the backup and viewing details).
- [ ] 10. Record results here (date, firmware version, pass/fail per step).

**If step 5 fails** (amp won't load / device misbehaves): the firmware does inspect the
padding. Delete the slot immediately, mark the feature blocked, and fall back to the
local-sidecar alternative from the spec's rejected-alternatives section.
```

- [ ] **Step 2: Update `docs/vxamp-format.md`**

In the section-1 table, change the Padding row's Contents cell from `Zero bytes (slot fill to 12288 B)` to:

```
Zero bytes in VoidX-written slots. StompStation Manager stores its SSMD metadata block here (see src/Sonulab.Distill/VxampMetadata.cs); firmware ignores the region (hardware-validated — docs/HARDWARE-VALIDATION-amp-metadata.md).
```

(Only append the "hardware-validated" claim AFTER the checklist above has actually passed; until then write "validation pending".)

- [ ] **Step 3: Update `CLAUDE.md`**

- In the Architecture bullet for `src/Sonulab.Distill`, append `VxampMetadata (SSMD slot-metadata block)` to the parenthetical list.
- In "Not done", add: `Amp metadata hardware validation (docs/HARDWARE-VALIDATION-amp-metadata.md) pending — run before relying on SSMD blocks on-device. IR-slot metadata not designed.`

- [ ] **Step 4: Run the full suite one last time**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add docs/HARDWARE-VALIDATION-amp-metadata.md docs/vxamp-format.md CLAUDE.md
git commit -m "docs: amp-metadata hardware validation checklist + format/CLAUDE notes"
```

---

## Self-review notes

- Spec coverage: format (Task 1), fidelity capture (Task 2), write path incl. `.vxamp` preserve/overlay + distilled-file persistence (Task 3), read path + cache + no-metadata state + clickable URL (Task 4), edit path (Task 5), hardware gate + docs (Task 6). Out-of-scope items from the spec are not implemented anywhere.
- The DSP-payload-untouched constraint is asserted by tests in Tasks 1, 3, and 5.
- Type consistency: `AmpMetadata`/`VxampMetadata` signatures in Task 1's "Produces" are used verbatim in Tasks 3–5; `DistillRunner` returns `Task<double>` from Task 3 onward, matching Task 2's `DistillAsync`.
```
