# Preset-Usage Scan Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the "which presets use this amp/IR" feature actually work on hardware: fix the
empty-map correctness bug (path-matching), cut the per-preset read from 64 chunks to ≤32
(windowed head read), and run the scan as a non-blocking background task with progressive
highlight fill — while never letting scan reads interleave with user-initiated device bursts.

**Architecture:** Three layers. (1) `PresetUsageMap.Build` extracts amp/IR references by **node
path** (`root\app\amp\amp`, `root\app\ir\…\ir`) instead of the schema `ref` field that real
`dread` documents never carry. (2) `SonuClient` gains a **background lane**
(`SendBackgroundAsync`): background commands run only after the link has been foreground-quiet
for ≥1 s, so a scan dread can never land inside a user write burst (slot-26 hazard class);
`DeviceRepository.ReadPresetHeadAsync` uses it to read only chunks 1..≤32, stopping as soon as
the amp + both IR reference lines are complete. (3) `PresetUsageService` becomes a background
scanner exposing `Current` (partial map) + `MapUpdated` events + `EnsureCompleteAsync()` for the
delete/rename guards (fail-closed); the Amp/IR list VMs apply highlights progressively and never
hold `IsBusy` for the scan.

**Tech Stack:** .NET 10, xUnit, CommunityToolkit.Mvvm. No new dependencies.

**Reference docs:** `docs/superpowers/2026-07-24-preset-usage-scan-perf-handoff.md` (root cause +
probe verdicts), `docs/superpowers/specs/2026-07-23-preset-usage-guard-design.md` (original
feature spec), `PROTOCOL.md` ("dread limits & hazards" section).

## Global Constraints

- `src/Sonulab.Core` stays UI-free (no Avalonia/App references).
- New constructor parameters must be optional (`= null` / defaulted) so existing tests compile.
- Existing test baseline: **619 tests, all green** — every task ends green; new tests add to it.
- Serial-link safety: scan reads must NEVER interleave with a user-initiated command burst.
  The documented hazard (HwCheck finding, slot-26 incident 2026-07-06): a dread burst overlapping
  a dwrite burst silently discards the commit. The largest legitimate intra-burst foreground gap
  is AmpService's 750 ms settle before verify → background quiet window = **1000 ms**.
- Guards stay fail-closed: an unresolved/failed usage map must BLOCK delete/rename, never allow it.
- Do NOT touch `.axaml` views — `UsedInPresets`/`IsUsed`/`UsedInTooltip` bindings already exist.
- The firmware crash hazard (PROTOCOL.md): `dread` chunk values must always be plain integers.
- Commit after every task (conventional-commits style, `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`).

## File Structure

| File | Responsibility |
|---|---|
| `src/Sonulab.Core/Services/PresetUsageMap.cs` | pure map; path-based ref extraction; `HeadComplete` |
| `src/Sonulab.Core/SonuClient.cs` | + background lane (`SendBackgroundAsync`, bg dread/list helpers) |
| `src/Sonulab.Core/Services/DeviceRepository.cs` | + `ReadPresetHeadAsync` (windowed), `ListPresetsBackgroundAsync` |
| `src/Namager.App/Services/PresetUsageService.cs` | background scanner; new `IPresetUsageService` |
| `src/Namager.App/ViewModels/AmpListViewModel.cs` | progressive highlights; fail-closed guards |
| `src/Namager.App/ViewModels/IrListViewModel.cs` | mirror of the amp changes |
| `src/Namager.App/ViewModels/MainWindowViewModel.cs` | scanner lifetime (stop on reconnect), tab hooks |
| `tests/Sonulab.Core.Tests/Fixtures/QuadReverbSM57.pst` | real captured 8192-B preset doc (fixture) |

Real-document facts the tests rely on (verified against `presets/Quad Reverb SM57.pst`, a full
8192-byte capture, 7046 B content):
- line 26 `root\app\amp\amp:{"value":"Quad Reverb Randall Head SM57"}` (byte ~883, chunk 7)
- line 37 `root\app\ir\ir:{"value":"TWIN REVERB __ CLEAN"}` (byte ~1322, chunk 11)
- line 74 `root\app\ir\ir2\ir:{"value":""}` (byte ~2859, chunk 23)
- stub lines `root\app\amp:{"value":""}`, `root\app\ir:{"value":""}`, `root\app\ir\ir2:{"value":""}`
  exist and must NOT be treated as references.
- No line carries a `"ref"` field — that is a browse-schema field only.

---

### Task 1: PresetUsageMap — extract references by node path (the empty-map bug)

**Files:**
- Modify: `src/Sonulab.Core/Services/PresetUsageMap.cs`
- Modify: `tests/Sonulab.Core.Tests/PresetUsageMapTests.cs`
- Create: `tests/Sonulab.Core.Tests/Fixtures/QuadReverbSM57.pst` (copy of `presets/Quad Reverb SM57.pst`)
- Modify: `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj` (fixture copy-to-output)

**Interfaces:**
- Produces: `PresetUsageMap.AmpNodePath` (`const string` = `root\app\amp\amp`),
  `PresetUsageMap.IsIrRefPath(string path)` (`static bool`),
  `PresetUsageMap.HeadComplete(string documentText)` (`static bool`),
  `PresetUsageMap.Build(...)` (existing signature, new matching rule).
- Consumes: nothing new.

- [ ] **Step 1: Copy the fixture and register it**

```bash
mkdir -p tests/Sonulab.Core.Tests/Fixtures
cp "presets/Quad Reverb SM57.pst" tests/Sonulab.Core.Tests/Fixtures/QuadReverbSM57.pst
```

Add to `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj` (inside `<Project>`):

