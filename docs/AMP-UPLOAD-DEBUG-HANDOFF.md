# RESOLVED — `--upload-amp` amp write now persists (root cause found 2026-07-03)

**Status: FIXED and verified on hardware.** `dotnet run --project tools/HwCheck -- --upload-amp
"<file>.vxamp" <slot>` prints `RESULT: UPLOAD-AMP OK` reproducibly (verified 3× on fw 2.5.1 over
serial: distilled `Dumble Steel SS Clean Full2.vxamp` → idx 24, capture-extracted Bassman blob →
idx 25, Pano-Verb restore → idx 7). Read-back equals the intended 12288 bytes and the name appears
in the `root\amp` name table.

## Root cause

**`chunk:-1` is not a "zeros terminator" — it is the slot's NAME-TABLE write, and it is what
COMMITS the staged content.** Sending all-zeros at `chunk:-1` (what this tool did, copying the
VoidX capture) *deletes* the slot's name-table entry, and the device silently discards the staged
chunks 0..96. Every chunk is ACKed either way, so the transfer looks perfect and nothing persists.
Same semantics as the confirmed preset rename/delete: name at `chunk:-1` = set name, zeros = delete.

The fix: send the **padded name again at `chunk:-1`** (identical 128-byte value as chunk 0). Full
working sequence (all `dwrite root\amp:{"index":N,"chunk":C,"value":"<hex>"}`):

1. `chunk:0`  = name, ASCII zero-padded to 128 B
2. `chunk:1..96` = the 12288-byte `.vxamp` payload, 128 B per chunk
3. `chunk:-1` = **the name again** (NOT zeros) → device commits; name appears in `root\amp` list

## Why the old code looked right but wasn't (evidence trail)

- Decoded VoidX's own upload from `../SonulabCapture1.pcapng` (USBPcap of a **BLE dongle** —
  HCI/ACL/L2CAP/ATT; write cmds on handle 0x10, notifications on 0x12; parser scripts were in the
  session scratchpad, easily rewritten: reassemble L2CAP by ACL PB flags, split OUT stream on NUL,
  IN stream on CRLF).
- The capture shows a per-chunk **ACK**: the device replies `dwrite root\amp:{"index":N,
  "chunk":<nextExpected>}` to every chunk, and after chunk 96 the ACK literally requests
  `"chunk":-1`. Over serial we get the same ACKs — `SerialSonuLink.SendAsync`'s NUL-stop already
  waits for them, so the write path was never desynced (hypothesis 1 disproven by instrumenting
  every dwrite's raw response: all 98 ACKs correct, still nothing persisted).
- No prepare/commit command exists around the 98 dwrites in the capture (hypothesis 2 disproven).
- Pacing irrelevant (hypothesis 3): ACK-paced writes at ~45–60 ms/chunk fail or succeed purely
  based on the chunk `-1` payload.
- Content validation ruled out: the byte-exact blob VoidX uploaded (extracted from the capture,
  and byte-identical to today's slot 0 `Bassman 5F6A - Super Clean`) also failed with zeros at -1.
- The smoking gun: an earlier failed upload to occupied idx 7 **erased that slot's name**
  (backups `docs/backups/amp-7-20260703-2119*.vxamp` = `Pano-Verb`, restored since). Zeros at
  `chunk:-1` = name delete. And in the capture itself, the single `root\amp` list the device
  pushed right after VoidX's upload does NOT contain the uploaded name ("Bassman 5F6A - Super
  Clean - 1") and shows the target idx 15 empty — **the captured VoidX upload apparently failed
  the same way** (its app strings include "Failed to upload the item!"). The capture was never a
  working ground truth for the terminator.

## What changed (code)

- `tools/HwCheck/Program.cs` `--upload-amp`:
  - sends the name at `chunk:-1` (the fix);
  - verifies every per-chunk ACK (`"chunk":<nextExpected>`) and aborts fast on mismatch;
  - skips the pre-write backup dread when the target slot is empty per the name table
    (a 96-chunk dread of an empty slot is 96 no-response timeouts);
  - `--name <name>` overrides the name (default: file stem, ≤31 chars);
  - new `--list-amps` (read-only name table), `--delete-amp <slot>` (guarded: backup + zeros at
    `chunk:-1`), `--dread-probe <path> <idx> <chunks...>` (read-only diagnostics).
- `src/Sonulab.Core/SonuClient.cs`: `DWriteChunkAsync` returns the raw response window (`Task<string>`)
  so callers can verify the ACK; added `SendRawAsync` for diagnostics.
- `PROTOCOL.md`: corrected the File-upload section (chunk -1 = name/commit, per-chunk ACK flow
  control, dread serves only chunks 1..N) and flagged the preset-content dwrite finding for a
  re-test with the correct sequence.

## Current device state (2026-07-03 22:03)

- idx 24 = `Dumble Steel SS Clean Full2` — the distilled test amp, **left on the pedal for the
  human ear-check**: select it on the pedal / in the app and A/B against idx 4
  (`Dumble Steel SS Clean Full`, VoidX's own fit of a similar capture).
- idx 7 = `Pano-Verb` — restored from backup after the earlier failed-upload erasure.
- Test amp at idx 25 deleted (backup kept: `docs/backups/amp-25-*-deleted.vxamp`).

## Follow-ups

1. **Ear-check** idx 24 (human).
2. Guarded re-test of preset content upload with the corrected sequence (see PROTOCOL.md note).
3. IR upload (`root\ir`, 32 chunks) presumably follows the same pattern — untested.
