# Performance Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measurably faster Connect and toolbar reorder, and a settled, timed verdict on PROTOCOL.md's preset-content-dwrite OPEN QUESTION — every change bracketed by before/after hardware timings in `docs/perf-findings.md`.

**Architecture:** Instrument the connect path with PERF-prefixed NLog lines and capture a hardware baseline; probe preset dwrite through the existing `SlotBlobService` with a presets `SlotBlobKind` (guarded, empty-slot-only, timed); then three small fixes — lazy Amps/IRs tab loading via an `EnsureTabLoaded` seam, an adaptive open-settle achieved by retuning `SerialLinkOptions` (the probe-retry loop in `SonuConnector` already exists), and pointing the toolbar Move buttons at the lean `MoveStepAsync` path — then re-measure.

**Tech Stack:** .NET 10, NLog (existing), xUnit; no new packages.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-04-performance-pass-design.md`. Out of scope (do NOT build): applying dwrite to restore/duplicate/rollback, editor-load work, duplicate-speed work, UI changes beyond invisible loading behavior.
- **Task 2 is a VERDICT gate, not a STOP gate** — works+fast / works+slow / refuses are all valid outcomes; each has a prescribed PROTOCOL.md text. **Task 4 has an escalate clause**: if the Task 1 baseline shows open+settle+probes do NOT dominate connect, STOP and report the surprise instead of building Task 4.
- Hardware steps need the pedal connected and VoidX-Control CLOSED. Timings go into `docs/perf-findings.md`; PERF log lines go to `logs/sonulab.log` at Info (per-command Trace timing already exists).
- All commands from repo root `C:\Development\Buckdrivers\Sonulab\StompStationManager` (PowerShell). Suite green after every task (~271 tests today). Commit prefix `perf:`. HwCheck is NOT in the solution — build it explicitly when touched.
- Reference files (read before the task that touches them): `src/Sonulab.Core/Connection/SonuConnector.cs` + `DeviceSession.cs`, `src/Sonulab.Core/Transport/SerialSonuLink.cs` + `SerialLinkOptions.cs`, `src/Sonulab.App/ViewModels/MainWindowViewModel.cs` + `ConnectionViewModel.cs` + `PresetListViewModel.cs`, `src/Sonulab.Core/Services/ReorderService.cs` (the lean vs backup paths) + `SlotBlobService.cs`, `tools/HwCheck/Program.cs` (amp/IR upload blocks as the probe pattern).

## File Structure

```
src/Sonulab.Core/Connection/SonuConnector.cs        (Task 1 PERF logs; Task 4 none — options-only)
src/Sonulab.Core/Connection/DeviceSession.cs        (Task 1 PERF log around compat check)
src/Sonulab.App/ViewModels/MainWindowViewModel.cs   (Task 1 PERF logs; Task 3 lazy loading + EnsureTabLoaded; Task 4 options retune)
src/Sonulab.App/Views/MainWindow.axaml.cs           (Task 3 nav hook)
tools/HwCheck/Program.cs                            (Task 2 --preset-dwrite-probe)
PROTOCOL.md                                         (Task 2 verdict replaces the OPEN QUESTION)
src/Sonulab.App/ViewModels/PresetListViewModel.cs   (Task 5 toolbar -> MoveStepAsync)
tests/Sonulab.App.Tests/MainWindowViewModelTests.cs (Task 3, new)
tests/Sonulab.Core.Tests/SonuConnectorTests.cs      (Task 4, new or extend if exists)
tests/Sonulab.App.Tests/PresetListViewModelTests.cs (Task 5, extend)
docs/perf-findings.md                               (Task 1 baseline; Task 6 final table)
CLAUDE.md                                           (Task 6)
```

---

### Task 1: Connect-phase instrumentation + hardware BASELINE

**Files:**
- Modify: `src/Sonulab.Core/Connection/SonuConnector.cs`, `src/Sonulab.Core/Connection/DeviceSession.cs`, `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`
- Create: `docs/perf-findings.md` (baseline section)

**Interfaces:**
- Produces: PERF-prefixed NLog Info lines (`grep PERF logs/sonulab.log` shows the whole connect breakdown) and the baseline numbers Tasks 4 and 6 depend on. No public API changes.

- [ ] **Step 1: Instrument SonuConnector**

Add `private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();` and stopwatches (mirroring `ReorderService`'s logging style):

```csharp
                var swOpen = System.Diagnostics.Stopwatch.StartNew();
                await link.OpenAsync(ct);
                swOpen.Stop();
                var swProbe = System.Diagnostics.Stopwatch.StartNew();
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    // First command after open is often lost to the ESP32 reset — retry.
                    var resp = await link.SendAsync(@"read root\sys\_name", ct);
                    bool ok = ResponseParser.NonMeterRecords(resp)
                        .Any(r => NodeRecord.TryParse(r, out var nr) && nr.Path == @"root\sys\_name");
                    if (ok)
                    {
                        Log.Info("PERF connect open+settle={0}ms probes={1}ms attempts={2} port={3}",
                            swOpen.ElapsedMilliseconds, swProbe.ElapsedMilliseconds, attempt + 1, port);
                        return link;
                    }
                    if (attempt + 1 < attempts) await Task.Delay(retryDelay, ct);
                }
