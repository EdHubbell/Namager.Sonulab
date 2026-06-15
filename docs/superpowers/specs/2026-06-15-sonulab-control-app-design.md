# Sonulab StompStation Control App — Design Spec

Status: approved design (sections walked through 2026-06-15), grounded by Phase 0 protocol discovery.
Companion docs: `PROTOCOL.md` (wire protocol), `docs/PHASE0-protocol-discovery.md` (discovery log).

## 1. Goal & scope
A fast Avalonia (.NET 10) **desktop app** to manage a Sonulab StompStation ("AMP Station",
ESP32-S3, fw 2.5.1) over USB serial, fixing VoidX-Control's slow workflow.

**v1 (this spec):** preset management — list, **reorder (VoidX has none)**, duplicate, rename,
delete, and a **generic parameter editor** driven by the device's own schema; plus
backup/restore. Windows-first, serial-only (`COM6`, 115200 8N1).

**Out of scope for v1:** amp/IR list reordering (v1.1), `.nam` upload (phase 2 — needs RE of
VoidX's .nam→device-binary conversion), BLE/WiFi transports.

## 2. Protocol (confirmed — see PROTOCOL.md)
- Commands NUL-terminated ASCII; responses CRLF-separated `path:{...}` records. Device streams
  meters (`root\sys\_meters\`, `root\usb\_status`) continuously — reader filters them.
- Five verbs: `read`, `browse`, `write` (+`"save":"save"`), `dread`, `dwrite`.
- Slots: 30 each. presets 8192 B/64 chunks, amp 12288/96, ir 4096/32; chunk 128 B; name at chunk -1;
  empty slot = empty name. Blob bytes transferred as hex.
- `browse root\app` returns a complete, self-describing UI schema (type/min/max/def/unit/shape/dec/
  options/ref) — the parameter editor needs no baked metadata.

## 3. Architecture (layered; each layer testable in isolation)
```
Sonulab.sln
├─ Sonulab.Core   (no UI; pure, unit-testable)
│   Transport:  ISonuLink  -> SerialSonuLink (System.IO.Ports) | FakeSonuLink (in-memory, fixtures)
│   Protocol:   SonuClient  -> ReadAsync/BrowseAsync/WriteAsync/DReadAsync/DWriteAsync
│                              (serialized 1-in-flight queue, NUL framing, meter filter, ACK/timeout/retry)
│   Model:      NodeTree, NodeSchema (from browse), Slot, PresetBlob (.pst parse/serialize), Param
│   Services:   DeviceRepository (lists, select, save), SlotService (dread/dwrite blob move/copy),
│               PresetEditor (schema+values), BackupService (.pst snapshot/restore)
├─ Sonulab.App   (Avalonia MVVM, CommunityToolkit.Mvvm)
└─ Sonulab.Tests (xUnit; FakeSonuLink + real *.pst fixtures + recorded device transcripts)
```
`SonuClient` mirrors the wire verbs faithfully; feature logic lives in the services above it.

## 4. Connection
- Open `COM6` on launch; auto-probe baud (115200 first) via `read root\sys\_name`.
- Show connection status; degrade to offline mode (local `.pst` editing) if no device.
- Identity from `read root\sys\_name` / `_id` / `_ver`.

## 5. Features & data flow
- **List** — `read root\presets` (and amp/ir) -> 30-name array; index = slot, "" = empty. Instant.
- **Open one** — lazy: `write root\app\preset:{"value":name}` to recall, then `browse root\app` for
  values+schema. (Reading the active params is fast; full blob not needed for editing.)
- **Edit (generic editor)** — build UI from `browse root\app`: float->slider+box (min/max/shape/dec/
  unit), enum->dropdown(options), plist->dropdown(ref list), string->text. Edits staged in memory.
- **Save** — `write root\app\<param>:{"value":...}` for changed nodes, then
  `write root\app\preset:{"value":<name>,"save":"save"}`.
- **Rename** — `dwrite root\presets:{"index":N,"chunk":-1,"value":hex(name) padded 128}`.
- **Delete** — same with 128 zero bytes.
  Write-to-slot primitive (CONFIRMED): name slot N (`dwrite` chunk -1) -> `write` each `root\app\…`
  value -> `write root\app\preset:{"value":"<name>","save":"save"}` (save targets the named slot).
- **Duplicate** — read source params (select it, `browse root\app`), then write-to-slot an empty
  slot with a new unique name.
- **Reorder (drag / up-down)** — model the new order, then realize it by re-writing moved slots.
  FAST PATH (preset already on device): name target slot -> **select source**
  (`write root\app\preset:{"value":"<src>"}` loads its full state) -> `save` to target. ~2-3
  commands/slot, not 157. (157-param replay is only for restoring a `.pst` not on the device.)
  Names must stay UNIQUE during the shuffle (temp names) since save addresses by name. Do it as an
  atomic, verified transaction (§6) with a progress indicator.
- **Backup** — `dread` all non-empty slots -> timestamped `.pst` folder (read side).
  **Restore** — write-to-slot primitive per `.pst` (NOT `dwrite` of content).

## 6. Safety (perform-ready; "no bad preset on stage")
- **Auto-backup before any write** (affected slots -> local `.pst`).
- **Staged edits + explicit Save**; dirty indicator; one transactional flush.
- **Atomic reorder/copy** with **read-back verify** (re-`dread` and compare) and **auto-rollback**
  from backup on any mid-transaction failure — never a half-shuffled/duplicate/missing slot.
- **Snapshot-all / Restore-all** (gig-ready known-good state) and a **Verify device** action.
- Protocol guards: no-zero-byte payload check, reply/ACK timeout + bounded retry.
- **Wire-log debug panel** (every command/response) for diagnosis.

## 7. Testing (TDD in Sonulab.Core)
- `.pst` parse->serialize byte-identical round-trip (fixtures = existing `*.pst`).
- Reorder/duplicate/rename/delete produce expected slot states; simulated failure -> exact rollback.
- Protocol: NUL framing, meter filtering, dread/dwrite chunk math (64/96/32), ACK/timeout/retry.
- Contract tests replay recorded device transcripts (from the Phase 0 captures) against FakeSonuLink.
- Avalonia view-model tests for list/drag/dirty logic; UI verified manually via a device checklist.

## 8. Risks / open items
- **RESOLVED: preset content is NOT written via `dwrite`** (that path is for amp/IR model files).
  Presets persist via **save-from-live-state**, and **`save` targets the slot whose name matches**.
  Write-preset-to-slot N: (1) `dwrite` name to slot N (chunk -1); (2) `write` each `root\app\…`
  value from the `.pst`; (3) `write root\app\preset:{"value":"<name>","save":"save"}`. Proven
  byte-identical (Pano-Verb -> slot 25). **Reorder/copy build on this**; the reorder algorithm must
  keep slot names unique during a shuffle (temp names) since save addresses by name. Writing a
  preset also changes the pedal's live/active state (select a benign preset afterward if needed).
- Protocol `index` is 0-based; **UI shows slot = index + 1**.
- Save is **by name** (device picks slot) — verify slot placement for duplicate/save-as; the app
  should re-`read root\presets` after save to learn where it landed.
- **Selecting a preset to edit changes the pedal's active (audible) preset** — intended for a
  management context; the app warns if used while performing.
- Reorder throughput at 115200 (seconds for multi-slot moves) — acceptable; show progress.
- `.nam` conversion (phase 2) remains unreversed. (Amp/IR name lives in chunk 0, not -1 — handle
  per item type when v1.1 adds amp/IR management.)
