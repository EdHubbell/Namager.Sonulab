# Modulation block + recursive parameter nesting — design

Date: 2026-08-03
Status: approved, not yet implemented
Retires: `docs/STATUS.md` ranked follow-up #1 ("`mod` block is not editable in NAMager")

## Problem

`ParameterEditorViewModel.Blocks_InScope` is `{ gate, exp, comp, amp, eq, ir, delay, reverb }`. It
omits `mod`, so the pedal's Modulation block — chorus / flanger / phaser, plus Tremolo and a
Tone-and-Character folder — has never been editable in NAMager, despite appearing in every `.pst`.

Adding `"mod"` to that list is not sufficient. The tree-builder in `LoadCoreAsync` understands
exactly one level of nesting:

```csharp
var seg = rec.Path.Split('\\');       // [root, app, block, (folder?), leaf]
if (seg.Length == 4) section.Fields.Add(labeled);
else                 subgroups[prefix + "\\" + seg[3]].Fields.Add(labeled);
```

Modulation is three levels deep (`root\app\mod\trfolder\rate\rawdata`), so its Tremolo Rate
controls would be flattened into Tremolo as three unrelated rows with no indication they belong to
a Rate control. This is not hypothetical: the **Delay block already renders this way today**
(`delay\dlytime\*`, `delay\modfolder\rate\*`, `delay\ddfolder\rtime\*`).

## The firmware tree (captured, fw 2.5.1)

`dotnet run --project tools/HwCheck -- --browse root\app\mod`, 24 records:

| Path | type | Notes |
|---|---|---|
| `mod` | `item` / `hfolder` | desc `"Mod"` |
| `mod\on_off` | enum | ON/OFF, def OFF |
| `mod\mode` | enum | Chorus / Flanger / Phaser |
| `mod\rate` | `item` / **`module`** | value `"partempo"` — container, **not editable** |
| `mod\rate\rawdata` | float | 0.05–8 Hz |
| `mod\rate\sbdv` | enum | Time Subdivision |
| `mod\rate\lock` | enum | Unlocked / Global / Preset |
| `mod\dpth` | float | Depth, 0–100 % |
| `mod\mix` | float | Dry-Wet, 0–100 % |
| `mod\tcfolder` | `item` / `vfolder` | desc `"Tone and Character"` |
| `mod\tcfolder\{emp,shape,hicut,locut,sphase}` | float/enum | Hi-Cut 900–20000 Hz |
| `mod\trfolder` | `item` / `vfolder` | desc `"Tremolo"` |
| `mod\trfolder\{on_off,dpt,wave,sphase}` | enum/float | |
| `mod\trfolder\rate` | `item` / **`module`** | container, not editable |
| `mod\trfolder\rate\{rawdata,sbdv,lock}` | float/enum | 0.7–15 Hz |

Two facts drive the design:

- **Every leaf publishes a usable `desc`** (Enable, Mode, Depth, Dry-Wet, Emphasis, Shape, Hi-Cut,
  Lo-Cut, Stereo Phase, Waveform, Rate, Time Subdivision, Lock Options), and every folder does too
  (Tremolo, Tone and Character, Rate). The label map needs one entry, not twenty.
- **`rate` is `item`/`module`.** Its `"partempo"` value is a module marker, not a control, and
  `item` is outside `EditableTypes` (`float`, `enum`, `plist`), so the existing filter already
  skips it. `delay\dlytime` and `delay\ddfolder\rtime` are the same shape.

## Design

### 1. One recursive group type

`BlockSectionViewModel` and `SubGroupViewModel` are replaced by a single
`ParameterGroupViewModel` used at every depth:

```
ParameterGroupViewModel : ObservableObject
    string  Header          labels.en.json → firmware desc → prettified segment
    string  Path            root\app\mod\trfolder — stable key for expansion memory
    bool    IsExpanded
    ObservableCollection<object> Items      ordered, mixed: field | group
    IEnumerable<ParameterFieldViewModel> Fields    filtered view over Items
    IEnumerable<ParameterGroupViewModel> Groups    filtered view over Items
    ParameterFieldViewModel? EnableField ; bool? Enabled
    bool ShowEqIcon ; bool ShowLevelIcon ; bool IsEqActive
```

