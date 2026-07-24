# Spec — paced-overlap serial pipelining (bulk dread)

**Date:** 2026-07-24
**Source:** item #2 of `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`
**Branch:** `worktree-feat-serial-pipelining` (worktree; base `b0c4602`)
**Scope guard:** serial transport only. A parallel agent owns the dswap/usage-map work.

## Problem

Every bulk blob read is lockstep: one `dread` command, wait for the full response, send the next.
That costs ~57 ms/chunk. A 30-slot preset backup is 1920 chunks ≈ 110 s; an amp dump is 96
chunks/slot.

The `--pipeline-probe` run (2026-07-24, fw 2.5.1, recorded in PROTOCOL.md "dread limits &
hazards") established:

- Zero-gap command bursts are **dropped** — the firmware does not queue serial commands.
- The firmware **does** accept the next command while still streaming the previous response.
- A **≥30 ms send pace sustains ~33 ms/chunk** (vs ~57 lockstep, ~1.75×). **25 ms is the cliff** —
  commands get dropped below it.
- Pipelined chunks matched the lockstep ground truth byte-for-byte.

PROTOCOL.md's own guidance: production code should **self-clock** (send N+1 when the first bytes
of response N arrive) rather than hard-code 30 ms.

## Goal

Deliver that ~1.7× on foreground bulk reads — backups, amp/IR dumps, upload read-back verify,
restore/duplicate — without weakening the integrity guarantees of the current lockstep path, and
without touching the files the parallel agent owns.

## Scope

**In:**

- `src/Sonulab.Core/Transport/ISonuLink.cs` — new `SendBatchAsync` (default interface method)
- `src/Sonulab.Core/Transport/SerialSonuLink.cs` — the self-clocking implementation
- `src/Sonulab.Core/Transport/SerialLinkOptions.cs` — three new options
- `src/Sonulab.Core/SonuClient.cs` — `DReadChunkRangeAsync` only (+ a gated batch helper)
- `tests/Sonulab.Core.Tests/` — new `ScriptedSerialPort` double and its tests
- `docs/HARDWARE-VALIDATION-pipelining.md` — manual on-device checklist

**Out, explicitly:** `ReorderService`, `PresetUsageMap`, `PresetUsageService`,
`PresetListViewModel` (parallel agent owns these); `DeviceRepository`; `tools/HwCheck`;
`TcpSonuLink` and all WiFi paths; `DReadChunkRangeBackgroundAsync`.

## Design

### 1. Transport seam — `ISonuLink.SendBatchAsync`

```csharp
/// Sends N commands with overlapped timing and returns the raw response windows
/// collected, split at NUL boundaries, in arrival order.
///
/// RESPONSE-PRODUCING COMMANDS ONLY (dread). A silent command (write/dwrite) emits no
/// NUL and would shift every later window.
///
/// The returned list is NOT positionally guaranteed to align with `commands`: an
/// unsolicited meter record or a dropped response shifts it. Callers MUST identify each
/// response by its own content (for dread: ResponseParser.ChunkHex verifies both index
/// and chunk) rather than by list position.
Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    => SequentialFallbackAsync(commands, ct);
```

A **default interface method** whose fallback is a plain loop over `SendAsync`. Consequence:
`TcpSonuLink` (WiFi) and `FakeSonuLink` need no edits and keep exactly today's behavior. Only
`SerialSonuLink` overrides it.

The weak positional contract is deliberate. It converts the two realistic failure modes —
unsolicited meter records interleaved in the stream, and a wholly dropped response — from
"silently returns the wrong chunk's bytes" into "the caller doesn't find that chunk and repairs
it". Correctness rests on the record verification the caller already performs, not on counting
NULs.

### 2. `SerialSonuLink` — self-clocking overlap

Send command N+1 when **both** conditions hold:

1. the first byte of response N has arrived (self-clocking — adapts to a slow device), **and**
2. at least `PipelineMinPaceMs` has elapsed since the previous send (the floor that keeps us off
   the 25 ms cliff).

Read continuously into an accumulator; close a window at each NUL. Stop when the window count
reaches the command count, or at the deadline `MaxWaitMs + PipelineMinPaceMs × n`; on deadline,
return the windows completed so far (short list = the caller repairs the rest).

The port's input buffer is discarded once, before the first send — not per command, as the
lockstep path does, since a discard mid-batch would destroy in-flight responses.

`SerialLinkOptions` gains:

| Option | Default | Meaning |
| --- | --- | --- |
| `PipelineEnabled` | `true` | Kill switch. `false` → `SendBatchAsync` uses the sequential fallback, byte-identical to today's behavior. |
| `PipelineMinPaceMs` | `30` | Minimum gap between sends. The probe's proven pace; 25 ms is the cliff. |
| `PipelinePollMs` | `3` | Read-poll interval inside a batch. The lockstep `PollMs` of 10 is too coarse to land a 30 ms pace cleanly. |

For deterministic tests, `SerialSonuLink` gains optional ctor parameters
`Func<long>? tickSource, Func<int, CancellationToken, Task>? delay`, defaulting to
`Environment.TickCount64` and `Task.Delay`. This mirrors the seam `SonuClient` already uses for
its background lane, so the pattern is not new to the codebase.

### 3. `SonuClient.DReadChunkRangeAsync` — batch, verify, repair