```xml
  <ItemGroup>
    <None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

Replace the line templates and add real-document tests in
`tests/Sonulab.Core.Tests/PresetUsageMapTests.cs`. Replace the `AmpLine`/`IrLine` constants and
`Captures_multiple_ir_nodes_in_one_preset` test; add the new tests. The `Doc(...)` helper stays.

```csharp
    // REAL device lines: dread/.pst documents carry ONLY {"value":…} — no "ref" field.
    // (The old fixtures injected a synthetic "ref" the firmware never sends; that let the
    // schema-ref matching bug pass 619 tests while highlighting nothing on hardware.)
    private static string Amp(string name) => $@"root\app\amp\amp:{{""value"":""{name}""}}";
    private static string Ir(string name)  => $@"root\app\ir\ir:{{""value"":""{name}""}}";
    private static string Ir2(string name) => $@"root\app\ir\ir2\ir:{{""value"":""{name}""}}";

    [Fact]
    public void Captures_primary_and_secondary_ir_refs_in_one_preset()
    {
        var map = PresetUsageMap.Build(new[] { (3, "Big", Doc(Ir("CabA"), Ir2("RoomB"))) });
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("CabA"));
        Assert.Equal(new[] { new PresetRef(3, "Big") }, map.PresetsUsingIr("RoomB"));
    }

    [Fact]
    public void Stub_lines_and_foreign_paths_are_not_references()
    {
        var map = PresetUsageMap.Build(new[]
        {
            (0, "P", Doc(
                @"root\app\amp:{""value"":""NotARef""}",        // amp block stub
                @"root\app\ir:{""value"":""NotARef""}",         // ir block stub
                @"root\app\ir\ir2:{""value"":""NotARef""}",     // ir2 stub (no trailing \ir)
                @"root\app\reverb\ir:{""value"":""NotARef""}")),// outside the ir block
        });
        Assert.Empty(map.PresetsUsingAmp("NotARef"));
        Assert.Empty(map.PresetsUsingIr("NotARef"));
    }

    [Fact]
    public void Builds_from_a_real_captured_preset_document()
    {
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        var map = PresetUsageMap.Build(new[] { (0, "Quad Reverb SM57", PresetDocument.Parse(blob)) });
        Assert.Equal(new[] { new PresetRef(0, "Quad Reverb SM57") },
                     map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Equal(new[] { new PresetRef(0, "Quad Reverb SM57") },
                     map.PresetsUsingIr("TWIN REVERB __ CLEAN"));
    }

    [Fact]
    public void HeadComplete_requires_all_three_reference_lines()
    {
        var text = File.ReadAllText(Path.Combine("Fixtures", "QuadReverbSM57.pst"))
                       .TrimEnd('\0');
        Assert.True(PresetUsageMap.HeadComplete(text));
        // Truncated before the ir2\ir line (byte ~2859): incomplete.
        Assert.False(PresetUsageMap.HeadComplete(text[..2000]));
        // Truncated mid-line (ir2\ir line present but its record still open): incomplete.
        int ir2 = text.IndexOf(@"root\app\ir\ir2\ir:{", StringComparison.Ordinal);
        Assert.False(PresetUsageMap.HeadComplete(text[..(ir2 + 10)]));
        Assert.False(PresetUsageMap.HeadComplete(""));
    }
```

Also update the two old tests that relied on synthetic `"ref"` JSON:
- `Skips_empty_values_and_non_ref_nodes`: change its two lines to
  `@"root\app\amp\amp:{""value"":""""}"` (empty value) and
  `@"root\app\gate\threshold:{""value"":-60.0}"` (non-ref path).
- Delete `Captures_multiple_ir_nodes_in_one_preset` (replaced by
  `Captures_primary_and_secondary_ir_refs_in_one_preset` above).

Add `using System.IO;` if not already imported.

- [ ] **Step 3: Run tests to verify the new ones fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetUsageMapTests -v q --nologo`
Expected: FAIL — `Builds_from_a_real_captured_preset_document` and
`Captures_primary_and_secondary_ir_refs_in_one_preset` fail (empty results — current code needs
`"ref"`); `HeadComplete` tests fail to compile until Step 4 (add the method stub first if you
want a clean RED: `public static bool HeadComplete(string documentText) => false;`).

- [ ] **Step 4: Implement path-based matching + HeadComplete**

In `src/Sonulab.Core/Services/PresetUsageMap.cs`, replace the `AmpRef`/`IrRef` constants and the
`Build` loop body, and add the two helpers:

```csharp
    /// <summary>The amp reference node: its value is the amp file name.</summary>
    public const string AmpNodePath = @"root\app\amp\amp";

    /// <summary>An IR reference node = an `ir` leaf inside the `root\app\ir` block —
    /// `root\app\ir\ir` (primary) and `root\app\ir\ir2\ir` (secondary/dual), and any future
    /// `…\ir3\ir`. Excludes the block stubs (`root\app\ir`, `root\app\ir\ir2`) by requiring
    /// the `root\app\ir\` prefix AND a `\ir` leaf.</summary>
    public static bool IsIrRefPath(string path) =>
        path.StartsWith(@"root\app\ir\", StringComparison.Ordinal) &&
        path.EndsWith(@"\ir", StringComparison.Ordinal);

    /// <summary>True when <paramref name="documentText"/> already contains COMPLETE lines for
    /// all three reference nodes (amp, primary IR, secondary IR) — the windowed head read stops
    /// here. "Complete" = the path prefix is present and its JSON object is closed.</summary>
    public static bool HeadComplete(string documentText)
    {
        return LineComplete(documentText, AmpNodePath)
            && LineComplete(documentText, @"root\app\ir\ir")
            && LineComplete(documentText, @"root\app\ir\ir2\ir");

        static bool LineComplete(string text, string path)
        {
            int i = text.IndexOf(path + ":{", StringComparison.Ordinal);
            return i >= 0 && text.IndexOf('}', i) >= 0;
        }
    }
```

In `Build`, replace the per-line matching:

```csharp
            foreach (var line in doc.Lines)
            {
                if (!NodeRecord.TryParse(line, out var rec)) continue;
                // Real dread/.pst documents carry only {"value":…} lines — match by node PATH.
                // (The schema "ref" field exists only in `browse` responses; keying off it here
                // is the bug that made every on-device map come back empty.)
                var target = rec.Path == AmpNodePath ? amp
                           : IsIrRefPath(rec.Path) ? ir
                           : null;
                if (target is null) continue;

                var value = rec.ValueString?.Trim();
                if (string.IsNullOrEmpty(value)) continue;

                if (!target.TryGetValue(value, out var list)) target[value] = list = new List<PresetRef>();
                if (!list.Any(r => r.Index == slotIndex)) list.Add(entry);   // dedupe by slot
            }
```

Delete the now-unused `AmpRef`/`IrRef` constants and the `NodeSchema.FromRecord(...).Ref` usage
(drop the `using` for `NodeSchema` only if nothing else in the file needs it — `NodeRecord` is
still used). Update the class XML-doc summary to describe path matching.

- [ ] **Step 5: Run the Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests -v q --nologo`
Expected: PASS (all, including the rewritten PresetUsageMapTests).

- [ ] **Step 6: Fix the App-side line templates that still inject "ref"**

`tests/Namager.App.Tests/FakePresetUsageService.cs` — change the two templates to real lines:

```csharp
    public static string AmpLine(string name) => $@"root\app\amp\amp:{{""value"":""{name}""}}";
    public static string IrLine(string name) => $@"root\app\ir\ir:{{""value"":""{name}""}}";
```

`tests/Namager.App.Tests/PresetUsageServiceTests.cs` — change its `AmpNode` template the same way:

```csharp
    private const string AmpNode = @"root\app\amp\amp:{{""value"":""{0}""}}";
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test -v q --nologo`
Expected: PASS, ≥619 tests.

- [ ] **Step 8: Commit**

```bash
git add src/Sonulab.Core/Services/PresetUsageMap.cs tests/Sonulab.Core.Tests/ tests/Namager.App.Tests/FakePresetUsageService.cs tests/Namager.App.Tests/PresetUsageServiceTests.cs
git commit -m "fix(core): PresetUsageMap matches refs by node path, not schema ref"
```

---

### Task 2: SonuClient background lane

**Files:**
- Modify: `src/Sonulab.Core/SonuClient.cs`
- Create: `tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs`

**Interfaces:**
- Produces (all on `SonuClient`):
  - ctor gains `int backgroundQuietMs = 1000, Func<long>? tickSource = null,
    Func<CancellationToken, Task>? backgroundPollDelay = null` (appended, defaulted).
  - `Task<string> SendBackgroundAsync(string command, CancellationToken ct = default)`
  - `Task<byte[]> DReadChunkRangeBackgroundAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)`
  - `Task<IReadOnlyList<string>> ReadListBackgroundAsync(string path, CancellationToken ct = default)`
- Consumes: existing `_gate`, `_link`, `ResponseParser`.

Semantics: a background command runs ONLY when (a) no foreground command is holding the link
gate AND (b) at least `backgroundQuietMs` have elapsed since the last foreground command started
or finished. Background commands do NOT reset the quiet clock (they can run back-to-back).
Foreground commands are never delayed by a *waiting* background command (at most they queue
behind one *in-flight* background chunk, ~100 ms).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs`:

```csharp
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientBackgroundLaneTests
{
    /// <summary>Link stub that records commands and answers dreads like FakePresetDevice.</summary>
    private sealed class RecordingLink : ISonuLink
    {
        public readonly List<string> Commands = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(command.StartsWith("read ", StringComparison.Ordinal)
                ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "");
        }
    }

    // tick + poll are injected so these tests are deterministic: the poll delay yields the
    // loop back to the test, which advances the fake clock.
    private static (SonuClient client, RecordingLink link, Action<long> setTick) Make(int quietMs = 1000)
    {
        long tick = 0;
        var link = new RecordingLink();
        var client = new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0,
            backgroundQuietMs: quietMs,
            tickSource: () => Volatile.Read(ref tick),
            backgroundPollDelay: _ => Task.Delay(1));
        return (client, link, v => Volatile.Write(ref tick, v));
    }

    [Fact]
    public async Task Background_send_waits_for_the_foreground_quiet_window()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(0);
        await client.ReadValueAsync(@"root\sys\_name");            // foreground at tick 0
        int after = link.Commands.Count;

        setTick(500);                                              // only 500 ms quiet
        var bg = client.SendBackgroundAsync("dread root\\presets:{\"index\":0,\"chunk\":1}");
        await Task.Delay(50);                                      // give the poll loop real time
        Assert.False(bg.IsCompleted);
        Assert.Equal(after, link.Commands.Count);                  // nothing sent yet

        setTick(1500);                                             // quiet window satisfied
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(after + 1, link.Commands.Count);
    }

    [Fact]
    public async Task Background_sends_do_not_reset_the_quiet_clock()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(5000);                                             // long quiet since construction tick 0? see note
        await client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":1}");
        await client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":2}");   // must not wait
        Assert.Equal(2, link.Commands.Count);
    }

    [Fact]
    public async Task Foreground_is_not_delayed_by_a_waiting_background_command()
    {
        var (client, link, setTick) = Make(quietMs: 1000);
        setTick(0);
        await client.ReadValueAsync(@"root\sys\_name");            // stamps the clock at 0
        setTick(100);
        var bg = client.SendBackgroundAsync("dread a:{\"index\":0,\"chunk\":1}"); // waits (quiet not met)
        var fg = await client.ReadValueAsync(@"root\sys\_name");   // must complete promptly
        Assert.Equal("AMP Station", fg);
        setTick(5000);
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Background_dread_range_parses_chunks_like_the_foreground_twin()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        await dev.OpenAsync();
        var client = new SonuClient(dev, backgroundQuietMs: 0);    // 0 = no quiet gating
        var fgBytes = await client.DReadChunkRangeAsync(@"root\presets", 0, 1, 2);
        var bgBytes = await client.DReadChunkRangeBackgroundAsync(@"root\presets", 0, 1, 2);
        Assert.Equal(fgBytes, bgBytes);
    }

    [Fact]
    public async Task Background_list_read_parses_names()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        await dev.OpenAsync();
        var client = new SonuClient(dev, backgroundQuietMs: 0);
        var names = await client.ReadListBackgroundAsync(@"root\presets");
        Assert.Equal(30, names.Count);
        Assert.Equal("Lead", names[0]);
    }
}
```

Note for `Background_sends_do_not_reset_the_quiet_clock`: construction must stamp the clock with
the tick at construction time (0 here), so tick 5000 is quiet. That is the intended
implementation (see Step 3).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SonuClientBackgroundLaneTests -v q --nologo`
Expected: FAIL — compile errors (new ctor params/methods missing).

- [ ] **Step 3: Implement the lane**

In `src/Sonulab.Core/SonuClient.cs`:

```csharp
    private readonly int _backgroundQuietMs;
    private readonly Func<long> _tick;
    private readonly Func<CancellationToken, Task> _bgPollDelay;
    private long _lastForegroundTicks;

    public SonuClient(ISonuLink link, int readRetryAttempts = 4, int readRetryDelayMs = 120,
        int backgroundQuietMs = 1000, Func<long>? tickSource = null,
        Func<CancellationToken, Task>? backgroundPollDelay = null)
    {
        _link = link;
        _readRetryAttempts = Math.Max(1, readRetryAttempts);
        _readRetryDelayMs = readRetryDelayMs;
        _backgroundQuietMs = backgroundQuietMs;
        _tick = tickSource ?? (static () => Environment.TickCount64);
        _bgPollDelay = backgroundPollDelay ?? (static ct => Task.Delay(50, ct));
        _lastForegroundTicks = _tick();
    }
```

Stamp the clock in the existing foreground `SendAsync` (both on entry after the gate and on exit):

```csharp
    private async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Volatile.Write(ref _lastForegroundTicks, _tick());
        var sw = Stopwatch.StartNew();
        try { return await _link.SendAsync(command, ct); }
        finally
        {
            sw.Stop();
            Volatile.Write(ref _lastForegroundTicks, _tick());
            _gate.Release();
            if (Log.IsTraceEnabled)
                Log.Trace("cmd {0,5}ms  {1}", sw.ElapsedMilliseconds,
                    command.Length > 70 ? command[..70] + "…" : command);
        }
    }
```

Add the background primitives:

```csharp
    /// <summary>Background lane: sends <paramref name="command"/> only once the link has been
    /// foreground-quiet for the configured window (default 1000 ms — chosen above AmpService's
    /// 750 ms settle, the largest legitimate gap INSIDE a foreground burst). This is how the
    /// preset-usage scan shares the serial link without ever interleaving with a user-initiated
    /// read/write burst (HwCheck finding: a dread inside a dwrite burst can silently discard the
    /// commit). Background sends do not reset the quiet clock, so they run back-to-back;
    /// a foreground command queues behind at most one in-flight background command.</summary>
    public async Task<string> SendBackgroundAsync(string command, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_tick() - Volatile.Read(ref _lastForegroundTicks) >= _backgroundQuietMs
                && await _gate.WaitAsync(0, ct))
            {
                try
                {
                    // Re-check under the gate: a foreground command may have just finished.
                    if (_tick() - Volatile.Read(ref _lastForegroundTicks) >= _backgroundQuietMs)
                        return await _link.SendAsync(command, ct);
                }
                finally { _gate.Release(); }
            }
            await _bgPollDelay(ct);
        }
    }

    /// <summary>Background twin of <see cref="DReadChunkRangeAsync"/> — same permissive
    /// torn-chunk semantics, background lane per chunk.</summary>
    public async Task<byte[]> DReadChunkRangeBackgroundAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)
    {
        var bytes = new List<byte>(count * 128);
        for (int c = firstChunk; c < firstChunk + count; c++)
        {
            var raw = await SendBackgroundAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, index, c) ?? "";
            if ((hex.Length & 1) == 1) hex = "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }

    /// <summary>Background twin of <see cref="ReadListAsync"/> (single attempt — the scanner
    /// retries at its own cadence instead of the WiFi-quirk retry loop).</summary>
    public async Task<IReadOnlyList<string>> ReadListBackgroundAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendBackgroundAsync(SonuCommands.Read(path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path
                && r.Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return Array.Empty<string>();
    }
```

Note: `await _gate.WaitAsync(0, ct)` is the non-blocking try-acquire; if the gate is held by a
foreground command, the background loop just polls again — it never queues on the gate (that
would stall a foreground command arriving behind it).

- [ ] **Step 4: Run the lane tests, then the Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/SonuClient.cs tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs
git commit -m "feat(core): SonuClient background lane — quiet-window gated sends for the usage scan"
```

---

### Task 3: DeviceRepository — windowed head read + background list

**Files:**
- Modify: `src/Sonulab.Core/Services/DeviceRepository.cs`
- Create: `tests/Sonulab.Core.Tests/PresetHeadReadTests.cs`

**Interfaces:**
- Produces (on `DeviceRepository`):
  - `const int HeadChunkCap = 32;`
  - `Task<IReadOnlyList<PresetSlot>> ListPresetsBackgroundAsync(CancellationToken ct = default)`
  - `Task<PresetDocument> ReadPresetHeadAsync(int index, bool background = true, CancellationToken ct = default)`
- Consumes: `SonuClient.DReadChunkRangeBackgroundAsync` / `DReadChunkRangeAsync` /
  `ReadListBackgroundAsync` (Task 2), `PresetUsageMap.HeadComplete` (Task 1).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/PresetHeadReadTests.cs`:

```csharp
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

public class PresetHeadReadTests
{
    private sealed class CountingLink : ISonuLink
    {
        private readonly ISonuLink _inner;
        public int Dreads;
        public CountingLink(ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }

    private static IReadOnlyList<string> RealDocLines()
    {
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        return PresetDocument.Parse(blob).Lines;
    }

    private static (DeviceRepository repo, CountingLink link, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        return (new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0)), link, dev);
    }

    [Fact]
    public async Task Head_read_of_a_real_document_finds_all_refs_within_the_cap()
    {
        var (repo, link, dev) = Make();
        dev.SeedSlot(0, "Quad", RealDocLines());
        var doc = await repo.ReadPresetHeadAsync(0);

        var map = PresetUsageMap.Build(new[] { (0, "Quad", doc) });
        Assert.Single(map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Single(map.PresetsUsingIr("TWIN REVERB __ CLEAN"));

        // THE bounded-cost assertion (handoff step 4): the real doc's last ref line (ir2\ir)
        // sits in chunk 23 — the head read must stop right there, way under the full 64.
        Assert.InRange(link.Dreads, 1, DeviceRepository.HeadChunkCap);
        Assert.True(link.Dreads < 30, $"expected an early stop, read {link.Dreads} chunks");
    }

    [Fact]
    public async Task Head_read_stops_at_content_end_for_a_short_document()
    {
        var (repo, link, dev) = Make();
        dev.SeedSlot(0, "Tiny", new[]
        {
            @"root\app\amp\amp:{""value"":""Plexi""}",
            @"root\app\ir\ir:{""value"":""V30""}",
        });   // ~70 bytes → content ends inside chunk 1
        var doc = await repo.ReadPresetHeadAsync(0);
        Assert.Single(PresetUsageMap.Build(new[] { (0, "Tiny", doc) }).PresetsUsingAmp("Plexi"));
        Assert.Equal(1, link.Dreads);                       // NUL seen in the first chunk → stop
    }

    [Fact]
    public async Task Head_read_falls_back_to_a_full_read_when_refs_never_complete()
    {
        var (repo, link, dev) = Make();
        // A document with NO ir2 line that fills all 64 chunks (no NUL, HeadComplete never true):
        var filler = Enumerable.Range(0, 220)
            .Select(i => $@"root\app\mod\rate\rawdata{i:D3}:{{""value"":1.0000000}}");
        dev.SeedSlot(0, "Odd", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" }.Concat(filler));
        var doc = await repo.ReadPresetHeadAsync(0);
        Assert.Equal(64, link.Dreads);                      // cap hit → full-document fallback
        Assert.Single(PresetUsageMap.Build(new[] { (0, "Odd", doc) }).PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task Background_list_read_returns_slots()
    {
        var (repo, _, dev) = Make();
        dev.SeedSlot(3, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        var slots = await repo.ListPresetsBackgroundAsync();
        Assert.Equal(30, slots.Count);
        Assert.Equal("Lead", slots[3].Name);
        Assert.True(slots[0].IsEmpty);
    }
}
```

(Note: 220 filler lines × ~45 B ≈ 9.9 KB > 8192 — trim to 170 lines if `FakePresetDevice`'s
8192-byte buffer overflows; the intent is content that fills all 64 chunks. `PresetDocumentFrom`
in the fake copies into `new byte[8192]` — `CopyTo` THROWS if content exceeds it, so size the
filler to land just under 8192: 170 lines ≈ 7.6 KB + amp line ≈ fits.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Core.Tests --filter PresetHeadReadTests -v q --nologo`
Expected: FAIL — compile errors (`ReadPresetHeadAsync`/`ListPresetsBackgroundAsync`/`HeadChunkCap` missing).

- [ ] **Step 3: Implement**

Add to `src/Sonulab.Core/Services/DeviceRepository.cs`:

```csharp
    /// <summary>Head-read window: the amp ref sits near chunk 7, primary IR near 11, secondary
    /// IR near 23 (measured, real captures — see the 2026-07-24 perf handoff). 32 gives slack
    /// for value-length drift; past it we assume an unexpected layout and fall back to a full read.</summary>
    public const int HeadChunkCap = 32;

    public async Task<IReadOnlyList<PresetSlot>> ListPresetsBackgroundAsync(CancellationToken ct = default)
    {
        var names = await _client.ReadListBackgroundAsync(PresetsList, ct);
        var slots = new List<PresetSlot>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(new PresetSlot(i, i < names.Count ? names[i] : ""));
        return slots;
    }

    /// <summary>Reads only the HEAD of a preset document — chunk by chunk until the amp and both
    /// IR reference lines are complete (<see cref="PresetUsageMap.HeadComplete"/>), the content-end
    /// NUL appears, or <see cref="HeadChunkCap"/> is hit (then: full-read fallback). Built for the
    /// preset-usage scan: ~14–25 chunks instead of 64 (~2.5×). <paramref name="background"/>=true
    /// rides the SonuClient background lane (default; the scan must yield to user bursts);
    /// false uses the foreground lane (EnsureCompleteAsync's urgent finish).</summary>
    public async Task<PresetDocument> ReadPresetHeadAsync(int index, bool background = true, CancellationToken ct = default)
    {
        Task<byte[]> ReadChunks(int first, int count) => background
            ? _client.DReadChunkRangeBackgroundAsync(PresetsList, index, first, count, ct)
            : _client.DReadChunkRangeAsync(PresetsList, index, first, count, ct);

        var bytes = new List<byte>(HeadChunkCap * 128);
        for (int chunk = 1; chunk <= HeadChunkCap; chunk++)
        {
            var seg = await ReadChunks(chunk, 1);
            bytes.AddRange(seg);
            // Content ends at the first NUL (the rest of the blob is zero padding) — or a torn
            // chunk came back empty; either way there is nothing more to learn from this slot.
            if (seg.Length == 0 || Array.IndexOf(seg, (byte)0) >= 0)
                return PresetDocument.Parse(bytes.ToArray());
            if (PresetUsageMap.HeadComplete(System.Text.Encoding.ASCII.GetString(bytes.ToArray())))
                return PresetDocument.Parse(bytes.ToArray());
        }
        // Unexpected layout (refs not found in the head window): fall back to the full document
        // so the guard logic never runs on silently truncated data.
        var rest = await ReadChunks(HeadChunkCap + 1, PresetChunks - HeadChunkCap);
        bytes.AddRange(rest);
        return PresetDocument.Parse(bytes.ToArray());
    }
```

`PresetDocument.Parse` requires a byte[]; it tolerates any length (splits at first NUL). No
change needed there.

- [ ] **Step 4: Run the Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Services/DeviceRepository.cs tests/Sonulab.Core.Tests/PresetHeadReadTests.cs
git commit -m "feat(core): windowed preset head read (<=32 chunks) + background preset list"
```

---

### Task 4: PresetUsageService — background scanner

**Files:**
- Modify: `src/Namager.App/Services/PresetUsageService.cs` (full rework)
- Modify: `tests/Namager.App.Tests/PresetUsageServiceTests.cs` (full rewrite)

**Interfaces:**
- Produces (new `IPresetUsageService` — ALL consumers update in Tasks 5–7):

```csharp
public interface IPresetUsageService
{
    /// <summary>The latest built map. Empty before any scan; PARTIAL while a scan is running;
    /// possibly STALE after Invalidate() (kept for best-effort highlights — guards must use
    /// EnsureCompleteAsync instead).</summary>
    PresetUsageMap Current { get; }

    /// <summary>True when Current covers every occupied preset and no Invalidate() has
    /// happened since. The delete/rename guards may trust Current only when this is true.</summary>
    bool IsComplete { get; }

    /// <summary>Raised after each preset resolves and once when the scan completes.
    /// MAY fire on a background thread — subscribers must marshal to the UI thread.</summary>
    event Action? MapUpdated;

    /// <summary>Idempotent: start (or continue) the background scan if the map is incomplete.
    /// Returns immediately; progress arrives via MapUpdated.</summary>
    void EnsureScanning();

    /// <summary>Guard path: finish the scan NOW (foreground reads) and return the complete map.
    /// Throws if the scan cannot complete (link died) — callers must treat that as "blocked".</summary>
    Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default);

    /// <summary>A preset mutation happened: the map is stale. Keeps Current for best-effort
    /// highlights but clears IsComplete; the next EnsureScanning()/EnsureCompleteAsync() rescans.</summary>
    void Invalidate();

    /// <summary>Cancel any background work (disconnect / reconnect).</summary>
    void Stop();
}
```

- Consumes: `DeviceRepository.ListPresetsBackgroundAsync` / `ListPresetsAsync` /
  `ReadPresetHeadAsync` (Task 3), `PresetUsageMap.Build` (Task 1).
- `PresetRefFormat` (bottom of the file) is unchanged.

- [ ] **Step 1: Rewrite the tests (failing)**

Replace `tests/Namager.App.Tests/PresetUsageServiceTests.cs` entirely:

```csharp
// tests/Namager.App.Tests/PresetUsageServiceTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageServiceTests
{
    private const string AmpNode = @"root\app\amp\amp:{{""value"":""{0}""}}";
    private static string Amp(string name) => string.Format(AmpNode, name);

    private static (PresetUsageService svc, FakePresetDevice dev, CountingLink link) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        // backgroundQuietMs 0: tests exercise scan logic, not the lane (lane has its own tests)
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        return (new PresetUsageService(repo), dev, link);
    }

    [Fact]
    public async Task EnsureComplete_builds_the_full_map()
    {
        var (svc, _, _) = Make();
        var map = await svc.EnsureCompleteAsync();
        Assert.True(svc.IsComplete);
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task Scan_is_progressive_and_raises_MapUpdated_per_preset()
    {
        var (svc, _, _) = Make();
        int updates = 0;
        svc.MapUpdated += () => Interlocked.Increment(ref updates);
        svc.EnsureScanning();
        await svc.EnsureCompleteAsync();
        Assert.True(updates >= 2, $"expected per-preset updates, got {updates}");
        Assert.Single(svc.Current.PresetsUsingAmp("Plexi").Where(r => r.Index == 0));
    }

    [Fact]
    public async Task Complete_map_is_cached_until_invalidated()
    {
        var (svc, _, link) = Make();
        await svc.EnsureCompleteAsync();
        int afterFirst = link.Dreads;
        Assert.True(afterFirst > 0);

        await svc.EnsureCompleteAsync();
        Assert.Equal(afterFirst, link.Dreads);              // cache hit

        svc.Invalidate();
        Assert.False(svc.IsComplete);
        Assert.NotSame(PresetUsageMap.Empty, svc.Current);  // stale map kept for highlights
        await svc.EnsureCompleteAsync();
        Assert.True(link.Dreads > afterFirst);              // rescan happened
    }

    [Fact]
    public async Task Invalidate_during_a_scan_restarts_it()
    {
        var (svc, dev, _) = Make();
        svc.EnsureScanning();
        svc.Invalidate();
        dev.SeedSlot(2, "New", new[] { Amp("JCM800") });
        var map = await svc.EnsureCompleteAsync();
        Assert.Single(map.PresetsUsingAmp("JCM800"));       // post-invalidate content included
    }

    [Fact]
    public async Task EnsureComplete_throws_when_the_link_is_dead_and_guards_stay_closed()
    {
        var dev = new FakePresetDevice();                   // never opened → SendAsync throws
        var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
        Assert.False(svc.IsComplete);
    }

    [Fact]
    public async Task Stop_cancels_a_running_scan()
    {
        var (svc, _, _) = Make();
        svc.EnsureScanning();
        svc.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
    }

    private sealed class CountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public CountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Namager.App.Tests --filter PresetUsageServiceTests -v q --nologo`
Expected: FAIL — compile errors against the old interface.

- [ ] **Step 3: Implement the scanner**

Replace the `IPresetUsageService` interface, `PresetUsageService`, and `NullPresetUsageService`
in `src/Namager.App/Services/PresetUsageService.cs` (keep `PresetRefFormat`). Interface exactly
as in the **Interfaces** block above. Implementation:

```csharp
/// <summary>Background preset-usage scanner. Reads each occupied preset's HEAD (windowed,
/// ≤32 chunks — see DeviceRepository.ReadPresetHeadAsync) over the SonuClient background lane,
/// publishing a partial map after every preset. The scan therefore never blocks a tab and never
/// interleaves with user-initiated bursts (the lane waits for foreground quiet). Shared by the
/// preset, amp and IR list VMs; one instance per connection.</summary>
public sealed class PresetUsageService : IPresetUsageService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _scanTask;
    private int _version;                  // bumped by Invalidate: a running scan restarts
    private volatile bool _urgent;         // EnsureCompleteAsync: use the foreground lane
    private volatile PresetUsageMap _current = PresetUsageMap.Empty;
    private volatile bool _isComplete;

    public PresetUsageService(DeviceRepository repo) => _repo = repo;

    public PresetUsageMap Current => _current;
    public bool IsComplete => _isComplete;
    public event Action? MapUpdated;

    public void EnsureScanning()
    {
        lock (_sync)
        {
            if (_isComplete || _cts.IsCancellationRequested) return;
            if (_scanTask is { IsCompleted: false }) return;
            _scanTask = Task.Run(() => ScanLoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    public async Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default)
    {
        _urgent = true;
        try
        {
            while (!_isComplete)
            {
                ct.ThrowIfCancellationRequested();
                _cts.Token.ThrowIfCancellationRequested();
                Task scan;
                lock (_sync)
                {
                    if (_scanTask is null or { IsCompleted: true } && !_isComplete)
                        _scanTask = Task.Run(() => ScanLoopAsync(_cts.Token), CancellationToken.None);
                    scan = _scanTask!;
                }
                await scan.WaitAsync(ct);   // ScanLoop swallows its own errors; see below
                if (!_isComplete && scan.IsCompleted)
                    throw new InvalidOperationException("Preset-usage scan could not complete.");
            }
            return _current;
        }
        finally { _urgent = false; }
    }

    public void Invalidate()
    {
        lock (_sync) { _version++; _isComplete = false; }
        // _current is kept: stale highlights beat no highlights. Guards use EnsureCompleteAsync.
    }

    public void Stop() => _cts.Cancel();

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                int version; lock (_sync) version = _version;
                var slots = _urgent
                    ? await _repo.ListPresetsAsync(ct)
                    : await _repo.ListPresetsBackgroundAsync(ct);
                var resolved = new List<(int, string, Sonulab.Core.Model.PresetDocument)>();
                bool restart = false;
                foreach (var s in slots)
                {
                    if (s.IsEmpty) continue;
                    ct.ThrowIfCancellationRequested();
                    lock (_sync) { if (_version != version) { restart = true; } }
                    if (restart) break;
                    var doc = await _repo.ReadPresetHeadAsync(s.Index, background: !_urgent, ct);
                    resolved.Add((s.Index, s.Name, doc));
                    _current = PresetUsageMap.Build(resolved);
                    MapUpdated?.Invoke();
                }
                if (restart) continue;                       // stale version: rescan from the top
                lock (_sync) { if (_version == version) _isComplete = true; else continue; }
                MapUpdated?.Invoke();
                return;
            }
        }
        catch (OperationCanceledException) { /* Stop() or caller cancel */ }
        catch (Exception ex)
        {
            // Best-effort: a link failure ends the scan quietly (highlights stay partial/stale).
            // EnsureCompleteAsync observes the incomplete state and throws — guards stay CLOSED.
            Log.Warn(ex, "preset-usage scan aborted");
        }
    }
}

/// <summary>No-op fallback so a VM constructed without a usage service (existing tests) works —
/// nothing is ever "used", the map reports complete, guards never block.</summary>
public sealed class NullPresetUsageService : IPresetUsageService
{
    public static readonly NullPresetUsageService Instance = new();
    public PresetUsageMap Current => PresetUsageMap.Empty;
    public bool IsComplete => true;
    public event Action? MapUpdated { add { } remove { } }
    public void EnsureScanning() { }
    public Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default)
        => Task.FromResult(PresetUsageMap.Empty);
    public void Invalidate() { }
    public void Stop() { }
}
```

Notes:
- The old `IStatusService` ctor param is dropped (the background scan is silent; the guard path
  shows its own status scope in the VMs). Update `MainWindowViewModel`'s construction in Task 7 —
  until then the App project won't compile, which is why Tasks 4–7 land as one commit **only if
  needed**; prefer: keep a temporary compatibility ctor `PresetUsageService(DeviceRepository repo,
  IStatusService? status)` that ignores `status` and delete it in Task 7. Add it now:

```csharp
    // TODO(Task 7): remove — kept so MainWindowViewModel compiles until its wiring task.
    public PresetUsageService(DeviceRepository repo, IStatusService? status) : this(repo) { }
```

- `AmpListViewModel`/`IrListViewModel` still call `_usage.GetAsync()` at this point — they break.
  To keep this task independently green, add a temporary default-interface bridge is NOT allowed
  (interface changed). Instead: Tasks 4+5+6 each leave the suite green by updating the direct
  compile breaks minimally: in THIS task, patch the two VMs' `ApplyUsageAsync` to the one-liner
  `var map = _usage.Current;` (full rewiring with events/guards happens in Tasks 5–6):

```csharp
    private Task ApplyUsageAsync()
    {
        try
        {
            var map = _usage.Current;
            foreach (var item in Items)
                item.UsedInPresets = item.IsEmpty
                    ? System.Array.Empty<Sonulab.Core.Services.PresetRef>() : map.PresetsUsingAmp(item.Name);
        }
        catch (Exception ex) { Log.Warn(ex, "amp preset-usage lookup failed"); }
        return Task.CompletedTask;
    }
```

  (IR mirror uses `PresetsUsingIr`.) Update `FakePresetUsageService` to the new interface NOW
  (Task 5 Step 1 shows the final shape — implement it here) so the App test project compiles;
  existing VM tests that asserted scan-blocking behavior (`RefreshUsage_holds_the_busy_gate_while_scanning`
  in `AmpListViewModelTests` and any `Gate`-based test in `ItemUsageTests`/`IrListViewModelTests`)
  should be updated to the new semantics or temporarily adjusted — final behavior asserts land
  in Tasks 5–6.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test -v q --nologo`
Expected: PASS (some VM tests updated as described).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/PresetUsageService.cs src/Namager.App/ViewModels/AmpListViewModel.cs src/Namager.App/ViewModels/IrListViewModel.cs tests/Namager.App.Tests/
git commit -m "feat(app): PresetUsageService is a progressive background scanner"
```

---

### Task 5: AmpListViewModel — progressive highlights + fail-closed guards

**Files:**
- Modify: `src/Namager.App/ViewModels/AmpListViewModel.cs`
- Modify: `tests/Namager.App.Tests/FakePresetUsageService.cs` (finalize)
- Modify: `tests/Namager.App.Tests/AmpListViewModelTests.cs`, `tests/Namager.App.Tests/ItemUsageTests.cs`

**Interfaces:**
- Consumes: `IPresetUsageService` (Task 4 shape).
- Produces: `AmpListViewModel.RefreshUsageAsync()` (kept, now non-blocking, returns `Task`);
  behavior contract for Task 7 (MainWindow calls it on tab revisit).

Final `FakePresetUsageService`:

```csharp
using Namager.App.Services;
using Sonulab.Core.Services;

/// <summary>Controllable usage service for VM tests: set <see cref="Map"/>/<see cref="Complete"/>,
/// raise <see cref="RaiseMapUpdated"/>, observe calls.</summary>
public sealed class FakePresetUsageService : IPresetUsageService
{
    public PresetUsageMap Map { get; set; } = PresetUsageMap.Empty;
    public bool Complete { get; set; } = true;
    public int InvalidateCount { get; private set; }
    public int EnsureScanningCount { get; private set; }
    public int EnsureCompleteCount { get; private set; }

    // When set, EnsureCompleteAsync awaits this — lets a test hold a guard check in flight.
    public System.Threading.Tasks.TaskCompletionSource? Gate { get; set; }
    // When set, EnsureCompleteAsync throws (simulates a dead link — guards must stay closed).
    public System.Exception? FailWith { get; set; }

    public PresetUsageMap Current => Map;
    public bool IsComplete => Complete;
    public event System.Action? MapUpdated;
    public void RaiseMapUpdated() => MapUpdated?.Invoke();

    public void EnsureScanning() { EnsureScanningCount++; }
    public async System.Threading.Tasks.Task<PresetUsageMap> EnsureCompleteAsync(
        System.Threading.CancellationToken ct = default)
    {
        EnsureCompleteCount++;
        if (Gate is not null) await Gate.Task;
        if (FailWith is not null) throw FailWith;
        Complete = true;
        return Map;
    }
    public void Invalidate() { InvalidateCount++; Complete = false; }
    public void Stop() { }

    // MapFor / AmpLine / IrLine helpers unchanged from Task 1's ref-less templates.
}
```

- [ ] **Step 1: Write the failing VM tests**

Add to `tests/Namager.App.Tests/AmpListViewModelTests.cs` (using its existing `MakeVm`-style
helpers — the file already constructs `AmpListViewModel(ampService, true, status, usage: usage)`
with `dispatch: a => a()` in usage tests; follow the existing local pattern):

```csharp
    [Fact]
    public async Task Refresh_does_not_block_on_an_incomplete_usage_scan()
    {
        var usage = new FakePresetUsageService { Complete = false };   // scan "running"
        var vm = MakeUsageVm(usage);                                   // helper per existing tests
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);                                       // list is usable NOW
        Assert.NotEmpty(vm.Items);
        Assert.Equal(1, usage.EnsureScanningCount);                    // scan was kicked, not awaited
    }

    [Fact]
    public async Task Highlights_fill_in_when_the_scan_publishes()
    {
        var usage = new FakePresetUsageService { Complete = false };
        var vm = MakeUsageVm(usage);                                   // must pass dispatch: a => a()
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.All(vm.Items, i => Assert.Empty(i.UsedInPresets));

        usage.Map = FakePresetUsageService.MapFor((0, "Lead", new[]
            { FakePresetUsageService.AmpLine("AmpA") }));              // AmpA = an occupied item name
        usage.Complete = true;
        usage.RaiseMapUpdated();
        Assert.NotEmpty(vm.Items.First(i => i.Name == "AmpA").UsedInPresets);
    }

    [Fact]
    public async Task Delete_with_incomplete_map_finishes_the_scan_and_blocks_when_used()
    {
        var usage = new FakePresetUsageService
        {
            Complete = false,
            Map = FakePresetUsageService.MapFor((0, "Lead", new[]
                { FakePresetUsageService.AmpLine("AmpA") })),
        };
        var vm = MakeUsageVm(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items.First(i => i.Name == "AmpA");
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, usage.EnsureCompleteCount);                    // guard finished the scan
        Assert.Contains("used in the following presets", vm.ErrorMessage);
    }

    [Fact]
    public async Task Delete_stays_blocked_when_the_scan_cannot_complete()
    {
        var usage = new FakePresetUsageService
        { Complete = false, FailWith = new InvalidOperationException("link died") };
        var vm = MakeUsageVm(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items.First(i => !i.IsEmpty);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);                               // refused, with a message
        Assert.All(vm.Items, i => Assert.True(!i.IsEmpty || true));    // nothing deleted:
        // assert via the fake amp device that no delete dwrite was issued (existing pattern:
        // FakeAmpDevice records commands — reuse the file's existing helper for this).
    }
```

Delete `RefreshUsage_holds_the_busy_gate_while_scanning` (busy-blocking is now WRONG behavior);
rewrite `RefreshUsage_reapplies_without_relisting_amps` to assert `RefreshUsageAsync` applies
`usage.Current` without a list re-read and without setting `IsBusy`. In `ItemUsageTests`, drop
`Gate`/`GetCount` usages in favor of `Complete`/`RaiseMapUpdated` (same intent: partial map →
no highlight until published).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Namager.App.Tests --filter AmpListViewModelTests -v q --nologo`
Expected: FAIL — new tests fail (VM still has Task 4's minimal bridge, no events/guards).

- [ ] **Step 3: Implement the rewiring**

In `src/Namager.App/ViewModels/AmpListViewModel.cs`:

Constructor — subscribe to the scanner (after `_usage = …`):

```csharp
        // Progressive highlight fill: the background scan publishes after each preset resolves.
        // MapUpdated may fire on a worker thread — marshal through the dispatch seam.
        _usage.MapUpdated += () => _dispatch(ApplyUsage);
```

Replace `ApplyUsageAsync` with a synchronous apply + kick:

```csharp
    /// <summary>Tag each item with the presets that use it, from the CURRENT (possibly partial
    /// or stale) map — best-effort by design; the fail-closed check lives in the guards.</summary>
    private void ApplyUsage()
    {
        var map = _usage.Current;
        foreach (var item in Items)
            item.UsedInPresets = item.IsEmpty
                ? System.Array.Empty<Sonulab.Core.Services.PresetRef>() : map.PresetsUsingAmp(item.Name);
    }
```

`ReloadAsync` tail becomes:

```csharp
        ApplyUsage();
        _usage.EnsureScanning();     // non-blocking: highlights stream in via MapUpdated
```

`RefreshUsageAsync` (tab revisit) becomes non-blocking:

```csharp
    /// <summary>Re-apply highlighting from the current map and make sure a scan is running if
    /// it is incomplete/stale. Never sets IsBusy — the scan streams in via MapUpdated.</summary>
    public Task RefreshUsageAsync()
    {
        ApplyUsage();
        _usage.EnsureScanning();
        return Task.CompletedTask;
    }
```

Guards — replace the head of `DeleteAsync` and `CommitRenameAsync` with a shared fail-closed check:

```csharp
    /// <summary>Fail-closed guard: resolve the preset-usage of <paramref name="s"/> COMPLETELY
    /// before a delete/rename. If the map is incomplete, finishes the scan now (foreground reads,
    /// status-scoped). Returns null when usage cannot be determined — the caller must refuse.</summary>
    private async Task<IReadOnlyList<Sonulab.Core.Services.PresetRef>?> ResolveUsageAsync(AmpItemViewModel s)
    {
        if (_usage.IsComplete) return _usage.Current.PresetsUsingAmp(s.Name);
        IsBusy = true; BusyMessage = "Checking preset usage…";
        using var op = _status.BeginOperation("Checking preset usage…");
        try { return (await _usage.EnsureCompleteAsync()).PresetsUsingAmp(s.Name); }
        catch (Exception ex)
        {
            Log.Warn(ex, "usage check failed");
            ErrorMessage = "Couldn't verify preset usage — try again.";
            _status.Failure("Couldn't verify preset usage.");
            return null;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        if (await ResolveUsageAsync(s) is not { } refs) return;        // unknown → refuse
        s.UsedInPresets = refs;
        if (refs.Count > 0) { BlockUsed(s, "delete"); return; }
        await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (await ResolveUsageAsync(s) is not { } refs) { s.IsEditing = false; return; }
        s.UsedInPresets = refs;
        if (refs.Count > 0) { s.IsEditing = false; BlockUsed(s, "rename"); return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;
    }
```

Note `ResolveUsageAsync` runs BEFORE `RunAsync` (no gate conflict: `EnsureCompleteAsync`'s
foreground reads are the user's own operation here, serialized on the UI thread).

- [ ] **Step 4: Run the App suite**

Run: `dotnet test tests/Namager.App.Tests -v q --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/AmpListViewModel.cs tests/Namager.App.Tests/
git commit -m "feat(app): amp list highlights fill progressively; delete/rename guards fail closed"
```

---

### Task 6: IrListViewModel — mirror

**Files:**
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs`
- Modify: `tests/Namager.App.Tests/IrListViewModelTests.cs`, `tests/Namager.App.Tests/ItemUsageTests.cs`

**Interfaces:**
- Consumes: `IPresetUsageService` (Task 4), same contract as Task 5.
- Produces: `IrListViewModel` ctor gains `Action<Action>? dispatch = null` (appended, optional —
  IrListViewModel has no dispatch seam today; add the same field/default as AmpListViewModel:
  `_dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));`).

- [ ] **Step 1: Write failing tests** — mirror Task 5 Step 1 exactly, on `IrListViewModelTests`:
  `Refresh_does_not_block_on_an_incomplete_usage_scan`, `Highlights_fill_in_when_the_scan_publishes`
  (uses `FakePresetUsageService.IrLine`), `Delete_with_incomplete_map_finishes_the_scan_and_blocks_when_used`,
  `Delete_stays_blocked_when_the_scan_cannot_complete`. Construct with `dispatch: a => a()`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Namager.App.Tests --filter IrListViewModelTests -v q --nologo`
Expected: FAIL.

- [ ] **Step 3: Implement** — mirror Task 5 Step 3 in `IrListViewModel`: add the `_dispatch`
field + ctor param, the `MapUpdated` subscription, synchronous `ApplyUsage()` (with
`map.PresetsUsingIr`), non-blocking `RefreshUsageAsync`, `ResolveUsageAsync(IrItemViewModel s)`
(identical body, `PresetsUsingIr`), and the guard heads of `DeleteAsync`/`CommitRenameAsync`
(same shape as the amp VM — IR delete line is
`await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _irs.DeleteIrAsync(s.Index));`).

- [ ] **Step 4: Run the App suite** — `dotnet test tests/Namager.App.Tests -v q --nologo` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/IrListViewModel.cs tests/Namager.App.Tests/
git commit -m "feat(app): IR list mirrors progressive usage highlights + fail-closed guards"
```

---

### Task 7: MainWindowViewModel — scanner lifetime + wiring

**Files:**
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Namager.App/Services/PresetUsageService.cs` (remove the temporary compat ctor)
- Modify: `tests/Namager.App.Tests/MainWindowViewModelTests.cs` (compile/behavior fixes if needed)

**Interfaces:**
- Consumes: `PresetUsageService(DeviceRepository)` (Task 4), `AmpListViewModel.RefreshUsageAsync`
  (Task 5), `IrListViewModel.RefreshUsageAsync` (Task 6).

- [ ] **Step 1: Wire the lifetime**

In `MainWindowViewModel`: add a field `private PresetUsageService? _usageService;`.
In the `Connected` handler replace `var usage = new PresetUsageService(_connection.Repository!, Status);` with:

```csharp
            // One scanner per connection. Stop the previous connection's background scan first —
            // its link is gone and its task must not linger into the new session.
            _usageService?.Stop();
            var usage = _usageService = new PresetUsageService(_connection.Repository!);
```

Then delete the temporary compat ctor from `PresetUsageService`.

`EnsureTabLoaded` stays as-is (`RefreshUsageAsync` is now non-blocking, so `PendingTabLoad`
completes immediately on revisit — that is fine for the Tone3000 handoff await).

- [ ] **Step 2: Run the full suite**

Run: `dotnet test -v q --nologo`
Expected: PASS (fix any MainWindowViewModelTests compile fallout from the ctor change).

- [ ] **Step 3: Commit**

```bash
git add src/Namager.App/ViewModels/MainWindowViewModel.cs src/Namager.App/Services/PresetUsageService.cs tests/Namager.App.Tests/
git commit -m "feat(app): usage scanner lifetime — one per connection, stopped on reconnect"
```

---

### Task 8: End-to-end acceptance test + docs + hardware checklist

**Files:**
- Create: `tests/Namager.App.Tests/UsageScanEndToEndTests.cs`
- Modify: `docs/HARDWARE-VALIDATION-preset-usage.md`
- Modify: `docs/superpowers/2026-07-24-preset-usage-scan-perf-handoff.md` (status note at top)

- [ ] **Step 1: Write the end-to-end test (real scanner + fake device, no VM fakes)**

```csharp
// tests/Namager.App.Tests/UsageScanEndToEndTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

/// <summary>The handoff's step-4 acceptance: with a REAL preset document, the scan resolves a
/// used-highlight within the head-read budget (≤32 dreads/preset) and the map is correct.</summary>
public class UsageScanEndToEndTests
{
    [Fact]
    public async Task Scan_of_a_real_document_is_bounded_and_correct()
    {
        var dev = new FakePresetDevice();   // linked from Sonulab.Core.Tests (see Step 2)
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        dev.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
        await dev.OpenAsync();
        var counter = new CountingLink(dev);
        var svc = new PresetUsageService(
            new DeviceRepository(new SonuClient(counter, backgroundQuietMs: 0)));

        var map = await svc.EnsureCompleteAsync();

        Assert.Single(map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Single(map.PresetsUsingIr("TWIN REVERB __ CLEAN"));
        Assert.InRange(counter.Dreads, 1, DeviceRepository.HeadChunkCap);
    }

    private sealed class CountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public CountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }
}
```

- [ ] **Step 2: Make `FakePresetDevice` + the fixture available to the App test project**

`Namager.App.Tests.csproj` references `Sonulab.Core.Tests`? Check — if not, add a compile link
and fixture copy instead of a project reference:

```xml
  <ItemGroup>
    <Compile Include="..\Sonulab.Core.Tests\FakePresetDevice.cs" Link="FakePresetDevice.cs" />
    <None Include="..\Sonulab.Core.Tests\Fixtures\QuadReverbSM57.pst" Link="Fixtures\QuadReverbSM57.pst" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

(If `PresetUsageServiceTests.cs` already compiles against `FakePresetDevice`, this link exists —
reuse whatever mechanism is in place.)

- [ ] **Step 3: Run the full suite**

Run: `dotnet test -v q --nologo`
Expected: PASS, ≥625 tests.

- [ ] **Step 4: Update the hardware checklist**

Rewrite the checks in `docs/HARDWARE-VALIDATION-preset-usage.md` for the new UX (keep its
existing format). The checklist must cover:
1. First Amps-tab visit after connect: list appears **immediately** (≤2 s), NOT gated on
   "Checking preset usage…"; highlights fill in progressively over ~15–30 s.
2. Highlight correctness: amps/IRs referenced by presets get the highlight + tooltip lists the
   right presets (compare against the preset list by hand).
3. Delete of a used amp while the scan is still running: shows "Checking preset usage…",
   then blocks with the used-by message. Delete of an unused amp: proceeds.
4. Preset edit (change an amp) → revisit Amps tab: highlight moves to the new amp after the
   rescan (invalidate → progressive refill).
5. Scan vs. user ops: start an amp upload while highlights are still filling — upload must run
   normally; no corrupted slot (the background lane yields). Verify the uploaded amp byte-checks
   (the upload path already verifies).
6. WiFi smoke: repeat check 1 over WiFi (`--wifi` equivalent: connect with USB unplugged).

- [ ] **Step 5: Update the handoff doc status**

At the top of `docs/superpowers/2026-07-24-preset-usage-scan-perf-handoff.md`, under the title:

```markdown
> **STATUS 2026-07-24: FIXED in `feat-preset-usage-guard`** — bug 2 (path-matching) + Options
> A+B built per `docs/superpowers/plans/2026-07-24-preset-usage-scan-fix.md`: windowed head read
> (≤32 chunks), SonuClient background lane (1 s foreground-quiet window), progressive
> non-blocking highlights, fail-closed guards. Paced-overlap pipelining (30 ms) NOT built —
> documented follow-up. Awaiting on-device validation (docs/HARDWARE-VALIDATION-preset-usage.md).
```

- [ ] **Step 6: Commit**

```bash
git add tests/Namager.App.Tests/ docs/HARDWARE-VALIDATION-preset-usage.md docs/superpowers/2026-07-24-preset-usage-scan-perf-handoff.md
git commit -m "test(app): bounded+progressive usage-scan acceptance; refresh hw checklist"
```

---

## Explicitly deferred (do NOT build in this plan)

- **Paced-overlap pipelining** (~33 ms/chunk vs ~57): a transport-level change to SerialSonuLink;
  the windowed+background scan is fast enough (~15–25 s in background, list never blocked).
- **`dswap`-based reorder** and amp/IR `dswap` probing: separate feature (see PROTOCOL.md).
- **Status-bar scan progress indicator**: the progressive highlights are the feedback; revisit
  after hardware validation if Ed wants visibility.

## Self-Review Notes

- Spec coverage: bug 2 (Task 1), Option A windowed read (Task 3), Option B non-blocking +
  progressive (Tasks 4–7), serial-link safety (Task 2 lane + 1000 ms > 750 ms settle), guards
  fail-closed (Tasks 5–6 `ResolveUsageAsync`), bounded-cost + non-blocking tests (Tasks 3, 5, 8),
  invalidation on preset mutations (already wired in `PresetListViewModel.RunAsync` — unchanged,
  new `Invalidate()` keeps the same name/semantics).
- Type consistency: `IPresetUsageService` shape identical across Tasks 4–7;
  `ReadPresetHeadAsync(int, bool, CancellationToken)` consistent between Tasks 3 and 4;
  `HeadComplete`/`AmpNodePath`/`IsIrRefPath` defined in Task 1, consumed in Task 3.
- Known compile-ordering hazard: Task 4 changes the interface consumed by both list VMs — the
  task includes the minimal VM bridge so the suite stays green at every commit.
