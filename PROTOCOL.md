# Sonulab StompStation ("AMP Station") — VoidX Control Protocol

Reverse-engineered from a USBPcap capture (`SonulabCapture1.pcapng`) + static strings in
`C:\Program Files (x86)\VoidX-Control\data\app.so` (Flutter AOT). **Not encrypted.**

## Hardware / transport
- Device is **ESP32-based** (`VoidxFlasher` = Espressif `ESP-Flasher.exe`).
- Three interchangeable transports, all speaking the **same protocol** (VoidX abstracts them
  as `DartSerialCommunicationInterface`, `DartSocketCommunicationInterface`, BLE):
  - **USB serial** via CH340 (`VID_1A86 PID_7523`, `COM6`) — bulk OUT `0x02` / IN `0x82`.
  - **BLE** (used for the heavy lifting in the capture; ATT Write Command `0x52` host→device,
    Handle Value Notification `0x1b` device→host as ACKs).
  - **TCP socket** (WiFi / mDNS via `flutter_nsd`).
- Baud rate: not yet confirmed (CH340 sets it via vendor control req `0x9A`, not CDC). Auto-detect
  by opening COM6 and sending `read root\sys\_name\0` until a valid reply appears (try 115200 first).

## Wire framing
- **Commands (host -> device): NUL-terminated (`\x00`) ASCII**, e.g. `read root\sys\_name\0`.
- **Responses / streamed data (device -> host): CRLF-separated `path:{"value":…}` records** —
  identical to the `.pst` preset file layout. (NOT NUL-terminated.)
- No length prefix on the app layer, no checksum. Payloads must contain **no embedded zero bytes**.
- BLE characteristics: host->device on ATT handle `0x0010`, device->host (notify) on `0x0012`.
  Over USB serial (CH340) the same bytes ride bulk OUT `0x02` / IN `0x82`.
- The device **continuously streams meters** on the notify channel: `root\sys\_meters\_in0/_in1/`
  `_out0/_out1` and `root\usb\_status`. A reader MUST filter these out to find command responses.
- `read root` returns only **root-level scalars** (status + meters), not a recursive dump;
  enumerate children with `browse <path>`.
- Config is a **node tree** with backslash paths (`root\app\amp\amp`, `root\sys\_name`, …).

## Commands (host -> device)
| Verb | Form | Notes |
|------|------|-------|
| `read`   | `read <path>\0` | returns `<path>:{"value":<v>}\0`; for a list node, value is a 30-element name array |
| `browse` | `browse <path>\0` | node(s) with full schema: desc/type/min/max/def/unit/shape/dec/options/ref |
| `write`  | `write <path>:{"value":<v>}\0` | set a value; `,"save":"save"` saves current `root\app\*` as a preset by name |
| `dread`  | `dread <path>:{"index":N,"chunk":C}\0` | read one blob chunk; C in 1..(size/128); name at chunk -1 |
| `dwrite` | `dwrite <path>:{"index":N,"chunk":C,"value":"<hex>"}\0` | write one blob chunk (128 B); name at chunk -1 |

NOTE: protocol `index` is **0-based**; VoidX UI shows slots **1-based** (index 25 = "Slot 26").
The app should display `index + 1`.

Slot model (per `read`/`browse` of the list node): 30 slots each.
`root\presets` size 8192 (64 chunks), `root\amp` 12288 (96), `root\ir` 4096 (32); all `chunk:128`.
Empty slot = empty name; download-all skips empties. CONFIRMED via SonulabCapture_DownloadAll:
`dread root\presets:{"index":N,"chunk":1..64}`.

## Reorder / copy / backup (composed — VoidX has NO native reorder)
### CRITICAL FINDING (guarded write test, 2026-06-15): preset CONTENT is NOT dwrite-able
- Writing `dwrite root\presets:{"index":25,"chunk":1..64,...}` to slot 25 had **no effect**
  (read-back chunk 1 = all zeros); only the name (`chunk:-1`) persisted. Tried both name-first and
  content-first. => the firmware does not accept preset content via `dwrite` (that path is for
  amp/IR model files). `dread` still reads preset content fine.
- **Presets persist only via save-from-live-state:** `write root\app\preset:{"value":"<name>","save":"save"}`
  serializes the current `root\app\*` parameters into a slot.
- Implications:
  - **Write a preset** = load its params into `root\app\*` (write each `root\app\…` value from the
    `.pst`), then `save`. NOT a blob `dwrite`.
  - **Reorder / Copy** must be built on load-params + save.
  - RESOLVED (save targeting, guarded experiment 2026-06-15): **`save` targets the slot whose name
    matches `value`.** Write-preset-to-slot N =
      (1) `dwrite root\presets:{"index":N,"chunk":-1,"value":hex(uniqueName)}`  (name the slot)
      (2) for each `root\app\…` line in the .pst: `write <that line>`           (load live params)
      (3) `write root\app\preset:{"value":"<uniqueName>","save":"save"}`        (save -> slot N)
    Verified: replaying Pano-Verb.pst this way produced slot 25 content byte-identical to the file.
    Reorder algorithm must keep slot names UNIQUE during the shuffle (use temp names) since save
    addresses by name.