`Items` is the single ordered store; `Fields` and `Groups` are filtered views over it, so display
order and logic can never disagree and nothing is stored twice. The power glyph, the enable
tracking and the expansion memory now exist once and apply at every depth by construction — today
they exist in two classes that could drift.

Views split to match, eliminating two copy-pasted templates (block-level and sub-group-level field
rows, currently differing in label-column width and in whether the amp-detail flyout is present):

- `ParameterFieldView.axaml` — one field row, used at every depth.
- `ParameterGroupView.axaml` — Expander + header icons + `Items`, with two `DataTemplates` (field →
  `ParameterFieldView`, group → `ParameterGroupView`). The recursion is a **self-instantiating
  UserControl**, not a self-referencing `DataTemplate` resource, because Avalonia resolves the
  former reliably.
- `ParameterEditorView.axaml` — toolbar, error line, `ScrollViewer`, `ItemsControl` over `Blocks`.

Indentation is 12 px per level, applied by the parent group; the label column is a single width at
all depths.

### 2. Generic tree building

`LoadCoreAsync` drops the depth test. For each editable record under a block prefix:

```
parentPath = rec.Path minus its last segment
EnsureGroup(parentPath).Items.Add(field)
```

`EnsureGroup(p)` returns the block section when `p` is the block prefix, otherwise finds or creates
a group inside `EnsureGroup(parent(p))`, labelled from `p`'s own browse record. No depth limit, no
firmware-specific knowledge, and a future firmware that nests a fourth level needs no code change.

**Editable container rule.** If a record's path is later used as a group path and that record was
itself editable, its field is moved to be the first item of that group rather than sitting in the
parent's list. On fw 2.5.1 this never fires — every container is `item` — but without it a future
editable container would render its value detached from its own children. Five lines; kept as a
guard, and tested with a synthetic record.

### 3. Order follows the firmware

Groups interleave with fields in browse order rather than being appended after them. Firmware order
for `mod` is `on_off · mode · rate · dpth · mix · tcfolder · trfolder`, so Rate renders third,
between Mode and Depth — matching the pedal, which is the standard being adopted here.

The visible consequence outside Modulation is that Delay's `Time` group (`dlytime`) moves from last
to second. Accepted.

```
▾ Modulation          ⏻          ▾ Delay              ⏻
    Enable  [OFF ▾]                  Enable   [OFF ▾]
    Mode    [Chorus ▾]             ▸ Time
  ▸ Rate                               Feedback  ──●──
    Depth   ──●── 50%                ▸ Tone and Character
    Dry-Wet ──●── 50%                ▸ Modulation
  ▸ Tone and Character               ▸ Dual Delay
  ▸ Tremolo             ⏻
```

### 4. Expansion

| | Default | Memory |
|---|---|---|
| Top-level blocks | collapsed (unchanged) | per-session, keyed by path |
| Nested groups | expanded iff the group's own `on_off` is `ON` | per-session, overrides the default |
| Level block | expanded (unchanged) | per-session, overrides |

Auto-open applies to nested groups **only**. Amp, IR, Delay and Reverb are all `ON` in a typical
preset, so applying it to top-level blocks would expand the entire editor on every preset load. One
level down it does the useful thing: open a block and the parts of the patch actually doing
something are already visible.

`_expansion` is already keyed by node path, so `mod\rate` and `mod\trfolder\rate` are remembered
independently despite sharing the header "Rate".

Auto-open is evaluated at preset load, not live. Toggling Tremolo's `on_off` in the editor does not
make the group jump open under the cursor.

### 5. Labels

One addition to `labels.en.json`:

```json
"root\\app\\mod": "Modulation"
```

Its firmware desc is `"Mod"`. Nothing else is added — every other node's `desc` is already correct,
and an override would only risk drifting from the firmware. No `hidden-params.json` change; the
existing `*\_st` entry covers the only noise under these nodes.

### 6. Unit-aware value readout

The slider readout is hardcoded `{0:F2}`, so Modulation would show Hi-Cut as `18000.00` when the
firmware publishes `unit:"Hz"`, `dec:0`. `ParameterFieldViewModel` already parses `Unit` and `Dec`
and already formats them — in `FormatDefault()`, used only by the reset tooltip.

