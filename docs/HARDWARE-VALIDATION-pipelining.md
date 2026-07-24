# Hardware validation — paced-overlap serial pipelining

**Feature:** `ISonuLink.SendBatchAsync` / `SerialSonuLink` paced overlap, used by
`SonuClient.DReadChunkRangeAsync` for every multi-chunk foreground read.
**Status:** NOT YET RUN. Until this checklist passes on the pedal, the speedup is
probe-evidenced (PROTOCOL.md, `--pipeline-probe`) but not product-validated.

**Preconditions:** VoidX-Control CLOSED. Pedal on USB (this feature is serial-only).
Take a full backup first — every step here is read-only, but the rule stands.

## How to switch it off

`src/Namager.App/ViewModels/MainWindowViewModel.cs` builds `SerialLinkOptions`. Add
`PipelineEnabled = false` to that initializer for the "before" runs, remove it for the "after"
runs. No other code change is needed.

## Checklist

- [ ] **1. Backup, pipelining OFF.** Back up all 30 preset slots. Record wall-clock time and the
      backup directory name.
- [ ] **2. Backup, pipelining ON.** Repeat. Record wall-clock time.
- [ ] **3. Byte-compare.** The two backup sets must be **identical**. Any difference is a hard
      stop — report it before going further.
      `fc /b <before>\<file> <after>\<file>` per file, or a directory diff tool.
- [ ] **4. Amp slot dump (96 chunks).** `dotnet run --project tools/HwCheck -- --dump-amps` dumps
      every occupied amp slot to `NAMFiles/VxampDump/`. Run once with pipelining OFF, move/rename
      that directory (e.g. `VxampDump-before`), run again with pipelining ON, then
      `fc /b VxampDump-before\<file> VxampDump\<file>` per file for at least one occupied slot.
- [ ] **5. IR slot dump (32 chunks).** `dotnet run --project tools/HwCheck -- --dump-irs` dumps
      every occupied IR slot to `NAMFiles/IrDump/`. Same before/after/rename/`fc /b` procedure as
      step 4.
- [ ] **6. Repair rate.** The file log target is already Debug by default (`src/Namager.App/Logging.cs`,
      `config.AddRule(LogLevel.Debug, LogLevel.Fatal, target)`) — no config change needed. Run a
      full backup, then open the log file (path printed at startup / `Logging.Configure()`'s return
      value, under `logs/namager.log` next to the app binary) and count `pipelined dread missed`
      lines. Above ~1 % of chunks means the 30 ms floor is too aggressive on this hardware — raise
      `PipelineMinPaceMs` to 35 or 40 and re-run steps 2–3.
- [ ] **7. Live-preset sanity.** With a preset selected and audible, run a backup with pipelining
      ON. Audio must not glitch and the pedal must stay responsive.
- [ ] **8. Record the numbers** in `docs/perf-findings.md` (before/after, per-chunk ms, repair
      rate) and mark this checklist done with the date.

## Expected

~57 ms/chunk → ~33 ms/chunk (~1.7×). A 30-slot preset backup is 1920 chunks: roughly 110 s → 63 s.
