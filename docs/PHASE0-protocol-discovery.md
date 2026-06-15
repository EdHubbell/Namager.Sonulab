# Phase 0 — Protocol Discovery (prerequisite for the Avalonia app)

Goal: confirm the exact device commands for **listing, reading, reordering, copying, and saving
presets** (and listing amp/IR models) so the `Sonulab.Core` protocol/repository layer can be built
and unit-tested against a faithful `FakeSonuLink`. Read-only probing first; writes only on a
throwaway/backed-up slot.

## Confirmed so far (from captures + app.so)
- Transports (same protocol on each): USB serial (CH340 `COM6`), BLE, WiFi socket.
  - BLE handles: **`0x0010` = host→device (commands)**, **`0x0012` = device→host (notifications)**.
- **Commands (host→device): NUL-terminated ASCII.** Verbs: `read`, `browse`, `write`, `dwrite`.
- **Responses/stream (device→host): CRLF-separated `path:{"value":…}` records** — same layout as `.pst`.
- Device **continuously streams meters**: `root\sys\_meters\_in0/_in1/_out0/_out1`, `root\usb\_status`.
  The reader MUST ignore these (filter `root\sys\_meters\` and `root\usb\_status`).
- `read root` returns only **root-level scalars** (status + meters), NOT a recursive dump.
- Node namespaces: `root\sys\…` (device id/name/meters), `root\app\…` (current preset params),
  `root\presets` (preset slots, indexed; name in `dwrite … chunk:-1`), `root\amp` (amp models, indexed),
  `root\ir` / `root\app\ir` (IR).
- Identity: `read root\sys\_id` → device id; `read root\sys\_name` → e.g. "AMP Station".
- Saving current params into the selected preset: `write root\app\preset:{"value":"","save":"save"}`.
- Upload format (`dwrite`): `chunk:0` (amps) or `chunk:-1` (presets) = name (zero-padded);
  data chunks = **hex**, 128 bytes/256 chars each; `chunk:-1` (amps) sent last = commit/terminator.
- "Princeton Comp Test" was a **preset** (`dwrite root\presets index:4 chunk:-1`), confirming the
  preset **name table** (`root\presets`) is separate from preset **content** (`root\app\*` + save).

## Status after Step A (read-only probe, 2026-06-15)
RESOLVED:
1. **List presets** — `read root\presets` returns a 30-name array (index = slot, "" = empty). DONE.
2. **List amp/IR** — `read root\amp` (30, "vxamp"), `read root\ir` (30, "wav_44100"). DONE.
3. **Param metadata** — `browse root\app` is fully self-describing (min/max/def/unit/shape/dec/
   options/ref). Auto-editor needs no baked table. DONE.
4. **Select active item** — `write root\app\preset` / `root\app\amp\amp` / `root\app\ir\ir`. DONE.
7. **Baud** — 115200 8N1 on COM6. DONE.
8. **Pacing** — read a CRLF window after each command, filtering `root\sys\_meters\` +
   `root\usb\_status`. Works. (Still confirm wait-for-ACK on writes.)

- **Rename** — CONFIRMED: `dwrite root\presets:{"index":N,"chunk":-1,"value":"<hex(name) padded
  to 128 bytes>"}` renames only, content untouched. DONE.
- **Delete** — CONFIRMED: `dwrite root\presets:{"index":N,"chunk":-1,"value":"<128 zero bytes>"}`
  (empty name = empty slot). DONE.
- **Save** — CONFIRMED: `write root\app\preset:{"value":"<name>","save":"save"}` saves current
  `root\app\*` state into a preset addressed by NAME (device assigns slot). DONE.
- **Duplicate** — composes: select source (`write root\app\preset:{"value":"<src name>"}`) then
  save-as a new name. (Verify slot placement live.)
- **Backup/snapshot** — composes: for each non-empty preset, select + `browse root\app`, serialize
  to `.pst`. No raw blob-read required.

- **Blob read** — CONFIRMED (SonulabCapture_DownloadAll): `dread root\presets:{"index":N,"chunk":C}`
  for C in 1..64 (8192/128). Mirrors `dwrite`. Download-all skips empty slots. DONE.
- **Reorder** — RESOLVED (no native VoidX reorder): implement as `dread` slot N -> `dwrite` slot M,
  rewriting the shifted range atomically with read-back verify + rollback. DONE (design).

## PHASE 0 COMPLETE — all verbs and the slot model are confirmed. Proceed to full spec + plan.

## Method
### Step A — read-only live probe (safe; no writes)
Run `tools/probe.ps1` with **VoidX-Control closed** and the pedal on `COM6`. It auto-probes baud,
sends a battery of `browse`/`read` commands, filters meter noise, and logs every request/response to
`docs/probe-output.txt`. Answers Q1–Q3, Q7–Q8 with zero risk.

### Step B — targeted VoidX capture (for write/reorder/copy semantics)
With Wireshark+USBPcap capturing the **CH340 (force USB, disable PC Bluetooth so traffic uses serial)**:
do ONE of each, slowly: load a single preset; rename a preset; reorder two presets; duplicate a preset.
Save as `SonulabCapture_PresetOps.pcapng`. Decode to confirm Q4–Q6 exactly as the app does them.

### Step C — confirm with a guarded write
On a **backed-up throwaway slot**, replay the discovered reorder/copy and read back to verify.

## Deliverable
Update `PROTOCOL.md` with confirmed list/reorder/copy/save commands, then proceed to the full
implementation spec + plan for the Avalonia app.