```
count < 2                    -> unchanged single-command path
otherwise:
  windows = await SendBatchGatedAsync(dread commands for firstChunk..firstChunk+count-1)
  for each wanted chunk c:
      hex = first window w where ResponseParser.ChunkHex(w, index, c) is a valid even-length hex
  missing = chunks with no valid hex
  for each missing chunk (in order), up to RepairAttempts tries:
      re-read it through the existing lockstep SendAsync path
  assemble in chunk order; a chunk still missing contributes 0 bytes
```

- **Scan all windows, not window[i].** Kills the misalignment class of bug outright.
- **Repair is lockstep**, so a partial drop costs ~57 ms per bad chunk instead of forfeiting the
  batch. Budget: 2 attempts per chunk.
- **Permissive tail preserved.** A chunk that survives repair still missing contributes 0 bytes,
  exactly as today, so `SlotBlobService`'s validated wrappers keep failing loudly and their
  behavior is unchanged.
- **Gating.** A private `SendBatchGatedAsync` mirrors `SendAsync`'s bookkeeping: acquire `_gate`
  for the whole batch (one burst = one gate hold), stamp `_lastForegroundTicks` before and after,
  emit the same Trace-level timing line. The background lane's quiet-window semantics are
  therefore intact — pipelining happens *within* a burst; the lane still governs *between* bursts.
- The odd-length-hex guard from the current loop is retained (a torn record yields odd-length hex;
  treat as missing rather than letting `Convert.FromHexString` throw past every caller).

`DReadBlobAsync` gets the win for free — it already delegates to `DReadChunkRangeAsync`.

### 4. What is deliberately not accelerated

`DReadChunkRangeBackgroundAsync` is untouched. The preset-usage scan's head read
(`DeviceRepository.ReadPresetHeadAsync`) requests **one chunk per call** so it can stop the moment
the amp/IR refs are complete; batching it would require grouping requests, which over-reads up to
`batch-1` chunks past the stop point, changes the scan's yield-to-user latency, and edits
`DeviceRepository` — adjacent to the parallel agent's work. Recorded as a follow-up in the
next-steps doc instead.

## Testing

A **`ScriptedSerialPort`** test double lives in `tests/Sonulab.Core.Tests/` (production
`FakeSerialPort` is left alone). Driven by a virtual clock, it models:

- per-command service time (first-byte latency and response transmit time),
- responses emitted in fragments rather than atomically,
- a **drop rule** — e.g. "drop any command received less than 25 ms after the previous one",
  reproducing the firmware's real cliff,
- optional injection of an unsolicited meter record into the stream.

Cases:

| # | Case | Expectation |
| --- | --- | --- |
| 1 | Batch of N well-behaved commands | N windows, in order, each parseable |
| 2 | Pace floor | No two sends closer than `PipelineMinPaceMs` on the virtual clock |
| 3 | Genuinely self-clocking | With a slow-responding device, sends wait for the first byte — not a fixed sleep |
| 4 | `PipelineEnabled=false` | Behavior and command sequence identical to N × `SendAsync` |
| 5 | Deadline | A device that stops answering yields a short window list, no hang |
| 6 | Cancellation | `ct` mid-batch throws `OperationCanceledException` promptly |
| 7 | Drop → repair | Port drops one command; `DReadChunkRangeAsync` still returns the complete blob |
| 8 | Meter-record misalignment | Injected meter record shifts window positions; all chunks still found |
| 9 | Torn / odd-length hex | Treated as missing, repaired, blob complete |
| 10 | Unrepairable chunk | Contributes 0 bytes; short buffer reaches the caller (today's contract) |
| 11 | Background lane | A background send cannot interleave mid-batch (extends `SonuClientBackgroundLaneTests`) |
| 12 | `count == 1` | Takes the unchanged single-command path |

The existing 648 tests must stay green. The `FakeSonuLink`-based suites exercise the default
interface fallback, so a regression there would show up immediately.

## Hardware validation

`docs/HARDWARE-VALIDATION-pipelining.md` — a manual checklist, run once on the pedal:

1. One full 30-slot preset backup with `PipelineEnabled=false`; record wall-clock.
2. The same backup with `PipelineEnabled=true`; record wall-clock.
3. **Byte-compare the two backup sets — they must be identical.**
4. Repeat for one amp slot dump (96 chunks) and one IR slot dump (32 chunks).
5. Log the repair count observed at Debug level; a repair rate above roughly 1 % means the pace
   floor is too aggressive on this hardware and should be raised.
6. File before/after numbers in `docs/perf-findings.md`.

Until this checklist has been run, the feature is unvalidated on-device — the same status
PROTOCOL.md's probe numbers carry.

## Risks

| Risk | Mitigation |
| --- | --- |
| A slower USB hub / different cable drops at 30 ms | `PipelineMinPaceMs` is an option; raise it. Repair keeps results correct meanwhile. |
| Meter records shift window alignment | Callers identify responses by content; alignment is never trusted. |
| A regression appears late, on-device | `PipelineEnabled=false` restores the old path with a one-line change and no code edit. |
| Merge conflict with the parallel agent | Only `SonuClient.DReadChunkRangeAsync` is shared ground; the dswap work adds new methods elsewhere in the file. |

## Success criteria

- All existing tests green, plus the 12 new cases.
- A 64-chunk foreground blob read issues one batch and, absent drops, no lockstep repairs.
- `PipelineEnabled=false` produces a command sequence identical to today's.
- Hardware checklist written; the perf numbers recorded once it is run.