Extract that into a shared `Format(double)`, add `Display => Format(Number)` (re-raised from
`OnNumberChanged`), and bind the readout to it. The readout column widens 52 → 64 px for
`18000 Hz`. The reset tooltip keeps using the same formatter, so the two can never disagree.

This affects every block: `50%`, `300 ms`, `0 deg`, `-60 dB`, and `1 Hz` where the schema omits
`dec` (the existing `0.##` fallback). Percent has no space before the sign; every other unit does —
that is `FormatDefault()`'s existing rule, inherited rather than redecided.

## Blast radius

1. **Delay and Expression re-render** — nesting and order. Presentation only; no change to what is
   read from or written to the device.
2. **`MatchVolumeTests.PstWithModOn()` rests on a premise that dies.** Its comment — *"`mod` is
   outside `Blocks_InScope`, so nothing the editor builds from `AllFields()` ever carries this
   path"* — is the reason the test exists, and it becomes false. The test will likely still pass
   for the wrong reason, because the fake's browse records contain no `mod` node at all. Fix: add
   `mod` to the fake's browse records so the test proves what it claims.
3. **`EstimateLoadedAsync`'s doc comment must be rewritten.** Reading the loaded slot's own `.pst`
   as the base layer stays correct, but its justification changes: not "`mod` is out of scope" but
   "`LevelModel.InputPaths` is not a subset of what the editor exposes, and `LevelModel.IsOff`
   treats an absent path as OFF". A stale comment here is worse than none — it is the comment that
   explains why the volume-match caveat is direction-independent.
4. **`AllFields()` recurses**, so Save writes depth-3 fields. That is the feature. No cost change:
   only dirty fields are written.
5. `ParameterEditorViewModelTests` and `AmpDetailFlyoutTests` reference the deleted type names —
   mechanical churn.

Not in scope: the firmware's per-node `shape` taper (sliders stay linear), the `style` colour hints
(`mod` is `#00FFFF`, `trfolder` `#FFFF00`) — the theme uses tokens, not device hex — and any
purpose-built compound control for the `partempo` rate/time nodes.

## Testing

Unit tests build their browse records from the **captured dump above**, not hand-written records,
so the fixture cannot drift from the device:

- `mod` renders between `ir` and `delay`; Rate is the third item, before Depth.
- `rate` (`item`/`module`) produces a group and no field row.
- Three-level nesting survives: Modulation → Tremolo → Rate contains `rawdata`, `sbdv`, `lock`.
- An editable container's value becomes its group's first item and is not duplicated in the parent
  (synthetic record — does not occur on fw 2.5.1).
- Headers come from `desc` (Tremolo, Tone and Character, Rate); the block header comes from the
  override (Modulation).
- Auto-open: `trfolder\on_off` = ON → expanded; OFF → collapsed.
- Expansion memory beats auto-open across a preset switch, and `mod\rate` / `mod\trfolder\rate`
  are remembered independently.
- `AllFields()` reaches `trfolder\rate\rawdata`; editing it dirties the preset and Save writes it.
- `Display` formats `18000 Hz`, `50%`, `0 deg`, `1.00 Hz`.
- Delay keeps every field it has today, at its new nesting and order.

## Hardware validation

`docs/HARDWARE-VALIDATION-modulation.md`:

- Every Modulation parameter round-trips: edit → Save → select another preset → return → value
  persists; confirm against a fresh `--browse root\app\mod`.
- Chorus, Flanger and Phaser each audibly respond to Depth, Dry-Wet and Rate.
- Tremolo `on_off`, Depth, Waveform and its own Rate behave independently of the parent block.
- Rate `lock` = Global / Preset behaves sanely against the pedal's tempo (observe only; do not
  design around it this cycle).
- Delay is unchanged in behaviour after the re-render, and its `Time` group edits correctly.
- Back up before any write (`BackupService`, `docs/backups/`).

On completion, remove ranked follow-up #1 from `docs/STATUS.md`. `CLAUDE.md` needs no change — it
mentions `Blocks_InScope` only to say that `root\app\output` stays out of it, which remains true.
