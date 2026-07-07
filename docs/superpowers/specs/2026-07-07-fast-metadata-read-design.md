# Fast amp-metadata retrieval (region-only read) — design

**Date:** 2026-07-07
**Status:** Approved approach; spec for review.

## Problem

Selecting an amp reads the full 12288-byte slot (96 sequential dreads, ~52 ms each ≈ 5 s) just
to display the SSMD metadata block — typically ~900 bytes at a known offset. Since the
slot-26 fix, the save path re-reads the device itself, so nothing consumes the cached
full-slot bytes: the details pane only ever needs the metadata region.

## Approach (decided)

Read only the chunks the SSMD block spans: one chunk to find the block and its length, then
exactly the chunks that hold it. Typical block ≈ 8 chunks ≈ 0.4 s; no-metadata amp = 1 chunk
≈ 50 ms; worst case (4024-byte block) = 32 chunks ≈ 1.7 s. Hardware-verified feasible:
arbitrary single-chunk dreads work (probe run 2026-07-06).

Rejected alternatives: a persistent disk cache (instant repeats but reintroduces staleness
machinery a day after a stale-cache incident; can be layered on later if 0.4 s still feels
slow) and serial-timing tuning (~20–30% at best; already squeezed by the perf pass).

## 1. Core: generic validated chunk-range read

`SlotBlobService` gains:

```csharp
/// <summary>Dread chunks [firstChunk .. firstChunk+count-1] (1-based) and return count*128
/// bytes. Every chunk is validated to be exactly 128 B — a dropped/torn chunk throws
/// (same loud-failure discipline as ReadValidatedAsync), never a short/shifted buffer.</summary>
public async Task<byte[]> ReadChunkRangeAsync(int index, int firstChunk, int count, CancellationToken ct = default)
```

- Validates `index` in 0..29, `firstChunk >= 1`, `count >= 1`, `firstChunk+count-1 <= Kind.Chunks`.
- Implementation loops `SonuClient.SendAsync(SonuCommands.DRead(...))` + index-checked
  `ResponseParser.ChunkHex(raw, index, c)` per chunk (mirrors `DReadBlobAsync`, including the
  odd-length torn-hex → treated-as-missing rule), and throws via `_raise` naming the chunk
  that came back missing/short.
- Exposed as `AmpService.ReadChunksAsync(int index, int firstChunk, int count, CancellationToken ct = default)`
  (thin front, like `ReadAmpAsync`).
- **Layering rule:** Core stays SSMD-ignorant. No `Sonulab.Distill` reference, no metadata
  offsets — it reads chunk ranges, period. (IR slots get the API for free; unused for now.)

`DReadBlobAsync` in `SonuClient` keeps its signature and behavior; as built it delegates to
`DReadChunkRangeAsync(path, index, 1, chunkCount)` (DRY — verified behavior-identical in review).

## 2. Codec: region parsing + block length

`VxampMetadata` (Sonulab.Distill) gains two members; all SSMD layout knowledge stays here:

```csharp
/// <summary>Parse an SSMD block from a buffer that STARTS at slot offset 8256 (the padding
/// region). Accepts any length >= 8; bytes beyond 8+len are ignored (may be zeros or
/// unread). Same null-on-anything-malformed contract as TryRead.</summary>
public static AmpMetadata? TryReadRegion(ReadOnlySpan<byte> region)

/// <summary>Total block byte count (header + JSON = 8 + len) read from the first 8 bytes of
/// a region buffer, or null when there is no valid block start (bad magic, bad version, or
/// len > MaxJsonBytes). Needs only the first 8 bytes.</summary>
public static int? BlockLength(ReadOnlySpan<byte> regionStart)
```

- `TryRead(slot)` becomes: validate `slot.Length == SlotSize`, then
  `TryReadRegion(slot.Slice(Offset, RegionSize))` — one parser, no duplication.
- `TryReadRegion` returns null if `region.Length < BlockHeaderSize` or
  `8 + len > region.Length` (caller passed too little data) — never throws on any input.