```

- [ ] **Step 2: Instrument DeviceSession**

Around `await _checker.CheckAsync(Client, ct)`:

```csharp
            var swCompat = System.Diagnostics.Stopwatch.StartNew();
            var compat = await _checker.CheckAsync(Client, ct);
            Log.Info("PERF connect compat={0}ms", swCompat.ElapsedMilliseconds);
```

(with the same static NLog logger field added to the class).

- [ ] **Step 3: Instrument the Connected handler**

In `MainWindowViewModel`, the handler currently fire-and-forgets three refreshes. Make the timing observable by awaiting them in a timed async local function (still fire-and-forget from the event's perspective — behavior is unchanged, ordering is now deterministic presets→amps→irs, which the serialized link forced anyway):

```csharp
        _connection.Connected += (_, _) =>
        {
            // ... existing VM construction unchanged (presets, editor, amps, irs) ...
            _ = LoadInitialAsync(presets, amps, Irs!);
        };

        async Task LoadInitialAsync(PresetListViewModel presets, AmpListViewModel amps, IrListViewModel irs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await presets.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect presets-list={0}ms", sw.ElapsedMilliseconds);
            sw.Restart();
            await amps.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect amps-list={0}ms", sw.ElapsedMilliseconds);
            sw.Restart();
            await irs.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect irs-list={0}ms", sw.ElapsedMilliseconds);
        }
```

(add the static NLog logger field; Task 3 will replace the amps/irs lines with lazy loading — this task only makes the baseline measurable).

- [ ] **Step 4: Build + full suite**

Run: `dotnet build` then `dotnet test` — all green (~271; instrumentation must not change behavior).

- [ ] **Step 5: HARDWARE BASELINE (pedal connected, VoidX-Control CLOSED)**

1. `dotnet run --project src/Sonulab.App` → Connect → wait for all three lists → close the app.
2. `Select-String -Path logs\sonulab.log -Pattern 'PERF connect' | Select-Object -Last 6` — record every phase.
3. Reorder baseline: reconnect, select an occupied preset with an occupied neighbour below, click the TOOLBAR Move Down button once, then the per-row arrow once (undo). Then:
   `Select-String -Path logs\sonulab.log -Pattern 'MoveAsync|MoveStep' | Select-Object -Last 6` — record the `backed up N slot(s) in Xms` + path time (toolbar) vs the `MoveStep ... completed in Xms` (arrow).
4. Write `docs/perf-findings.md`:

```markdown
# Performance findings — baseline (2026-07-04, fw 2.5.1, COM6)

## Connect phases (before fixes)
| phase | ms |
|---|---|
| open+settle | <measured> |
| probes (n attempts) | <measured> |
| compat | <measured> |
| presets-list | <measured> |
| amps-list | <measured> |
| irs-list | <measured> |
| **total to fully loaded** | <sum> |

## Reorder single step (before fixes)
| path | ms |
|---|---|
| toolbar (MoveAsync: backup + rotate) | <measured> (backup <x>ms + path <y>ms) |
| per-row arrow (MoveStepAsync lean) | <measured> |