- **Backup/Restore**: backup = `dread` all non-empty slots -> `.pst`. Restore = load params + save
  (per above), NOT `dwrite`.
- Rename (`dwrite … chunk:-1` name) and delete (chunk:-1 zeros) DO work (name table is dwrite-able).

### Observed examples
```
read root\sys\_id            -> root\sys\_id:{"value":"c7e811051914272110b41dc7c558"}   (device id)
read root\sys\_name          -> root\sys\_name:{"value":"AMP Station"}
browse root
write root\app\amp\amp:{"value":"Quad Reverb Randall Head SM57"}   (set current preset's amp)
write root\app\ir\on_off:{"value":"ON"}
write root\app\preset:{"value":"","save":"save"}                   (commit/save preset)
```

## File upload (`dwrite`) — e.g. a `.nam` amp model
Target a **slot** via `index`. Chunks:
- `chunk:0`  = item **name**, ASCII, zero-padded to 128 bytes (256 hex chars).
- `chunk:1..N` = file payload, **hex-encoded**, **128 bytes (256 hex chars) per chunk**,
  zero-padded out to the slot's fixed capacity (observed N up to 96 → ~12 KB region).
- `chunk:-1` = **terminator / commit** (value all zeros) sent LAST.

Observed amp upload: `dwrite root\amp:{"index":15,"chunk":0..96 then -1, ...}` (98 writes).
Preset upload uses `dwrite root\presets` with the name in `chunk:-1`.

### IMPORTANT caveat — `.nam` is converted, not raw
`chunk:1` began `40 20 00 00 00 00 00 00 ... "Amp model"` — a **binary header**, not raw NAM JSON
(which starts with `{`). So **VoidX converts a `.nam` file into a device-specific binary format
before upload.** A CLI that uploads raw `.nam` files must either reproduce this conversion or
capture/borrow it. Rename + renumber (name chunk + `index`) do **not** need the conversion.

## CONFIRMED via live read-only probe (2026-06-15, `docs/probe-output.txt`)
- **Serial:** `COM6` (CH340), **115200 8N1**. Auto-probe `read root\sys\_name` succeeds.
- **Device:** ESP32-S3, firmware `root\sys\_ver` = 2.5.1, `root\sys\_license` = "stompstation1".
- **Lists are addressable as one node returning a 30-element name array** (array index = slot,
  empty string = empty slot):
  - `read root\presets` → 30 names; `size 8192`, `chunk 128`, `item_type "pst_pst"`.
  - `read root\amp`     → 30 names; `size 12288`, `chunk 128`, `item_type "vxamp"`.
  - `read root\ir`      → 30 names; `size 4096`,  `chunk 128`, `item_type "wav_44100"`.
- **The node tree is self-describing.** `browse <path>` returns, per node, a JSON object with:
  `desc`, `value`, `type` (`item`/`float`/`enum`/`plist`/`action`/`ctrl`), and for floats
  `min`,`max`,`def`,`unit`,`shape` (taper 0..1),`inv`,`dec`; for enums `options`; for `plist`
  `ref` (the list node a dropdown draws from, e.g. `root\app\amp\amp` → `root\amp`).
  => the generic parameter editor is driven entirely by `browse root\app`; no baked param table needed.
- **Selecting** the active preset/amp/IR = `write` the name to `root\app\preset` /
  `root\app\amp\amp` / `root\app\ir\ir`.
- `browse root` returns the entire tree (~250 nodes) in one streamed response.

## Renumber / rename
- **Renumber** = the `index` (slot) field in `dwrite`.
- **Rename** = the name (`chunk:0` for amps; `chunk:-1` for presets), re-sent for the slot.
- CONFIRMED rename (SonulabCapture_RenamePresetPrincetonCompTestTwo, slot 4):
  `dwrite root\presets:{"index":4,"chunk":-1,"value":"<hex(name) zero-padded to 128 bytes>"}`
  — name only; content untouched. Name = ASCII hex, padded to 128 bytes (256 hex chars).
- `app.so` also exposes file ops: "rename the item", `File_LengthFromPath` — there may be a
  lighter rename path; confirm live with `browse root\amp` to see how slots/names are listed.

## Extracted artifacts in this folder
- `host_cmds_reassembled.txt` — all 1556 logical host commands, NUL-reassembled.
- `decoded_host_cmds.txt` — per-BLE-write decode (fragmented).
- `*.pst` — preset files; same node format as the wire protocol.

## Open items to confirm live (open COM6, query directly)
1. Baud rate.
2. `browse`/`read` **response** format for enumerating existing amp/IR slots (names + indices).
3. Exact `.nam` -> binary conversion (or decide CLI scope around it).
4. Whether `write`/`dwrite` need inter-message delays / wait-for-ACK pacing.
