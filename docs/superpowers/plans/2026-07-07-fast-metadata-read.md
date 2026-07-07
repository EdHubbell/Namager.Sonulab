# Fast Amp-Metadata Retrieval (Region-Only Read) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the Amps-tab details load from ~5 s (96-chunk full-slot read) to ~0.4 s typical / ~50 ms for no-metadata amps by reading only the chunks the SSMD block spans.

**Architecture:** A generic validated chunk-range read in Core (`SonuClient.DReadChunkRangeAsync` → `SlotBlobService.ReadChunkRangeAsync` → `AmpService.ReadChunksAsync`; Core stays SSMD-ignorant), region-parsing entry points in the codec (`VxampMetadata.TryReadRegion`/`BlockLength`/chunk-geometry constants), and a two-step fetch in `AmpListViewModel` (read chunk 65, learn the block length, read only the rest). The save path's full fresh read + integrity guards are untouched.

**Tech Stack:** .NET 10, existing serial protocol (dread), xUnit with the existing `FakeAmpDevice`/`DreadCount` test seams.

**Spec:** `docs/superpowers/specs/2026-07-07-fast-metadata-read-design.md` — read it first.

## Global Constraints

- **Core (`Sonulab.Core`) must not reference `Sonulab.Distill` or contain SSMD offsets** — it gains only a generic chunk-range read.
- Every chunk read is validated (a dropped/torn chunk throws via the service's `_raise`, never returns a short/shifted buffer). The torn-odd-length-hex → treat-as-missing rule from `DReadBlobAsync` applies identically.
- `SaveMetadataAsync`'s full fresh read and integrity guards are NOT modified. Uploads/backups/delete/verify untouched.
- All SSMD layout math lives in `VxampMetadata` (no magic 65/64/128 in the ViewModel — use the new named constants).
- `TryRead`'s existing behavior must be bit-identical after the delegation refactor (the whole existing `VxampMetadataTests` suite passes unmodified).
- Suite currently 321/321; every task ends green. The only pre-existing tests whose ASSERTIONS may change are the three dread-count tests named in Task 3 (96→1, 192→2) — that count change IS the feature.
- Run all commands from repo root `C:\Development\Buckdrivers\Sonulab\StompStationManager`.
- COMMIT HYGIENE: stage ONLY the files each task names, by exact path. NEVER `git add -A`, `git add .`, or `git commit -a` — the working tree has unrelated untracked files.

---

### Task 1: Core chunk-range read (`SonuClient` + `SlotBlobService` + `AmpService`)

**Files:**
- Modify: `src/Sonulab.Core/SonuClient.cs` (the `DReadBlobAsync` method, ~line 85)
- Modify: `src/Sonulab.Core/Services/SlotBlobService.cs` (add method near `ReadAsync`, ~line 52)
- Modify: `src/Sonulab.Core/Services/AmpService.cs` (add front near `ReadAmpAsync`, ~line 32)
- Test: `tests/Sonulab.Core.Tests/SlotBlobReadValidationTests.cs`

**Interfaces:**
- Consumes: `ResponseParser.ChunkHex(raw, index, chunk)`, `SonuCommands.DRead`, the service's `_raise`/`_kind` (all existing).
- Produces (Task 3 relies on):
  - `SonuClient.DReadChunkRangeAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default) : Task<byte[]>` (permissive: missing chunks shorten the result, like `DReadBlobAsync`)
  - `SlotBlobService.ReadChunkRangeAsync(int index, int firstChunk, int count, CancellationToken ct = default) : Task<byte[]>` (STRICT: throws unless exactly `count*128` bytes)
  - `AmpService.ReadChunksAsync(int index, int firstChunk, int count, CancellationToken ct = default) : Task<byte[]>`

- [ ] **Step 1: Write the failing tests**

Append inside the `SlotBlobReadValidationTests` class in `tests/Sonulab.Core.Tests/SlotBlobReadValidationTests.cs`:

```csharp
    // ---- generic chunk-range read (region-only metadata fetch, spec 2026-07-07) ----

    /// <summary>Blob whose every byte encodes its own offset (mod 251, a prime, so chunk
    /// boundaries never alias) — any shift/drop inside a range read is detectable.</summary>
    private static byte[] PatternBlob() =>
        Enumerable.Range(0, 12288).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task ReadChunks_returns_exactly_the_requested_range()
    {
        var dev = new DropChunkAmpDevice { Dropping = false };
        dev.SeedAmp(0, "Clean", PatternBlob());
        await dev.OpenAsync();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);

        var buf = await svc.ReadChunksAsync(0, firstChunk: 65, count: 3);   // bytes 8192..8575

        Assert.Equal(3 * 128, buf.Length);
        Assert.Equal(PatternBlob()[8192..8576], buf);
    }

    [Fact]
    public async Task ReadChunks_throws_on_a_dropped_chunk_inside_the_range()
    {
        var dev = new DropChunkAmpDevice { DropChunk = 66 };
        dev.SeedAmp(0, "Clean", PatternBlob());
        await dev.OpenAsync();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);

        var ex = await Assert.ThrowsAsync<AmpServiceException>(() => svc.ReadChunksAsync(0, 65, 3));
        Assert.Contains("384", ex.Message);                 // expected 3*128
        Assert.Contains("256", ex.Message);                 // got 2*128
    }

    [Theory]
    [InlineData(-1, 1, 1)]    // bad index
    [InlineData(0, 0, 1)]     // chunk numbers are 1-based
    [InlineData(0, 1, 0)]     // count must be >= 1
    [InlineData(0, 96, 2)]    // range overruns the 96-chunk slot
    public async Task ReadChunks_validates_its_arguments(int index, int first, int count)
    {
        var dev = new DropChunkAmpDevice { Dropping = false };
        dev.SeedAmp(0, "Clean", PatternBlob());
        await dev.OpenAsync();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.ReadChunksAsync(index, first, count));
    }
```

Note: `DropChunkAmpDevice` already exists in this file with a `Dropping` toggle and `DropChunk` property — reuse it, do not redefine it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBlobReadValidationTests`
Expected: FAIL — compile error, `ReadChunksAsync` does not exist.

- [ ] **Step 3: Implement**

1. In `src/Sonulab.Core/SonuClient.cs`, replace the body of `DReadBlobAsync` and add the range method (DRY — the blob read becomes the full-range case):

```csharp
    public Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default) =>
        DReadChunkRangeAsync(path, index, 1, chunkCount, ct);

    /// <summary>Dread chunks [firstChunk .. firstChunk+count-1] (1-based). PERMISSIVE like
    /// DReadBlobAsync: a missing/torn chunk contributes 0 bytes, shortening the result —
    /// callers that need integrity use SlotBlobService's validated wrappers.</summary>
    public async Task<byte[]> DReadChunkRangeAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)
    {
        var bytes = new List<byte>(count * 128);
        for (int c = firstChunk; c < firstChunk + count; c++)
        {
            var raw = await SendAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, index, c) ?? "";
            // A torn record can carry an odd-length hex value; Convert.FromHexString would
            // throw FormatException past every caller. Treat it as a missing chunk instead —
            // the resulting short buffer fails loudly at the validated-read layer.
            if ((hex.Length & 1) == 1) hex = "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
```

(The old `DReadBlobAsync` body — the `for (int c = 1; c <= chunkCount; ...)` loop including the torn-hex comment — is deleted; it now lives once in `DReadChunkRangeAsync`.)

2. In `src/Sonulab.Core/Services/SlotBlobService.cs`, add directly below `ReadValidatedAsync`:

```csharp
    /// <summary>Dread chunks [firstChunk .. firstChunk+count-1] (1-based) and return exactly
    /// count*128 bytes, or FAIL LOUDLY (same discipline as ReadValidatedAsync). Generic —
    /// this service knows nothing about what lives at any offset.</summary>
    public async Task<byte[]> ReadChunkRangeAsync(int index, int firstChunk, int count, CancellationToken ct = default)
    {
        if (index is < 0 or >= SlotCount)
            throw _raise($"Slot must be 0..{SlotCount - 1}, got {index}.");
        if (firstChunk < 1 || count < 1 || firstChunk + count - 1 > _kind.Chunks)
            throw _raise($"Chunk range {firstChunk}..{firstChunk + count - 1} is outside 1..{_kind.Chunks}.");
        var buf = await _client.DReadChunkRangeAsync(_kind.ListPath, index, firstChunk, count, ct);
        if (buf.Length != count * 128)
            throw _raise($"{_kind.Noun} slot {index} chunks {firstChunk}..{firstChunk + count - 1} returned {buf.Length} B (expected {count * 128}) — a chunk was dropped or garbled on the serial link. Try again.");
        return buf;
    }
```

3. In `src/Sonulab.Core/Services/AmpService.cs`, add directly below `ReadAmpAsync`:

```csharp
    /// <summary>Validated read of an arbitrary 1-based chunk range (128 B per chunk).</summary>
    public Task<byte[]> ReadChunksAsync(int index, int firstChunk, int count, CancellationToken ct = default) =>
        _inner.ReadChunkRangeAsync(index, firstChunk, count, ct);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SlotBlobReadValidationTests`
Expected: PASS (existing validation tests — including the torn-hex one, which now exercises the shared range method via `DReadBlobAsync` — plus the 3 new ones with 6 total cases).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: 321 + 6 new pass, zero failures (the `DReadBlobAsync` delegation must be behavior-identical).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Core/SonuClient.cs src/Sonulab.Core/Services/SlotBlobService.cs src/Sonulab.Core/Services/AmpService.cs tests/Sonulab.Core.Tests/SlotBlobReadValidationTests.cs
git commit -m "feat: validated chunk-range read (SonuClient/SlotBlobService/AmpService)"
```

---

### Task 2: Codec region parsing (`VxampMetadata.TryReadRegion` / `BlockLength` / geometry)

**Files:**
- Modify: `src/Sonulab.Distill/VxampMetadata.cs` (constants block ~line 30; `TryRead` ~line 40)
- Test: `tests/Sonulab.Distill.Tests/VxampMetadataTests.cs`

**Interfaces:**
- Consumes: existing `Magic`, `Version`, `MaxJsonBytes`, `BlockHeaderSize`, `StrictUtf8`, `FromJson`, `VxampFormat.SlotSize`.
- Produces (Task 3 relies on):
  - `const int VxampMetadata.ProtocolChunkSize` (=128), `const int FirstRegionChunk` (=65), `const int OffsetInFirstChunk` (=64)
  - `static int? BlockLength(ReadOnlySpan<byte> regionStart)` — `8 + len`, or null when no valid block start
  - `static int LastRegionChunk(int blockLength)` — 1-based chunk holding the block's final byte
  - `static AmpMetadata? TryReadRegion(ReadOnlySpan<byte> region)` — parse from a buffer starting at slot offset 8256
  - `TryRead` unchanged in behavior (now delegates)

- [ ] **Step 1: Write the failing tests**

Append inside the `VxampMetadataTests` class in `tests/Sonulab.Distill.Tests/VxampMetadataTests.cs`:

```csharp
    // ---- region-parsing entry points (fast partial reads, spec 2026-07-07) ----

    [Fact]
    public void TryReadRegion_roundtrips_a_written_block()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());
        var region = slot[VxampMetadata.Offset..];
        var m = VxampMetadata.TryReadRegion(region);
        Assert.NotNull(m);
        Assert.Equal("Bassman 5F6A.nam", m!.Source!.File);
        Assert.Equal("warm clean tone", m.Notes);
    }

    [Fact]
    public void TryReadRegion_needs_only_the_block_bytes_not_the_whole_region()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());
        int blockLen = VxampMetadata.BlockLength(slot.AsSpan(VxampMetadata.Offset))!.Value;
        var exact = slot[VxampMetadata.Offset..(VxampMetadata.Offset + blockLen)];
        Assert.NotNull(VxampMetadata.TryReadRegion(exact));          // exactly the block: parses
        var short1 = slot[VxampMetadata.Offset..(VxampMetadata.Offset + blockLen - 1)];
        Assert.Null(VxampMetadata.TryReadRegion(short1));            // one byte short: null, no throw
    }

    [Theory]
    [InlineData(0)]   // all zeros (no block)
    [InlineData(1)]   // bad magic
    [InlineData(2)]   // bad version
    [InlineData(3)]   // len > MaxJsonBytes
    [InlineData(4)]   // buffer shorter than the 8-byte header
    public void BlockLength_rejects_invalid_starts(int kind)
    {
        var region = new byte[VxampMetadata.RegionSize];
        if (kind >= 1)
        {
            var slot = Slot();
            VxampMetadata.Write(slot, Full());
            slot.AsSpan(VxampMetadata.Offset).CopyTo(region);
            switch (kind)
            {
                case 1: region[0] = (byte)'X'; break;
                case 2: region[4] = 99; break;
                case 3: region[6] = 0xFF; region[7] = 0xFF; break;
            }
        }
        var span = kind == 4 ? region.AsSpan(0, 7) : region.AsSpan();
        Assert.Null(VxampMetadata.BlockLength(span));
    }

    [Fact]
    public void BlockLength_returns_header_plus_json_length()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());
        int storedLen = slot[VxampMetadata.Offset + 6] | (slot[VxampMetadata.Offset + 7] << 8);
        Assert.Equal(VxampMetadata.BlockHeaderSize + storedLen,
                     VxampMetadata.BlockLength(slot.AsSpan(VxampMetadata.Offset)));
    }

    [Fact]
    public void Chunk_geometry_constants_match_the_slot_layout()
    {
        Assert.Equal(65, VxampMetadata.FirstRegionChunk);            // offset 8256 sits in chunk 65...
        Assert.Equal(64, VxampMetadata.OffsetInFirstChunk);          // ...64 bytes in
        // Block fully inside chunk 65 (<= 64 bytes) ends there:
        Assert.Equal(65, VxampMetadata.LastRegionChunk(64));
        // One byte more spills into chunk 66:
        Assert.Equal(66, VxampMetadata.LastRegionChunk(65));
        // A block ending exactly at a chunk edge: 64 + 128 bytes ends at byte 8447 = chunk 66's last:
        Assert.Equal(66, VxampMetadata.LastRegionChunk(64 + 128));
        // Maximum block (8 + 4024 = 4032) ends at the slot's final byte, chunk 96:
        Assert.Equal(96, VxampMetadata.LastRegionChunk(VxampMetadata.RegionSize));
    }

    [Fact]
    public void Header_only_block_reports_length_8_but_parses_null()
    {
        var region = new byte[VxampMetadata.RegionSize];
        "SSMD"u8.ToArray().CopyTo(region, 0);
        region[4] = 1;                                               // version 1, len 0
        Assert.Equal(8, VxampMetadata.BlockLength(region));
        Assert.Null(VxampMetadata.TryReadRegion(region));            // empty JSON is not a block
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampMetadataTests`
Expected: FAIL — compile errors, new members don't exist.

- [ ] **Step 3: Implement**

In `src/Sonulab.Distill/VxampMetadata.cs`, add to the constants block (after `Version`, line ~34):

```csharp
    /// <summary>The device protocol's dread/dwrite chunk size, mirrored here purely for
    /// region geometry (which 1-based chunk holds which region byte).</summary>
    public const int ProtocolChunkSize = 128;
    /// <summary>1-based chunk containing the region start (Offset 8256 → chunk 65)...</summary>
    public const int FirstRegionChunk = Offset / ProtocolChunkSize + 1;
    /// <summary>...and where the region starts inside that chunk (byte 64).</summary>
    public const int OffsetInFirstChunk = Offset % ProtocolChunkSize;
```

Replace the existing `TryRead` method (lines ~40–54) with:

```csharp
    public static AmpMetadata? TryRead(ReadOnlySpan<byte> slot) =>
        slot.Length == VxampFormat.SlotSize ? TryReadRegion(slot.Slice(Offset, RegionSize)) : null;

    /// <summary>Parse an SSMD block from a buffer that STARTS at slot offset 8256 (the
    /// padding region). Accepts any length; bytes beyond 8+len are ignored (zeros or
    /// unread). Same null-on-anything-malformed contract as TryRead — never throws.</summary>
    public static AmpMetadata? TryReadRegion(ReadOnlySpan<byte> region)
    {
        if (BlockLength(region) is not { } blockLen || blockLen > region.Length) return null;
        try
        {
            return JsonNode.Parse(StrictUtf8.GetString(region.Slice(BlockHeaderSize, blockLen - BlockHeaderSize)))
                is JsonObject o ? FromJson(o) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Total block byte count (header + JSON = 8 + len) read from the first 8 bytes
    /// of a region buffer, or null when there is no valid block start (short buffer, bad
    /// magic, bad version, or len > MaxJsonBytes). Needs only the first 8 bytes.</summary>
    public static int? BlockLength(ReadOnlySpan<byte> regionStart)
    {
        if (regionStart.Length < BlockHeaderSize) return null;
        if (!regionStart[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(regionStart.Slice(4, 2)) != Version) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(regionStart.Slice(6, 2));
        return n > MaxJsonBytes ? null : BlockHeaderSize + n;
    }

    /// <summary>1-based chunk holding the final byte of a block of the given total length
    /// (as returned by BlockLength).</summary>
    public static int LastRegionChunk(int blockLength) =>
        (Offset + blockLength - 1) / ProtocolChunkSize + 1;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Distill.Tests --filter VxampMetadataTests`
Expected: PASS — all 16 pre-existing tests unchanged (delegation is invisible) + the 6 new ones (10 cases).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Distill/VxampMetadata.cs tests/Sonulab.Distill.Tests/VxampMetadataTests.cs
git commit -m "feat: SSMD region parsing + chunk geometry (TryReadRegion/BlockLength)"
```

---

### Task 3: ViewModel two-step fetch + dread-count tests

**Files:**
- Modify: `src/Sonulab.App/ViewModels/AmpListViewModel.cs` (cache declaration ~line 328; `LoadDetailsCoreAsync` cache-miss branch ~line 358)
- Test: `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`

**Interfaces:**
- Consumes: `AmpService.ReadChunksAsync(index, firstChunk, count, ct)` (Task 1); `VxampMetadata.FirstRegionChunk/OffsetInFirstChunk/RegionSize/BlockLength/LastRegionChunk/TryReadRegion` (Task 2); existing `DreadCount` test helper.
- Produces: nothing new for later tasks (this is the last code task).

- [ ] **Step 1: Update the three dread-count assertions and add the failing count tests**

In `tests/Sonulab.App.Tests/AmpListViewModelTests.cs`:

1. `Reselecting_hits_the_cache_not_the_device`: both `Assert.Equal(96, DreadCount(dev, 0))` become `Assert.Equal(1, DreadCount(dev, 0))` and update the comments (`// one region probe (no block: single chunk)` / `// still one — cache hit`). The `Make()` seeds are `RealisticBlob` (zero padding, no SSMD block) → one probe chunk suffices.
2. `Rename_invalidates_the_details_cache`: `96` → `1`, `192` → `2`.
3. `Refresh_invalidates_the_details_cache`: `96` → `1`, `192` → `2`.

Then append inside the class:

```csharp
    // ---- region-only read: prove the dread counts (spec 2026-07-07) ----

    [Fact]
    public async Task Details_read_of_an_amp_with_metadata_fetches_only_the_block_chunks()
    {
        var dev = new FakeAmpDevice();
        var blob = BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("a.nam", 1000, "2026-01-01T00:00:00Z", "aa"),
            Notes: "hello"));
        dev.SeedAmp(0, "A", blob);
        await dev.OpenAsync();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;

        Assert.Equal("hello", vm.DetailsNotes);              // metadata fully loaded...
        int blockLen = VxampMetadata.BlockLength(blob.AsSpan(VxampMetadata.Offset))!.Value;
        int expected = 1 + (VxampMetadata.LastRegionChunk(blockLen) - VxampMetadata.FirstRegionChunk);
        Assert.Equal(expected, DreadCount(dev, 0));          // ...from exactly the block's chunks
        Assert.True(expected < 8, $"small block should span few chunks, got {expected}");
    }

    [Fact]
    public async Task Details_read_of_a_no_metadata_amp_is_a_single_chunk()
    {
        var (vm, dev) = Make();                              // RealisticBlob seeds: no SSMD block
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.True(vm.ShowNoMetadata);
        Assert.Equal(1, DreadCount(dev, 0));
    }
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test tests/Sonulab.App.Tests --filter AmpListViewModelTests`
Expected: FAIL — the two new tests (and the three updated ones) see 96 dreads; the implementation still full-reads.

- [ ] **Step 3: Implement the two-step fetch**

In `src/Sonulab.App/ViewModels/AmpListViewModel.cs`:

1. Slim the cache declaration (~line 328) — the slot bytes are dead weight since the save path re-reads the device:

```csharp
    private readonly Dictionary<int, (string Name, AmpMetadata? Meta)> _detailsCache = new();
```

2. In `LoadDetailsCoreAsync`, replace the two lines of the cache-miss read

```csharp
                var slot = await _amps.ReadAmpAsync(item.Index, cts.Token);
                entry = (item.Name, slot, VxampMetadata.TryRead(slot));
```

with

```csharp
                entry = (item.Name, await ReadMetadataAsync(item.Index, cts.Token));
```

3. Add the helper directly below `LoadDetailsCoreAsync`:

```csharp
    /// <summary>Region-only metadata fetch: one chunk to find the SSMD block and its length,
    /// then exactly the chunks it spans (~0.4 s typical vs ~5 s full-slot). Display-only —
    /// SaveMetadataAsync still does a FULL fresh read with integrity guards before flashing.</summary>
    private async Task<AmpMetadata?> ReadMetadataAsync(int index, CancellationToken ct)
    {
        var head = await _amps.ReadChunksAsync(index, VxampMetadata.FirstRegionChunk, 1, ct);
        var regionStart = head.AsSpan(VxampMetadata.OffsetInFirstChunk);
        if (VxampMetadata.BlockLength(regionStart) is not { } blockLen) return null;

        var region = new byte[VxampMetadata.RegionSize];
        regionStart.CopyTo(region);
        int lastChunk = VxampMetadata.LastRegionChunk(blockLen);
        if (lastChunk > VxampMetadata.FirstRegionChunk)
        {
            var rest = await _amps.ReadChunksAsync(index, VxampMetadata.FirstRegionChunk + 1,
                                                   lastChunk - VxampMetadata.FirstRegionChunk, ct);
            rest.CopyTo(region, regionStart.Length);
        }
        return VxampMetadata.TryReadRegion(region);
    }
```

No other changes: the pane clear, CTS, supersession checks, `AmpServiceException` → `DetailsError`, `BeginEditMetadata`'s `ContainsKey` gate, `EditBudgetWarning`'s `entry.Meta`, and `SaveMetadataAsync` all compile and behave unchanged against the slimmed tuple.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.App.Tests`
Expected: PASS — including the slot-26 incident tests unchanged: `Save_merges_against_fresh_device_read_not_a_poisoned_cache` (its glitch on the FIRST chunk-65 dread now poisons the region probe → `BlockLength` null → `ShowNoMetadata` precondition still holds; the save's full read is clean) and `Save_aborts_when_the_fresh_read_itself_looks_corrupt` (details probe consumes occurrence 1 of chunk 65; the save's full read hits occurrence 2 → guard aborts, as designed).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.App/ViewModels/AmpListViewModel.cs tests/Sonulab.App.Tests/AmpListViewModelTests.cs
git commit -m "feat: region-only metadata fetch in the details pane (~5s -> ~0.4s)"
```

---

## Post-merge note (not a task)

One-time on-device spot check (informal, per spec §5): select an amp with metadata and one without; confirm ~sub-second and ~instant loads respectively; record timings in the ledger. If a glitch error ("dropped or garbled on the serial link") ever shows in the pane, re-selecting retries — same recovery story as before.

## Self-review notes

- Spec coverage: §1 → Task 1 (incl. permissive-client/strict-service split and the `DReadBlobAsync` DRY delegation); §2 → Task 2 (all four members + geometry constants + boundary tests incl. chunk-edge and len-0); §3 → Task 3 (two-step fetch, slimmed cache tuple, count assertions updated 96→1/192→2 exactly as the spec's testing section names); §4 invariants enforced by Global Constraints (save path untouched — Task 3 explicitly changes only the cache-miss read); §5 testing mapped per layer; on-device check captured as the post-merge note.
- Type consistency: `ReadChunksAsync(int,int,int,CancellationToken)` identical across Tasks 1 and 3; `BlockLength` returns `int?` consumed via `is not { } blockLen`; tuple `(string Name, AmpMetadata? Meta)` matches every `entry.Meta` consumer named in Task 3.
- Geometry verified: `8256/128+1 = 65`, `8256%128 = 64`, `LastRegionChunk(4032) = (8256+4031)/128+1 = 95+1 = 96`.