(after-fix table added by the final task)
```

Fill every `<measured>` from the log — no placeholders may remain in the committed file.

- [ ] **Step 6: Commit**

```powershell
git add src docs/perf-findings.md
git commit -m "perf: connect-phase PERF instrumentation + hardware baseline"
```

---

### Task 2: `--preset-dwrite-probe` + the VERDICT (hardware)

Settles PROTOCOL.md's OPEN QUESTION (lines ~70-74) with a timed, guarded experiment. Read that PROTOCOL.md section and the `--upload-ir` HwCheck block first.

**Files:**
- Modify: `tools/HwCheck/Program.cs` (one guarded block), `PROTOCOL.md` (verdict replaces the OPEN QUESTION)

**Interfaces:**
- Consumes: `SlotBlobService` + `SlotBlobKind` (construct an ad-hoc presets kind — presets are NOT added to the public kinds; this is a probe, not a feature), `SonuClient.DReadBlobAsync`.
- Produces: CLI `--preset-dwrite-probe [--src <idx>] [--dst <idx>]`; `RESULT: PRESET-DWRITE-PROBE WORKS (...)` or `... FAILED (...)`; the PROTOCOL.md verdict.

- [ ] **Step 1: Implement the probe block**

Next to the amp/IR upload blocks (distinct top-level variable names — CS0136):

```csharp
// --preset-dwrite-probe [--src <idx>] [--dst <idx>] : guarded, TIMED re-test of the 2026-06-15
// "preset content is not dwrite-able" verdict, which used the buggy all-zeros chunk:-1 terminator
// (the amp-upload bug). Dreads an occupied preset (source untouched), dwrites it into an EMPTY
// slot with the correct name-at-chunk:-1 commit via SlotBlobService (ACK-checked + verified),
// then deletes the probe slot. Either outcome is a valid verdict for PROTOCOL.md.
int pdp = Array.IndexOf(args, "--preset-dwrite-probe");
if (pdp >= 0)
{
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    var pClient = session.Client!;
    var pNames = await pClient.ReadListAsync(@"root\presets");
    int pSrc = ArgAfter(args, "--src") ?? Enumerable.Range(0, pNames.Count).First(i => !string.IsNullOrEmpty(pNames[i]));
    int pDst = ArgAfter(args, "--dst") ?? Enumerable.Range(0, pNames.Count).First(i => string.IsNullOrEmpty(pNames[i]));
    if (string.IsNullOrEmpty(pNames[pSrc]) || !string.IsNullOrEmpty(pNames[pDst]))
    { Console.WriteLine($"RESULT: PRESET-DWRITE-PROBE ABORT — need occupied src (idx {pSrc}) and empty dst (idx {pDst})."); session.Disconnect(); return 1; }

    Console.WriteLine($"\n--- PRESET DWRITE PROBE: '{pNames[pSrc]}' (idx {pSrc}) -> empty idx {pDst} ---");
    var pSw = System.Diagnostics.Stopwatch.StartNew();
    var pBlob = await pClient.DReadBlobAsync(@"root\presets", pSrc, 64);
    Console.WriteLine($"[dread] source read: {pBlob.Length} B in {pSw.ElapsedMilliseconds}ms");

    var pKind = new SlotBlobKind(@"root\presets", 64, 8192, "Preset", "preset-probe", ".bin");
    var pSvc = new SlotBlobService(pClient, pKind,
        System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")),
        msg => new InvalidOperationException(msg));
    try
    {
        pSw.Restart();
        await pSvc.UploadAsync(pDst, pBlob, "__probe_dwrite", new Progress<SlotUploadProgress>(pp =>
        {
            if (pp.Stage == SlotUploadStage.Writing && (pp.ChunksDone % 16 == 0 || pp.ChunksDone >= pp.ChunksTotal))
                Console.WriteLine($"[chunk] {pp.ChunksDone}/{pp.ChunksTotal}");
        }));
        long pUploadMs = pSw.ElapsedMilliseconds;
        var pAfter = await pClient.ReadListAsync(@"root\presets");
        bool pLanded = pAfter[pDst] == "__probe_dwrite";
        Console.WriteLine($"[verify] service verified byte-equality; name landed: {pLanded}");
        pSw.Restart();
        await pSvc.DeleteAsync(pDst);
        Console.WriteLine($"[cleanup] probe slot deleted in {pSw.ElapsedMilliseconds}ms");
        Console.WriteLine(pLanded
            ? $"RESULT: PRESET-DWRITE-PROBE WORKS — 66 acked writes + verify in {pUploadMs}ms (compare: select+save copy ~216ms, param replay ~12s)"
            : $"RESULT: PRESET-DWRITE-PROBE FAILED — all writes ACKed but the name-table entry did not land");
        session.Disconnect();
        return pLanded ? 0 : 4;
    }
    catch (InvalidOperationException pex)
    {
        Console.WriteLine($"RESULT: PRESET-DWRITE-PROBE FAILED — {pex.Message}");
        // Best-effort cleanup if a partial name landed (service clears on verify-fail already).
        try { await pSvc.DeleteAsync(pDst); } catch { }
        session.Disconnect();
        return 4;
    }
}
```

with the small helper (place it with HwCheck's other locals, once):

```csharp
static int? ArgAfter(string[] a, string flag)
{
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : null;
}
```

Note: `SlotBlobService.UploadAsync` skips the backup dread for the empty dst (its occupied-check reads the name table) and performs the ACK-checked 0→1..64→-1 sequence + settle + verify + honest clear — exactly the experiment, for free. The service's verify FAILURE path clears the slot; the "ACKed but content zeros" outcome of 2026-06-15 would surface as its verify exception with the first-diff offset.

- [ ] **Step 2: Build + run the probe (hardware)**

`dotnet build tools/HwCheck` → clean. Then:
`dotnet run --project tools/HwCheck -- --preset-dwrite-probe`
Capture the FULL output. Any of the three verdicts is success for this task; only a crash/exception outside the RESULT contract is a defect.

- [ ] **Step 3: Update PROTOCOL.md**

Replace the OPEN QUESTION paragraph (the lines beginning "OPEN QUESTION (2026-07-03, after the amp-upload fix)") with ONE of:

If WORKS:
```markdown
- VERDICT (2026-07-04, --preset-dwrite-probe, fw 2.5.1 serial): preset content IS dwrite-able with
  the correct sequence (chunk:0 name → chunks 1..64 → name at chunk:-1 = commit) — the 2026-06-15
  test failed only because it used the zeros terminator. Measured: <N> ms for the 66-write upload
  + verify (vs ~216 ms select+save copy, ~12 s param replay). select+save remains the reorder copy
  engine (faster); dwrite is the byte-exact option for restore/duplicate (follow-up, not built).
