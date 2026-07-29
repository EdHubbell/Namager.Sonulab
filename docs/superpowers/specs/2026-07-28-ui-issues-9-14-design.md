# UI issues #9, #10, #12, #13, #14 — design

Date: 2026-07-28
Branch: `feature/ui-issues-9-14`
Issues: [#9](https://github.com/EdHubbell/Namager.Sonulab/issues/9),
[#10](https://github.com/EdHubbell/Namager.Sonulab/issues/10),
[#12](https://github.com/EdHubbell/Namager.Sonulab/issues/12),
[#13](https://github.com/EdHubbell/Namager.Sonulab/issues/13),
[#14](https://github.com/EdHubbell/Namager.Sonulab/issues/14)

## Scope

Five user-feedback issues, all in `Namager.App`. **Issue #11** (rename amps/IRs should cascade into
the presets that use them) is deliberately **out of scope** — it is a multi-preset device write with
real blast radius, not a UI change, and gets its own spec and branch.

**Nothing in this branch performs a persistent (flash) write** — no `dwrite`, no `save`, no rename,
no upload. Preset *activation* (`write root\app\preset`) does occur: it is already how selecting a
preset works today, and #10 changes when it happens while #14 adds a new path that triggers it. It
changes what is live on the pedal, never what is stored.

Every change is view-model or view level, confined to `Namager.App`. `Sonulab.Core` needs no change:
`NodeSchema` already parses `def` (`NodeSchema.cs:14`); it is `ParameterFieldViewModel` that
currently discards it.

## Shared foundation: extract the amp detail control

Issues #9 and #14 both need the amp detail rendered somewhere it currently cannot go.

Today `Views/AmpDetailPanel.axaml` declares `x:DataType="vm:AmpListViewModel"` and interleaves three
unrelated states in one file — the upload form, the details card, and an empty-state placeholder.
The details card reads directly off `AmpListViewModel` (`Selected.Name`, `DetailsFields`,
`DetailsNotes`, `DetailsUrl`, `IsDetailsLoading`, …), so it cannot be shown anywhere that isn't the
Amps tab, and `AmpListViewModel` has accumulated the detail-loading state machine on top of list
management, upload, metadata editing, and reorder.

**Extract `AmpDetailViewModel`** owning exactly the detail concern:

- identity: `Name`, `DisplaySlot`
- content: `Fields` (`MetadataField`), `Notes`, `Url`
- state: `IsLoading`, `ShowNoMetadata`, `Error`
- new for #14: `UsedInPresets`, `UsageState`
- the existing `_detailsCache`, `_detailsCts` and `LoadDetailsCoreAsync` logic, moved verbatim —
  including the deliberate non-disposal of the superseded CTS, which is load-bearing and documented
  in place.

**Extract `Views/AmpDetailView.axaml`** bound to `AmpDetailViewModel`, containing only the details
card. `AmpListViewModel` keeps a `Detail` property; `AmpDetailPanel` keeps the upload form and
placeholder and hosts `AmpDetailView` for the details state. The preset editor hosts the same
control in a flyout.

This is the only structural change in the branch. It is scoped to the code the issues already
require touching, and it is what makes #9 and #14 small rather than duplicated.

## #9 — amp detail as a popup from the preset's amp block, copy-pasteable

A button beside the amp field in the parameter editor opens a non-modal `Flyout` whose content is
`AmpDetailView`, bound to an `AmpDetailViewModel` loaded for the amp that field references. It
dismisses on click-away, so auditioning a preset and checking whether its amp wants an IR is a
two-click round trip that never leaves the editor.

The amp field is a ref-sourced field (`RefSource == "root\amp"`), so the referenced amp is resolved
by name against the amp list — the same name-based reference the firmware itself uses.

For copy/paste, the metadata value `TextBlock`s become `SelectableTextBlock` (Avalonia 12 built-in;
no new dependency). Labels stay plain — the values are what anyone wants to copy. Because this lives
in the shared control, the Amps tab gets selectable metadata for free, which is the other half of
what the issue asks for.

Flyout content is loaded on open, not eagerly per selection: opening is an explicit user action, and
a metadata read per preset click would put device reads on the hot path of browsing presets.

## #10 — rapid preset switching desyncs the highlight

**Root cause.** `MainWindowViewModel.cs:217` subscribes to `PresetListViewModel.Selected` and fires
`editor.LoadForCommand.Execute(new PresetTarget(...))` — fire-and-forget. `LoadForAsync`
(`ParameterEditorViewModel.cs:164`) then does two sequential awaits: a `write root\app\preset` that
**activates the preset on the pedal**, followed by `LoadCoreAsync()` (browse + rebuild blocks).
Nothing serializes or supersedes these. Clicking three presets quickly starts three overlapping
loads, whose device writes and completions interleave arbitrarily.

This is worse than a cosmetic mismatch: the pedal's active preset ends up being whichever `write`
happened to land last, which need not be the highlighted one. The user hears the wrong preset, and
the codebase's own rule that serial commands must not interleave is violated.

**Fix — single-flight, latest-wins, inside `ParameterEditorViewModel`:**

- One load in flight at a time. A request arriving while one is running is stored in a single
  `_pending` slot, replacing whatever was already there — intermediate clicks are dropped, never
  queued and replayed.
- A monotonic generation counter guards completion: a load whose generation is stale applies
  nothing (no `PresetName`, no `_loadedName`, no block rebuild).
- When the in-flight load finishes, if `_pending` differs from what just loaded, it runs next.
- The existing catch-all stays: an escape here is an unhandled UI-thread rethrow, i.e. process
  death.

**Invariant:** when loading settles, the preset activated on the pedal and the preset shown in the
editor both equal the last one the user clicked.

The list stays interactive throughout — this is the "coalesce, last click wins" behavior, not the
issue's suggested "disable the list while loading", which would make quick browsing feel dead.

*Refinement worth stating explicitly:* the highlight remains the user's click rather than rewinding
to the confirmed load. Rewinding it would make the selection jump backwards under a fast clicker,
which is a worse lie than the one being fixed. The guarantee delivered is convergence: the editor
and the pedal both catch up to the highlight, and the editor shows its loading state until they do.

## #12 — Tone3000 detail panel clips options and labels

The detail pane is currently the fixed 320px right column of
`Grid ColumnDefinitions="*,320"` (`Tone3000View.axaml:97`), with the model list rendered as an
unbounded `ItemsControl` (line 169). A tone with many files grows that list past the viewport, and
the 320px width truncates long labels — exactly the
[Silvertone 1484](https://www.tone3000.com/tones/silvertone-1484-twin-twelve-70876) case in the
issue.

Move the detail to a **bottom-docked panel** spanning the full width, with a bounded height and its
own internal `ScrollViewer`. Full width kills the label truncation; the bounded, scrolling region
means an arbitrarily long file list can never push content out of view.

For per-file selection: a `ComboBox` of the tone's files with the selected file's actions beside it.
A dropdown holds an unbounded file count in fixed space, which is the property that matters here.
The list-vs-dropdown call was left to me; if the real file counts turn out to be small enough that a
dropdown adds a click for no benefit, an inline wrapped list is the fallback, decided against real
API responses during implementation rather than guessed here.

## #13 — EQ icon that lights when the EQ is not flat

Add `Icon.Equalizer` to `Icons.axaml` (a `StreamGeometry`, consistent with every other icon; no
third-party icon library — see the Avalonia 12 constraint in `CLAUDE.md`). Render it on the EQ
sub-group header in the parameter editor.

`SubGroupViewModel` currently holds only `Header` and `Fields`. It gains:

- `IconKey` — optional, so only groups that warrant an icon get one.
- `IsActive` — true when any field's value differs from its neutral, recomputed on field change.

**Neutral is the firmware default (`def`) from the browse schema**, not a hardcoded 0. `NodeSchema`
already parses it (`NodeSchema.cs:14`), but `ParameterFieldViewModel` currently discards it — it
keeps `Min`/`Max`/`Options`/`Kind` and drops `Def`. So `ParameterFieldViewModel` gains a
`Default` property carried through from the schema.

**Fallback, and why it is not a hedge:** where a field has no `def`, neutral is 0. This makes the
rule correct whether or not the firmware publishes `def` for the EQ nodes — see Unverified
assumptions.

Active uses `Sonulab.AccentBrush`, inactive `Sonulab.TextMutedBrush` — tokens, never hex literals.

## #14 — amp detail lists the presets using that amp

`AmpDetailViewModel` gains a "Used in presets" section fed by the existing `IPresetUsageService`,
formatted with the existing `PresetRefFormat.Join` so it reads identically to the tooltip and the
rename/delete block message.

Entries are clickable and navigate to the Presets tab and select that preset — which loads it on the
pedal, exactly as clicking it in the preset list does. To keep `AmpDetailViewModel` from reaching
into `MainWindowViewModel`, navigation goes through a one-method seam:

```csharp
public interface IPresetNavigator { void NavigateToPreset(int index, string name); }
```

`MainWindowViewModel` implements it; tests substitute a recording fake.

**Three usage states, not two.** The scan is asynchronous and may be incomplete:

| `UsageState` | Shown |
| --- | --- |
| `Checking` | "Checking preset usage…" |
| `Complete`, empty | "Not used by any preset." |
| `Complete`, non-empty | the clickable list |

Rendering an empty list while the scan is still running would read as "this amp is unused", which is
precisely the wrong thing to tell someone deciding whether to delete it. The distinction is
mandatory, not cosmetic.

## Testing

Every change is view-model level and unit-testable offline; no test needs hardware.

- **#10** is the one with real concurrency. `ParameterEditorViewModel` tests drive a gated fake
  client that blocks a load mid-flight, assert that N rapid requests produce one in-flight load plus
  one final load for the newest target, that intermediate targets are never activated on the device,
  and that a stale completion mutates nothing.
- **#13** covers: differs-from-`def` lights the icon, equal-to-`def` does not, missing-`def` falls
  back to zero, and the flag recomputes on field change.
- **#14** covers all three `UsageState` renderings and that clicking an entry calls the navigator
  with the right slot.
- **#9** covers that the flyout VM loads the amp named by the preset's amp field, and that an
  unresolvable name surfaces the error state rather than throwing.
- **#12** is layout; it gets a VM test for file selection and is verified visually.

The extraction in the shared foundation must be behavior-preserving: existing `AmpListViewModel`
detail tests move to `AmpDetailViewModel` with their assertions intact.

## Unverified assumptions

**The EQ nodes publish `def` in their browse schema — NOT verified on hardware.** The pedal is on
COM6 and healthy, but `VoidX-Control` (PID 29928) holds the port exclusively, and terminating it was
blocked by the permission classifier, so no browse dump could be taken this session. The design is
built so the answer cannot block it: `def` when present, literal 0 when absent. If it turns out the
EQ nodes carry no `def`, behavior silently degrades to exactly what issue #13 literally asked for.

Confirm with `dotnet run --project tools/HwCheck -- --browse` (read-only) once VoidX is closed, and
record the EQ nodes' `def` values.

## Hardware validation

Deferred to `docs/HARDWARE-VALIDATION-ui-issues-9-14.md`, written alongside the implementation. It
covers the `def` confirmation above, the #10 rapid-click behavior against the real pedal (the failure
mode is timing-dependent and the fake can only prove the logic), and a visual pass on #12 with the
Silvertone tone.

## Out of scope

**#11 — rename cascade.** Presets reference amps and IRs *by name*, so renaming one orphans every
preset pointing at the old name. Making the rename cascade means rewriting each affected preset on
the device, which is a guarded, backed-up, verified multi-write operation. It needs its own spec,
its own branch, and hardware validation with a full backup in hand. The current fail-closed guard —
refusing to rename an in-use amp or IR — stays exactly as it is until then.
