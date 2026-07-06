# Amp slot metadata (SSMD block) — design

**Date:** 2026-07-06
**Status:** Approved approach A (embedded block); spec for review.

## Problem

The app distills `.nam` files to `.vxamp` and uploads them to `root\amp` slots, but once on the
pedal an amp is just a 31-char name. Nothing records which source file it came from, when, how
faithful the distillation was, or where the tone was downloaded from.

## Approach (decided)

Embed a metadata block in the **unused padding region of every amp slot**. A slot is 12288 B;
the firmware payload (header + DSP body) is only the first 8256 B — the header's size field
says so, and the corpus proves VoidX-Control zero-fills the remaining 4032 B. Our upload path
(`SlotBlobService.UploadAsync`) already writes all 96 chunks and byte-verifies the read-back,
so anything placed there round-trips through device flash. The DSP payload is never touched, so
an amp's sound cannot change.

Rejected alternatives: a local sidecar catalog (metadata wouldn't live on the pedal or survive
a PC move) and an embedded-block-plus-persistent-cache hybrid (deferred; a session cache is
enough for the on-demand read path chosen).

## 1. SSMD block format (offset 8256 within the 12288-B slot)

| Offset (rel) | Size | Contents |
|---|---|---|
| 0 | 4 | magic ASCII `"SSMD"` |
| 4 | 2 | u16 LE version = 1 |
| 6 | 2 | u16 LE JSON byte length N (0 ≤ N ≤ 4024) |
| 8 | N | UTF-8 JSON |
| 8+N | — | zero fill to slot end |

JSON schema (all fields optional; unknown fields preserved on edit):

```json
{
  "source":  { "file": "Bassman 5F6A.nam", "size": 1834024,
               "modified": "2026-05-01T14:22:00Z", "sha256": "<hex>" },
  "uploaded": "2026-07-06T09:15:00Z",
  "nam":     { "...": "verbatim copy of the .nam file's own metadata object, if present" },
  "distill": { "version": "<Sonulab.Distill assembly version>",
               "fidelity": { "...": "quality metrics the distiller computes" } },
  "notes":   "user free text",
  "url":     "https://tonehunt.org/..."
}
```

**Size budget:** hard cap 4024 B of JSON. If exceeded, trim in priority order: (1) `nam`
passthrough dropped, (2) `notes` truncated. The UI enforces a live character budget on the
notes field so truncation is visible before upload, not after.

**Parsing rule:** padding that does not begin with `SSMD` (all VoidX-written slots), an
unsupported version, a length that overruns the region, or JSON that fails to parse ⇒ the slot
"has no metadata". Never an error, never blocks any existing operation.

**Code location:** `VxampMetadata` (static codec: `TryRead(byte[] slot)` / `Write(byte[] slot,
AmpMetadata meta)`) in `src/Sonulab.Distill`, beside `VxampCodec`/`VxampFormat`, with an
`AmpMetadata` record. `Sonulab.App` already references `Sonulab.Distill`.

## 2. Write path (upload)

The Amps-tab upload panel gains two optional inputs: **Notes** and **Source URL**.

For a `.nam` source, `AmpListViewModel.StartUploadAsync`:
1. Computes SHA-256, file size, and last-modified of the `.nam`; reads its top-level
   `metadata` JSON object if present (via `JsonDocument`, independent of `NamParser`).
2. Distills as today (`Distiller.DistillAsync`).
3. Captures distillation info. The distiller already computes a fidelity metric
   (`Fidelity.FidelityVsNam`); if `Distill()` does not currently surface it, extend it to
   return a small `DistillResult` (blob + fidelity) — additive change, existing callers keep
   working via the file-writing `DistillAsync` overload.
4. Stamps the SSMD block into the slot bytes, then writes the stamped bytes both to
   `NAMFiles/Distilled/*.vxamp` and to the device via the unchanged `UploadAmpAsync`.

For a direct `.vxamp` source: `source` basics + `uploaded` + user `notes`/`url` only (no
`nam`/`distill` sections). If the picked `.vxamp` already contains an SSMD block, its fields
are kept and only user-entered fields overwrite.

Backups made by `SlotBlobService.BackupSlotAsync` and `--dump-amps` contain the block
automatically, since it is part of the slot bytes.

## 3. Read path (Amps tab details)

Selecting an occupied amp row shows a details pane:
- First selection triggers `ReadAmpAsync` (~1 s; "Reading…" placeholder; cancelled if the
  selection changes). `VxampMetadata.TryRead` parses the result.
- Parsed metadata is held in a **per-session in-memory cache** keyed by slot index + name,
  invalidated by refresh, upload, delete, and rename. No persistent cache in v1.
- Display: source filename/size/date, upload date, `.nam` metadata (if any), distill fidelity,
  notes, and the URL as a clickable link (opens default browser).
- No block ⇒ "No metadata — uploaded outside StompStation Manager."

## 4. Edit path

The details pane has an **Edit** action for notes and URL (auto-captured fields are read-only):
1. Uses the slot bytes already fetched for display (re-read if evicted).
2. Rewrites only the SSMD region; DSP payload bytes [0, 8256) are passed through untouched.
3. Re-uploads via the existing guarded `UploadAmpAsync` (backup → 98 acked chunks → read-back
   verify, ~3 s), same slot, same name. All existing failure guarantees apply.

## 5. Error handling

- Malformed/absent block ⇒ "no metadata" (see parsing rule). Garbage never propagates.
- Notes over budget blocked at input; programmatic overflow follows the trim priority.
- Upload/edit failures inherit `SlotBlobService` behavior: abort-before-commit on ACK
  mismatch, automatic pre-write backup of occupied slots, clear-on-verify-fail.
- Metadata capture failures during upload (e.g. unreadable `.nam` metadata section) degrade to
  omitting that field — never block the upload.

## 6. Testing

- **Unit (Sonulab.Distill.Tests):** SSMD round-trip; trim priority (drop `nam`, truncate
  `notes`); TryRead tolerance for zero padding, bad magic, bad version, overrun length,
  invalid UTF-8/JSON; payload bytes untouched by `Write`.
- **Unit (Sonulab.App.Tests):** upload stamps block (via `FakeSlotBlobDevice`); details pane
  read/cache/invalidation; edit rewrites only padding; no-metadata display state.
- **Hardware validation gate (before merge to main):** documented in
  `docs/HARDWARE-VALIDATION-amp-metadata.md` — upload a test amp with a full metadata block to
  a spare slot, power-cycle the pedal, select and play it (loads, sounds normal), read back
  and confirm the block survived, then delete the test slot. This settles the one open
  assumption: that firmware ignores the padding region rather than validating it.

## Out of scope (v1)

- IR slots (`root\ir`, 4096 B) — no known padding headroom analysis yet; revisit later.
- Persistent metadata cache / metadata shown inline in the 30-row list.
- Editing auto-captured fields; re-linking a slot to a different source file.