```

If FAILED (either failure mode):
```markdown
- VERDICT (2026-07-04, --preset-dwrite-probe, fw 2.5.1 serial): preset content is NOT dwrite-able
  even with the correct name-at-chunk:-1 commit (<one-line failure detail from the probe output>).
  The 2026-06-15 conclusion stands on its own merits; presets persist only via save-from-live.
```

Fill `<N>`/`<detail>` from the probe output — no angle-bracket markers in the committed file.

- [ ] **Step 4: Commit**

```powershell
git add tools/HwCheck PROTOCOL.md
git commit -m "perf: preset-dwrite probe run - PROTOCOL.md OPEN QUESTION settled with timed verdict"
```

---

### Task 3: Lazy Amps/IRs tab loading

**Files:**
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`, `src/Sonulab.App/Views/MainWindow.axaml.cs`
- Test: `tests/Sonulab.App.Tests/MainWindowViewModelTests.cs` (new)

**Interfaces:**
- Produces: `public void EnsureTabLoaded(int navIndex)` on `MainWindowViewModel` (0=Presets, 1=Amps, 2=IRs — the nav ListBox order) and `public int CurrentNavIndex` (set by the view on every nav change). Amps/IRs refresh exactly once, on first visit.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.App.Tests/MainWindowViewModelTests.cs`:

```csharp
using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class MainWindowViewModelTests
{
    private static AmpListViewModel AmpVm(out FakeAmpDevice dev)
    {
        dev = new FakeAmpDevice();
        dev.SeedAmp(0, "A", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), Path.Combine(Path.GetTempPath(), "mwvm-t"), 0, 0);
        return new AmpListViewModel(svc, writesAllowed: true);
    }

    [Fact]
    public void EnsureTabLoaded_refreshes_amps_once_on_first_visit_only()
    {
        var vm = new MainWindowViewModel();
        vm.Amps = AmpVm(out var dev);
        int Reads() => dev.CommandLog.Count(c => c == @"read root\amp");

        Assert.Equal(0, Reads());                 // constructing the VM must not read the device
        vm.EnsureTabLoaded(1);
        Assert.Equal(1, Reads());                 // first visit loads
        vm.EnsureTabLoaded(1);
        vm.EnsureTabLoaded(0);
        vm.EnsureTabLoaded(1);
        Assert.Equal(1, Reads());                 // revisits do not reload (manual Refresh still can)
    }

    [Fact]
    public void EnsureTabLoaded_ignores_missing_vms_and_presets_index()
    {
        var vm = new MainWindowViewModel();
        vm.EnsureTabLoaded(0);                    // presets tab: no-op here (eager elsewhere)
        vm.EnsureTabLoaded(1);                    // Amps is null before connect: must not throw
        vm.EnsureTabLoaded(2);
    }
}
```

(Constructing `MainWindowViewModel` in a test is safe: its ctor only enumerates port names and wires events; nothing opens a port until Connect. `EnsureTabLoaded` fire-and-forgets the async refresh — with `FakeAmpDevice`'s synchronous `Task.FromResult` completions the command completes before the assertion; keep the fake synchronous.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.App.Tests --filter MainWindowViewModelTests`
Expected: FAIL — `EnsureTabLoaded` not defined.