- New chunk-geometry constants so callers do no arithmetic with magic numbers:
  `VxampMetadata.FirstRegionChunk` (= 65: the 1-based 128-byte chunk containing `Offset`)
  and `VxampMetadata.OffsetInFirstChunk` (= 64: where the region starts inside that chunk).
  Both derived from `Offset` and the 128-byte chunk size, with a comment noting the chunk
  size is the device protocol's, mirrored here purely for geometry.
  `static int LastRegionChunk(int blockLength)` returns the 1-based chunk holding the block's
  final byte.

## 3. ViewModel: two-step fetch

`AmpListViewModel.LoadDetailsCoreAsync`'s cache-miss branch replaces
`ReadAmpAsync(index)` + `TryRead` with:

1. `head = await _amps.ReadChunksAsync(index, VxampMetadata.FirstRegionChunk, 1, ct)` —
   one dread; the region's first 64 bytes are `head[OffsetInFirstChunk..]`.
2. `blockLen = VxampMetadata.BlockLength(regionStart)`. If null → `meta = null`
   ("No metadata" state, exactly today's semantics for absent/corrupt blocks).
3. Otherwise allocate a `RegionSize` buffer, copy the head's region bytes in; if
   `LastRegionChunk(blockLen)` > `FirstRegionChunk`, read the remaining chunks with one more
   `ReadChunksAsync` call and copy them in; `meta = VxampMetadata.TryReadRegion(buffer)`.
4. Cache stores `(string Name, AmpMetadata? Meta)` — the `Slot` byte array member is REMOVED
   (nothing consumes it since the save path re-reads the device).

Everything else in the load path is unchanged: synchronous pane clear, per-load CTS,
supersession checks, `AmpServiceException` → `DetailsError`, cache-hit path,
post-upload/post-save refresh.

## 4. Safety invariants (unchanged by design)

- `SaveMetadataAsync` still performs its own FULL fresh `ReadAmpAsync` with the vxamp-header
  and unparseable-region guards before flashing. Partial reads are display-only.
- Uploads, backups, delete, verify: untouched.
- A glitched partial read throws (never caches, never renders garbage) — same contract as the
  full validated read.

## 5. Testing

- **Core (`SlotBlobReadValidationTests` or sibling):** `ReadChunkRangeAsync` returns the
  correct bytes for a mid-slot range (seed a recognizable pattern); dropped chunk inside the
  range throws with the chunk number in the message; torn odd-length hex throws (not
  `FormatException`); argument validation (bad index/first/count) throws.
- **Codec (`VxampMetadataTests`):** `TryReadRegion` round-trip via a `Write`+slice;
  `BlockLength` on a valid header, zero padding, bad magic/version, oversize len;
  boundary blocks — len that ends exactly at a chunk edge, len 0 (header-only block, empty
  JSON is invalid → TryReadRegion null but BlockLength returns 8), max len 4024;
  `TryRead` still passes its whole existing suite (delegation is invisible).
- **VM (`AmpListViewModelTests`):** dread-count assertions via the existing `DreadCount`
  helper — selecting an amp with a typical block issues exactly
  `1 + (LastRegionChunk-65)` dreads (compute from the seeded block); a no-metadata amp
  issues exactly 1; existing tests that assert 96 dreads per details load are UPDATED to the
  new counts (`Reselecting_hits_the_cache_not_the_device`, `Rename_invalidates_...`,
  `Refresh_invalidates_...`); the slot-26 incident tests keep passing (the save path still
  full-reads, so their glitch fakes keyed on chunk 65 still bite where intended — adjust
  glitch chunk numbers only if a fake targeted a chunk the details read no longer requests).
- **On-device spot check (one-time, informal):** select an amp with metadata and one without;
  confirm ~sub-second and ~instant respectively; note timings in the PR/ledger.

## Out of scope

- Persistent metadata cache (revisit only if region reads still feel slow).
- IR metadata (no SSMD for IRs yet; `ReadChunkRangeAsync` is ready when that lands).
- Serial-timing changes.