- [ ] **Step 3: Implement**

`MainWindowViewModel.cs`:
- Add fields/members:

```csharp
    private bool _ampsLoaded, _irsLoaded;

    /// <summary>Set by the view on every nav change; lets the Connected handler lazy-load
    /// whichever tab the user is already looking at.</summary>
    public int CurrentNavIndex { get; set; }

    /// <summary>Lazy tab loading (perf spec §3): Amps/IRs fetch their device lists on FIRST
    /// visit instead of at connect — removes two full list reads from the connect path.</summary>
    public void EnsureTabLoaded(int navIndex)
    {
        if (navIndex == 1 && Amps is { } a && !_ampsLoaded) { _ampsLoaded = true; _ = a.RefreshCommand.ExecuteAsync(null); }
        else if (navIndex == 2 && Irs is { } i && !_irsLoaded) { _irsLoaded = true; _ = i.RefreshCommand.ExecuteAsync(null); }
    }
```

- In the `Connected` handler: reset `_ampsLoaded = _irsLoaded = false;` (fresh VMs per connect), REMOVE the amps/irs awaits from `LoadInitialAsync` (Task 1's version) so it times/loads presets only, and end the handler with `EnsureTabLoaded(CurrentNavIndex);` (covers connecting while already on the Amps/IRs tab).

`MainWindow.axaml.cs` `OnNavSelectionChanged` — append:

```csharp
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CurrentNavIndex = NavList.SelectedIndex;
            vm.EnsureTabLoaded(NavList.SelectedIndex);
        }
```

- [ ] **Step 4: Run tests + full suite**

Run: `dotnet test tests/Sonulab.App.Tests --filter MainWindowViewModelTests` → PASS (2). Full suite green.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "perf: lazy Amps/IRs tab loading - two device list reads leave the connect path"
```

---

### Task 4: Adaptive open-settle (options retune) — CONDITIONAL on the Task 1 baseline

**Escalate clause:** first read the Task 1 baseline in `docs/perf-findings.md`. If `open+settle + probes` is NOT the largest connect cost, STOP and report the surprise (status BLOCKED with the numbers) — do not build this task.

**Files:**
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs` (the `SerialLinkOptions` literal)
- Test: `tests/Sonulab.Core.Tests/SonuConnectorTests.cs` (new, or extend the existing connector tests if a file already covers `SonuConnector` — check first)

**Interfaces:**
- No API changes. The mechanism: `SonuConnector`'s existing probe-retry loop becomes the settle — a short initial settle plus more, faster probe attempts within a similar worst-case budget. The device connects as soon as it boots instead of always paying 1500 ms.

- [ ] **Step 1: Write the failing/guard test**

`tests/Sonulab.Core.Tests/SonuConnectorTests.cs` (check for an existing connector test file first and extend it instead if present):

```csharp
using System.Text;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

public class SonuConnectorTests
{
    /// <summary>Port that ignores probes until the Nth attempt — models the ESP32 still booting.</summary>
    private sealed class BootingPort(int readyOnAttempt) : ISerialPortStream
    {
        private int _writes;
        private byte[] _pending = Array.Empty<byte>();
        public bool IsOpen { get; private set; }
        public void Open(string portName, int baudRate) => IsOpen = true;
        public void Close() => IsOpen = false;
        public int BytesToRead => _pending.Length;
        public void DiscardInBuffer() { }
        public void Write(byte[] buffer, int offset, int count)
        {
            if (buffer[0] == 0) return;                       // the NUL terminator write
            _writes++;
            _pending = _writes >= readyOnAttempt
                ? Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n\0")
                : Array.Empty<byte>();
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _pending.Length);
            Array.Copy(_pending, 0, buffer, offset, n);
            _pending = _pending[n..];
            return n;
        }
    }

    [Fact]
    public async Task Connects_once_the_device_answers_even_if_early_probes_are_lost()
    {
        var options = new SerialLinkOptions
        { OpenSettleMs = 0, ProbeAttempts = 8, ProbeRetryDelayMs = 1, FirstByteTimeoutMs = 20, PollMs = 1 };
        var connector = new SonuConnector(() => new BootingPort(readyOnAttempt: 4), options);
        var link = await connector.ConnectAsync(new[] { "COMX" }, new[] { 115200 });
        Assert.NotNull(link);                                  // attempt 4 of 8 succeeded
    }

    [Fact]
    public async Task Gives_up_when_the_device_never_answers()
    {
        var options = new SerialLinkOptions
        { OpenSettleMs = 0, ProbeAttempts = 3, ProbeRetryDelayMs = 1, FirstByteTimeoutMs = 20, PollMs = 1 };
        var connector = new SonuConnector(() => new BootingPort(readyOnAttempt: 99), options);
        Assert.Null(await connector.ConnectAsync(new[] { "COMX" }, new[] { 115200 }));
    }
}
```

(If `ISerialPortStream` has members not listed here, read `src/Sonulab.Core/Transport/ISerialPortStream.cs` and implement them trivially.)

- [ ] **Step 2: Run — these may already pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SonuConnectorTests`
The retry loop already exists, so these are characterization tests and may pass immediately. That is fine — they pin the mechanism the retune depends on. If they fail, the fake port is mis-modeling `SendAsync`'s read loop — fix the fake, not the connector.

- [ ] **Step 3: Retune the options**

In `MainWindowViewModel`:

```csharp
        // Adaptive settle (perf spec §4): instead of always paying a fixed 1500 ms for the
        // ESP32's post-open reboot, wait briefly and let the probe-retry loop find the moment
        // the device answers. Worst case ≈ 250 + 8×(300 fail-fast + 150 delay) ≈ 3.9 s (old:
        // 1500 + 3×(300+300) ≈ 3.3 s); typical case = actual boot time + ≤450 ms overshoot.
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
```

- [ ] **Step 4: Full suite + hardware check**

`dotnet test` green. Then on hardware: `dotnet run --project src/Sonulab.App`, Connect, and check `Select-String logs\sonulab.log -Pattern 'PERF connect open'` — record the new `open+settle`/`probes`/`attempts` numbers (they feed Task 6). Connect must still succeed reliably — run Connect THREE times (disconnect/reconnect); if any attempt fails to find the device, the tuning is too aggressive: raise `OpenSettleMs` to 500 and retest before considering other values.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "perf: adaptive open-settle - probe-poll replaces most of the fixed 1500ms wait"
```

---

### Task 5: Toolbar reorder → lean MoveStepAsync

**Files:**
- Modify: `src/Sonulab.App/ViewModels/PresetListViewModel.cs` (MoveUpAsync/MoveDownAsync bodies)
- Test: `tests/Sonulab.App.Tests/PresetListViewModelTests.cs` (extend)

**Interfaces:**
- No API changes. The toolbar buttons take the same lean path as the per-row arrows; `ReorderService.MoveAsync` remains (device-full fallback inside `SwapAdjacentAsync`, future multi-slot moves).

- [ ] **Step 1: Write the failing test (append to PresetListViewModelTests.cs)**

```csharp
    // Counts content reads so we can prove the toolbar move is content-free (perf spec §5).
    sealed class DreadCountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public DreadCountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public System.Threading.Tasks.Task OpenAsync(System.Threading.CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public System.Threading.Tasks.Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Toolbar_move_reads_no_preset_content()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var link = new DreadCountingLink(dev);
        var repo = new DeviceRepository(new SonuClient(link));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);           // toolbar path
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, link.Dreads);                          // lean: zero content reads
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.App.Tests --filter PresetListViewModelTests`
Expected: the new test FAILS on `Dreads` (MoveAsync backs up the range by dreading); existing tests pass.

- [ ] **Step 3: Implement**

In `PresetListViewModel`, change the two toolbar command bodies (selection-follow preserved):

```csharp
    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} up…", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { } s && s.Index < Items.Count - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} down…", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }
```

(Also gate like the per-row commands do: `MoveStepAsync` throws on an EMPTY selected slot where `MoveAsync` did too — behavior unchanged; the existing empty-guard patterns in the per-row commands may be mirrored if the existing tests demand it — run them and match.)

- [ ] **Step 4: Run tests + full suite**

Run: `dotnet test tests/Sonulab.App.Tests --filter PresetListViewModelTests` → ALL PASS including the pre-existing `MoveDown_moves_selected_and_reloads` (the lean swap produces the same visible result). Full suite green.

- [ ] **Step 5: Commit**

```powershell
git add src tests
git commit -m "perf: toolbar Move Up/Down use the lean content-free MoveStepAsync path"
```

---

### Task 6: AFTER measurement + docs (hardware)

**Files:**
- Modify: `docs/perf-findings.md` (after table + conclusions), `CLAUDE.md`

- [ ] **Step 1: Hardware after-run**

Same procedure as Task 1 Step 5 (connect → grep PERF; toolbar move + arrow move → grep MoveStep). Note: amps-list/irs-list no longer appear at connect — visit each tab once and record their first-visit times instead.

- [ ] **Step 2: Complete docs/perf-findings.md**

Append:

```markdown
## Connect phases (after fixes)
| phase | before ms | after ms |
|---|---|---|
| open+settle | <b> | <a> |
| probes | <b> | <a> |
| compat | <b> | <a> |
| presets-list | <b> | <a> |
| amps-list | <b> | (lazy — first tab visit: <a>) |
| irs-list | <b> | (lazy — first tab visit: <a>) |
| **total to usable (presets shown)** | <b> | <a> |

## Reorder single step (after)
| path | before ms | after ms |
|---|---|---|
| toolbar | <b> (MoveAsync) | <a> (MoveStepAsync) |
| per-row arrow | <b> | <a> (unchanged path) |

## Preset-dwrite verdict
<one-paragraph recap of the Task 2 verdict + timing, pointing at PROTOCOL.md>

## Conclusions
<2-3 sentences: what got faster and by how much; anything that surprised us>
```

Fill every cell from the logs — no angle-bracket markers in the committed file.

- [ ] **Step 3: CLAUDE.md**

- HwCheck args: append `| --preset-dwrite-probe`.
- Test count: update to the measured total.
- In `## Protocol essentials`, if the Task 2 verdict was WORKS, adjust the "Writing a preset = save-from-live, not dwrite-of-content" line to note dwrite also works (see PROTOCOL.md verdict) but save-from-live remains the copy engine. If FAILED, leave it.

- [ ] **Step 4: Full verification + commit**

`dotnet build` + `dotnet test` green; `dotnet build tools/HwCheck` clean.

```powershell
git add docs CLAUDE.md
git commit -m "perf: after-measurements - before/after table and conclusions"
```

---

## Definition of done (from the spec)

`docs/perf-findings.md` shows a real before/after hardware table with Connect measurably faster
(lazy tabs + adaptive settle) and toolbar moves on the lean path; PROTOCOL.md's OPEN QUESTION is
replaced by a timed verdict either way; full suite green.
