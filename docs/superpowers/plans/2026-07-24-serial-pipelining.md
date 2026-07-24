# Paced-Overlap Serial Pipelining Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make foreground bulk `dread` reads ~1.7× faster by overlapping serial sends, without weakening any integrity guarantee of the current lockstep path.

**Architecture:** `ISonuLink` gains `SendBatchAsync` as a **default interface method** whose fallback is a plain lockstep loop — so `TcpSonuLink` and every fake stay correct with zero edits. `SerialSonuLink` overrides it with a self-clocking loop: send command N+1 once the first byte of response N has arrived, never closer than a 30 ms floor. Responses come back as raw windows split at NUL, explicitly **not** positionally aligned with the commands; `SonuClient.DReadChunkRangeAsync` matches each response **by content** (`ResponseParser.ChunkHex` verifies index *and* chunk) and re-reads anything missing lockstep.

**Tech Stack:** .NET 10, C#, xUnit. No new dependencies.

## Global Constraints

- **Serial only.** Do not touch `src/Sonulab.Transport.Wifi/` or any TCP path.
- **Hands off (a parallel agent owns these):** `ReorderService`, `PresetUsageMap`, `PresetUsageService`, `PresetListViewModel`. Also out of scope: `DeviceRepository`, `tools/HwCheck`, and `SonuClient.DReadChunkRangeBackgroundAsync`.
- **Pace floor is 30 ms** (`PipelineMinPaceMs` default). PROTOCOL.md: 25 ms is the cliff where the firmware drops commands. Never lower the default.
- **`SendBatchAsync` is for response-producing commands only** (`dread`). A silent command (`write`/`dwrite`) emits no NUL and would shift every later window. The only call site is `DReadChunkRangeAsync`.
- **Never trust window position.** Every response must be identified by its own content.
- **Permissive tail is preserved:** a chunk that cannot be recovered contributes 0 bytes to the result, exactly as today, so `SlotBlobService`'s validated wrappers keep failing loudly and unchanged.
- Baseline: 648 tests green (190 Core, 268 App, 86 Distill, 78 Tone3000, 26 Wifi). Run `dotnet test` from the worktree root.
- Test files in `tests/Sonulab.Core.Tests/` use **no namespace** (global). Match that.
- Spec: `docs/superpowers/specs/2026-07-24-serial-pipelining-design.md`.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/Sonulab.Core/Transport/SerialLinkOptions.cs` (modify) | Three new pipelining knobs + the kill switch |
| `src/Sonulab.Core/Transport/ISonuLink.cs` (modify) | `SendBatchAsync` contract + lockstep default implementation |
| `src/Sonulab.Core/Transport/SerialSonuLink.cs` (modify) | Self-clocking overlap; injectable clock seam |
| `src/Sonulab.Core/SonuClient.cs` (modify) | Gated batch helper; batch + content-matching + repair in `DReadChunkRangeAsync` |
| `tests/Sonulab.Core.Tests/SendBatchDefaultTests.cs` (create) | The default interface fallback is really lockstep |
| `tests/Sonulab.Core.Tests/ScriptedSerialPort.cs` (create) | Virtual-clock serial port double: latency, fragmentation, drops |
| `tests/Sonulab.Core.Tests/ScriptedSerialPortTests.cs` (create) | Self-tests proving the double behaves as advertised |
| `tests/Sonulab.Core.Tests/SerialSonuLinkBatchTests.cs` (create) | Pacing, self-clocking, deadline, cancellation, kill switch |
| `tests/Sonulab.Core.Tests/BatchLinkDouble.cs` (create) | `ISonuLink` double that distorts batch windows on demand |
| `tests/Sonulab.Core.Tests/SonuClientBatchReadTests.cs` (create) | Content matching, repair, misalignment immunity |
| `tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs` (modify) | Background lane cannot interleave mid-batch |
| `docs/HARDWARE-VALIDATION-pipelining.md` (create) | Manual on-device checklist |

Tasks 1–5 below follow this order; each ends green and committable.

---

### Task 1: Batch seam on `ISonuLink` + options

**Files:**
- Modify: `src/Sonulab.Core/Transport/SerialLinkOptions.cs`
- Modify: `src/Sonulab.Core/Transport/ISonuLink.cs`
- Test: `tests/Sonulab.Core.Tests/SendBatchDefaultTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `Task<IReadOnlyList<string>> ISonuLink.SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)`; `SerialLinkOptions.PipelineEnabled` (bool, default `true`), `.PipelineMinPaceMs` (int, default `30`), `.PipelinePollMs` (int, default `3`).

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/SendBatchDefaultTests.cs`:

```csharp
using Sonulab.Core.Transport;
using Xunit;

public class SendBatchDefaultTests
{
    /// <summary>A link that implements ONLY SendAsync — exactly the position TcpSonuLink and the
    /// fakes are in. It must still answer SendBatchAsync correctly, via the default lockstep
    /// implementation on the interface.</summary>
    private sealed class SequentialOnlyLink : ISonuLink
    {
        public readonly List<string> Sent = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            Sent.Add(command);
            return Task.FromResult($"resp:{command}\r\n");
        }
    }

    [Fact]
    public async Task Default_SendBatchAsync_is_one_SendAsync_per_command_in_order()
    {
        var impl = new SequentialOnlyLink();
        ISonuLink link = impl;
        var windows = await link.SendBatchAsync(new[] { "a", "b", "c" });
        Assert.Equal(new[] { "a", "b", "c" }, impl.Sent);
        Assert.Equal(new[] { "resp:a\r\n", "resp:b\r\n", "resp:c\r\n" }, windows);
    }

    [Fact]
    public async Task Default_SendBatchAsync_handles_an_empty_command_list()
    {
        ISonuLink link = new SequentialOnlyLink();
        Assert.Empty(await link.SendBatchAsync(Array.Empty<string>()));
    }

    [Fact]
    public void Pipelining_defaults_match_the_probe_proven_values()
    {
        var o = new SerialLinkOptions();
        Assert.True(o.PipelineEnabled);
        Assert.Equal(30, o.PipelineMinPaceMs);   // PROTOCOL.md: 25 ms is the cliff
        Assert.Equal(3, o.PipelinePollMs);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SendBatchDefaultTests`
Expected: compile error — `SendBatchAsync` and the three options do not exist.

- [ ] **Step 3: Add the options**

In `src/Sonulab.Core/Transport/SerialLinkOptions.cs`, append inside the class:

```csharp
    /// <summary>Master switch for paced-overlap pipelining in SerialSonuLink.SendBatchAsync.
    /// false → the lockstep fallback, behaviourally identical to N × SendAsync. This is the
    /// kill switch if a cable/hub/firmware combination turns out to drop at the paced rate.</summary>
    public bool PipelineEnabled { get; init; } = true;

    /// <summary>Hard floor between pipelined sends. 30 ms is the probe-proven pace
    /// (PROTOCOL.md "dread limits &amp; hazards"); at 25 ms the firmware drops commands.
    /// Raise it if a device proves flaky — never lower it.</summary>
    public int PipelineMinPaceMs { get; init; } = 30;

    /// <summary>Read-poll interval inside a batch. The lockstep PollMs (10) is too coarse to
    /// land a 30 ms pace cleanly.</summary>
    public int PipelinePollMs { get; init; } = 3;
```

- [ ] **Step 4: Add the interface member**

Replace the body of `src/Sonulab.Core/Transport/ISonuLink.cs` with:

```csharp
namespace Sonulab.Core.Transport;

public interface ISonuLink
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    void Close();
    Task<string> SendAsync(string command, CancellationToken ct = default); // command WITHOUT trailing NUL

    /// <summary>Sends <paramref name="commands"/> with overlapped timing and returns the raw
    /// response windows collected, split at the device's NUL terminators, in arrival order.
    ///
    /// RESPONSE-PRODUCING COMMANDS ONLY (dread). A silent command (write/dwrite) emits no NUL
    /// and would shift every later window.
    ///
    /// The result is NOT positionally aligned with <paramref name="commands"/>: an unsolicited
    /// record or a dropped response shifts it, and the list may be SHORTER than the input when
    /// the deadline hits. Callers MUST identify each response by its own content — for dread,
    /// ResponseParser.ChunkHex(raw, index, chunk) verifies both fields — and never by position.
    ///
    /// The default implementation is a plain lockstep loop, so links with no pipelining support
    /// (TCP, fakes) are correct with no code of their own.</summary>
    async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        var windows = new List<string>(commands.Count);
        foreach (var c in commands) windows.Add(await SendAsync(c, ct));
        return windows;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SendBatchDefaultTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Run the full suite — nothing may regress**

Run: `dotnet test`
Expected: 651 passed, 0 failed (648 baseline + 3 new)

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/Transport/ISonuLink.cs src/Sonulab.Core/Transport/SerialLinkOptions.cs tests/Sonulab.Core.Tests/SendBatchDefaultTests.cs
git commit -m "feat(transport): ISonuLink.SendBatchAsync seam with lockstep default"
```

---

### Task 2: `ScriptedSerialPort` — virtual-clock port double

**Files:**
- Create: `tests/Sonulab.Core.Tests/ScriptedSerialPort.cs`
- Test: `tests/Sonulab.Core.Tests/ScriptedSerialPortTests.cs` (create)

**Interfaces:**
- Consumes: `ISerialPortStream` (`src/Sonulab.Core/Transport/ISerialPortStream.cs`).
- Produces: `ScriptedSerialPort(Func<long> now)` with settable `Responder`, `FirstByteLatencyMs`, `FragmentIntervalMs`, `FragmentSize`, `DropIfSentWithinMs`, `DropWhen`; readable `Received`, `ReceivedAt`, `Dropped`, `DiscardCount`.

Why this exists: `FakeSerialPort` answers instantly and synchronously, so it cannot express *when* a response arrives — which is the entire subject of this feature. This double is driven by a clock the **test** owns; `SerialSonuLink`'s injected delay advances it, making pacing assertions exact instead of flaky.

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/ScriptedSerialPortTests.cs`:

```csharp
using System.Text;
using Xunit;

public class ScriptedSerialPortTests
{
    private static (ScriptedSerialPort port, Action<long> advance) Make()
    {
        long now = 0;
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now));
        return (port, ms => Volatile.Write(ref now, Volatile.Read(ref now) + ms));
    }

    private static void Send(ScriptedSerialPort port, string command)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\0");
        port.Write(bytes, 0, bytes.Length);
    }

    private static string Drain(ScriptedSerialPort port)
    {
        var buf = new byte[4096];
        int n = port.Read(buf, 0, buf.Length);
        return Encoding.ASCII.GetString(buf, 0, n);
    }

    [Fact]
    public void Response_is_invisible_until_the_first_byte_latency_has_elapsed()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 20;
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "dread 1");
        Assert.Equal(0, port.BytesToRead);   // t=0
        advance(19);
        Assert.Equal(0, port.BytesToRead);
        advance(1);                          // t=20
        Assert.Equal("ok:dread 1\0", Drain(port));
    }

    [Fact]
    public void A_command_sent_inside_the_drop_window_is_eaten_and_never_answered()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.DropIfSentWithinMs = 25;        // the firmware's real cliff
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "first");
        advance(10);                         // too soon
        Send(port, "second");
        advance(1000);

        Assert.Equal(new[] { "first" }, port.Received);
        Assert.Equal(new[] { "second" }, port.Dropped);
        Assert.Equal("ok:first\0", Drain(port));
    }

    [Fact]
    public void DropWhen_targets_a_specific_command()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.DropWhen = c => c.Contains("chunk3");
        port.Responder = c => $"ok:{c}\0";
        port.Open("COM6", 115200);

        Send(port, "chunk2");
        Send(port, "chunk3");
        Send(port, "chunk4");
        advance(10);

        Assert.Equal(new[] { "chunk2", "chunk4" }, port.Received);
        Assert.Equal(new[] { "chunk3" }, port.Dropped);
        Assert.Equal("ok:chunk2\0ok:chunk4\0", Drain(port));
    }

    [Fact]
    public void Fragmented_delivery_preserves_byte_order_across_clock_advances()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 0;
        port.FragmentIntervalMs = 5;
        port.FragmentSize = 3;
        port.Responder = _ => "abcdefgh";
        port.Open("COM6", 115200);

        Send(port, "x");
        advance(0);
        Assert.Equal("abc", Drain(port));
        advance(5);
        Assert.Equal("def", Drain(port));
        advance(5);
        Assert.Equal("gh", Drain(port));
    }

    [Fact]
    public void DiscardInBuffer_drops_both_ready_and_scheduled_bytes()
    {
        var (port, advance) = Make();
        port.FirstByteLatencyMs = 10;
        port.Responder = _ => "payload\0";
        port.Open("COM6", 115200);

        Send(port, "x");
        port.DiscardInBuffer();
        advance(1000);

        Assert.Equal(0, port.BytesToRead);
        Assert.Equal(1, port.DiscardCount);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter ScriptedSerialPortTests`
Expected: compile error — `ScriptedSerialPort` does not exist.

- [ ] **Step 3: Write the double**

Create `tests/Sonulab.Core.Tests/ScriptedSerialPort.cs`:

```csharp
using System.Text;
using Sonulab.Core.Transport;

/// <summary>Serial-port double with a VIRTUAL clock, for testing paced/overlapped sends.
/// The test owns the clock and SerialSonuLink's injected delay advances it, so timing
/// assertions are exact rather than flaky. Models the three device behaviours that matter:
/// response latency, fragmented delivery, and the drop-if-sent-too-soon cliff
/// (PROTOCOL.md: commands paced under ~25 ms are eaten by the firmware).</summary>
public sealed class ScriptedSerialPort : ISerialPortStream
{
    private readonly Func<long> _now;
    private readonly List<(long At, byte[] Bytes)> _scheduled = new();
    private readonly List<byte> _cmdBuf = new();
    private readonly Queue<byte> _ready = new();
    private long _lastCommandAt = long.MinValue;

    public ScriptedSerialPort(Func<long> now) => _now = now;

    /// <summary>Maps a received command (NUL stripped) to its response text. Include the
    /// device's trailing NUL in the returned string when a terminator is wanted.</summary>
    public Func<string, string> Responder { get; set; } = _ => "";

    /// <summary>Virtual ms between a command arriving and its response starting to arrive.</summary>
    public int FirstByteLatencyMs { get; set; } = 10;

    /// <summary>Virtual ms between successive response fragments. 0 = deliver the whole
    /// response at once.</summary>
    public int FragmentIntervalMs { get; set; }

    /// <summary>Bytes per fragment when FragmentIntervalMs &gt; 0.</summary>
    public int FragmentSize { get; set; } = 64;

    /// <summary>Commands arriving less than this many virtual ms after the previous command are
    /// DROPPED — no response, ever. 0 disables.</summary>
    public int DropIfSentWithinMs { get; set; }

    /// <summary>Targeted drop: any command matching this predicate is eaten. Independent of
    /// DropIfSentWithinMs.</summary>
    public Func<string, bool>? DropWhen { get; set; }

    public List<string> Received { get; } = new();
    public List<long> ReceivedAt { get; } = new();
    public List<string> Dropped { get; } = new();
    public int DiscardCount { get; private set; }

    public bool IsOpen { get; private set; }
    public void Open(string portName, int baudRate) => IsOpen = true;
    public void Close() => IsOpen = false;

    public void DiscardInBuffer()
    {
        DiscardCount++;
        _ready.Clear();
        _scheduled.Clear();
    }

    public int BytesToRead { get { Pump(); return _ready.Count; } }

    public int Read(byte[] buffer, int offset, int count)
    {
        Pump();
        int i = 0;
        while (i < count && _ready.Count > 0) buffer[offset + i++] = _ready.Dequeue();
        return i;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = buffer[offset + i];
            if (b != 0) { _cmdBuf.Add(b); continue; }

            var cmd = Encoding.ASCII.GetString(_cmdBuf.ToArray());
            _cmdBuf.Clear();
            long at = _now();
            bool tooSoon = DropIfSentWithinMs > 0 && _lastCommandAt != long.MinValue
                           && at - _lastCommandAt < DropIfSentWithinMs;
            _lastCommandAt = at;
            if (tooSoon || DropWhen?.Invoke(cmd) == true) { Dropped.Add(cmd); continue; }

            Received.Add(cmd);
            ReceivedAt.Add(at);
            Schedule(at + FirstByteLatencyMs, Encoding.ASCII.GetBytes(Responder(cmd)));
        }
    }

    private void Schedule(long startAt, byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (FragmentIntervalMs <= 0) { _scheduled.Add((startAt, bytes)); return; }
        for (int off = 0, f = 0; off < bytes.Length; off += FragmentSize, f++)
        {
            int len = Math.Min(FragmentSize, bytes.Length - off);
            var frag = new byte[len];
            Array.Copy(bytes, off, frag, 0, len);
            _scheduled.Add((startAt + (long)f * FragmentIntervalMs, frag));
        }
    }

    /// <summary>Moves everything now due into the readable queue, oldest first. OrderBy is a
    /// stable sort, so fragments scheduled for the same instant keep their order.</summary>
    private void Pump()
    {
        long now = _now();
        var due = _scheduled.Where(s => s.At <= now).OrderBy(s => s.At).ToList();
        if (due.Count == 0) return;
        foreach (var s in due)
            foreach (var b in s.Bytes) _ready.Enqueue(b);
        _scheduled.RemoveAll(s => s.At <= now);
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter ScriptedSerialPortTests`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add tests/Sonulab.Core.Tests/ScriptedSerialPort.cs tests/Sonulab.Core.Tests/ScriptedSerialPortTests.cs
git commit -m "test(transport): virtual-clock serial port double for pacing tests"
```

---

### Task 3: `SerialSonuLink.SendBatchAsync` — self-clocking overlap

**Files:**
- Modify: `src/Sonulab.Core/Transport/SerialSonuLink.cs`
- Test: `tests/Sonulab.Core.Tests/SerialSonuLinkBatchTests.cs` (create)

**Interfaces:**
- Consumes: `ISonuLink.SendBatchAsync` (Task 1), `ScriptedSerialPort` (Task 2).
- Produces: `SerialSonuLink(ISerialPortStream port, string portName, int baudRate, SerialLinkOptions? options = null, Func<long>? tickSource = null, Func<int, CancellationToken, Task>? delay = null)` — the two new parameters are optional, so all existing call sites compile unchanged.

Note on the clock: the default tick source is a **per-instance `Stopwatch`**, not `Environment.TickCount64`. `TickCount64` has ~15.6 ms resolution on Windows, which cannot express a 30 ms pace.

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.Core.Tests/SerialSonuLinkBatchTests.cs`:

```csharp
using Sonulab.Core.Transport;
using Xunit;

public class SerialSonuLinkBatchTests
{
    private const int Pace = 30;

    /// <summary>Builds a link whose clock the test drives. The injected delay advances the
    /// virtual clock by exactly the requested amount, so the link's poll loop makes
    /// deterministic progress with no real waiting.</summary>
    private static (SerialSonuLink link, ScriptedSerialPort port, Func<long> now) Make(
        SerialLinkOptions? options = null)
    {
        long now = 0;
        var port = new ScriptedSerialPort(() => Volatile.Read(ref now));
        var opts = options ?? new SerialLinkOptions
        {
            PipelineMinPaceMs = Pace, PipelinePollMs = 1,
            PollMs = 1, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 100,
        };
        var link = new SerialSonuLink(port, "COM6", 115200, opts,
            tickSource: () => Volatile.Read(ref now),
            delay: (ms, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref now, Volatile.Read(ref now) + ms);
                return Task.CompletedTask;
            });
        return (link, port, () => Volatile.Read(ref now));
    }

    private static string Cmd(int chunk) => $"dread root\\presets:{{\"index\":0,\"chunk\":{chunk}}}";
    private static string[] Cmds(int count) => Enumerable.Range(1, count).Select(Cmd).ToArray();

    /// <summary>Device answers every dread with a record for the chunk it asked for.</summary>
    private static void RespondNormally(ScriptedSerialPort port) =>
        port.Responder = c =>
        {
            var chunk = c.Split("\"chunk\":")[1].TrimEnd('}');
            return $"root\\presets:{{\"index\":0,\"chunk\":{chunk},\"value\":\"aa\"}}\r\n\0";
        };

    [Fact]
    public async Task Returns_one_window_per_command_in_order()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(4));

        Assert.Equal(4, windows.Count);
        for (int i = 0; i < 4; i++) Assert.Contains($"\"chunk\":{i + 1}", windows[i]);
        Assert.Equal(4, port.Received.Count);
        Assert.Empty(port.Dropped);
    }

    [Fact]
    public async Task Never_sends_faster_than_the_pace_floor()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 1;          // device answers instantly — only the floor holds us back
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(6));

        Assert.Equal(6, port.ReceivedAt.Count);
        for (int i = 1; i < port.ReceivedAt.Count; i++)
            Assert.True(port.ReceivedAt[i] - port.ReceivedAt[i - 1] >= Pace,
                $"send {i} came {port.ReceivedAt[i] - port.ReceivedAt[i - 1]}ms after the previous — under the {Pace}ms floor");
    }

    [Fact]
    public async Task Self_clocks_on_the_first_response_byte_rather_than_the_pace_alone()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 90;         // much slower than the 30ms floor
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(4));

        // If we sent purely on the pace floor, gaps would be ~30ms. Waiting for the first byte
        // means each gap is at least the device's latency.
        for (int i = 1; i < port.ReceivedAt.Count; i++)
            Assert.True(port.ReceivedAt[i] - port.ReceivedAt[i - 1] >= 90,
                $"send {i} did not wait for the previous response to start ({port.ReceivedAt[i] - port.ReceivedAt[i - 1]}ms)");
    }

    [Fact]
    public async Task Discards_the_input_buffer_once_before_the_first_send()
    {
        // A mid-batch discard would destroy in-flight responses; lockstep SendAsync discards
        // per command, the batch must not.
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        await link.SendBatchAsync(Cmds(5));

        Assert.Equal(1, port.DiscardCount);
    }

    [Fact]
    public async Task Keeps_sending_after_the_device_eats_a_command()
    {
        var (link, port, _) = Make();
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        port.DropWhen = c => c.Contains("\"chunk\":3");
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(5));

        Assert.Equal(new[] { 1, 2, 4, 5 }, port.Received
            .Select(c => int.Parse(c.Split("\"chunk\":")[1].TrimEnd('}'))).ToArray());
        Assert.Equal(4, windows.Count);                       // chunk 3 simply never arrives
        Assert.DoesNotContain(windows, w => w.Contains("\"chunk\":3"));
    }

    [Fact]
    public async Task Pipelining_disabled_falls_back_to_lockstep()
    {
        var (link, port, _) = Make(new SerialLinkOptions
        {
            PipelineEnabled = false,
            PollMs = 1, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 100,
        });
        RespondNormally(port);
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(3));

        Assert.Equal(3, windows.Count);
        Assert.Equal(3, port.DiscardCount);   // SendAsync discards per command — proves the fallback ran
    }

    [Fact]
    public async Task Returns_a_short_list_when_the_device_stops_answering()
    {
        var (link, port, _) = Make();
        port.FirstByteLatencyMs = 5;
        port.Responder = c => c.Contains("\"chunk\":1")
            ? "root\\presets:{\"index\":0,\"chunk\":1,\"value\":\"aa\"}\r\n\0"
            : "";                              // silence from chunk 2 on
        await link.OpenAsync();

        var windows = await link.SendBatchAsync(Cmds(4));

        Assert.Single(windows);                // deadline reached, no hang
    }

    [Fact]
    public async Task Throws_if_the_port_is_not_open()
    {
        var (link, _, _) = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendBatchAsync(Cmds(2)));
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        var (link, port, _) = Make();
        port.Responder = _ => "";              // never answers, so the loop keeps polling
        port.FirstByteLatencyMs = 5;
        await link.OpenAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => link.SendBatchAsync(Cmds(4), cts.Token));
    }

    [Fact]
    public async Task Empty_command_list_returns_empty()
    {
        var (link, _, _) = Make();
        await link.OpenAsync();
        Assert.Empty(await link.SendBatchAsync(Array.Empty<string>()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SerialSonuLinkBatchTests`
Expected: compile error — the `tickSource`/`delay` constructor parameters and `SendBatchAsync` override do not exist.

- [ ] **Step 3: Add the clock seam to `SerialSonuLink`**

In `src/Sonulab.Core/Transport/SerialSonuLink.cs`, replace the fields and constructor with:

```csharp
    private static readonly byte[] Nul = { 0 };
    private readonly ISerialPortStream _port;
    private readonly string _portName;
    private readonly int _baud;
    private readonly SerialLinkOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<long> _tick;
    private readonly Func<int, CancellationToken, Task> _delay;

    /// <param name="tickSource">Millisecond clock for the response-collection loops. Defaults to
    /// this instance's Stopwatch — NOT Environment.TickCount64, whose ~15.6 ms Windows resolution
    /// cannot express the 30 ms pipelining pace. Tests inject a virtual clock.</param>
    /// <param name="delay">Awaited between read polls. Defaults to Task.Delay; tests inject a
    /// function that advances the virtual clock instead of really waiting.</param>
    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate,
        SerialLinkOptions? options = null, Func<long>? tickSource = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _port = port; _portName = portName; _baud = baudRate; _options = options ?? new SerialLinkOptions();
        _tick = tickSource ?? (() => _clock.ElapsedMilliseconds);
        _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
    }
```

Then rewrite `SendAsync`'s timing to use the seam (same logic, same defaults — `Stopwatch` elapsed becomes a tick delta):

```csharp
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        _port.DiscardInBuffer();
        var bytes = Encoding.ASCII.GetBytes(command);
        _port.Write(bytes, 0, bytes.Length);
        _port.Write(Nul, 0, 1);

        var sb = new StringBuilder();
        long start = _tick();
        long lastData = 0;
        bool sawData = false;

        while (_tick() - start < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _port.Read(buf, 0, avail);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = _tick() - start;
                // Device terminates each response with a NUL byte — stop as soon as we see it
                // (deterministic and size-independent; the idle gap below is only a fallback).
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) break;
            }
            else
            {
                if (sawData && _tick() - start - lastData >= _options.IdleGapMs) break;
                // No-response command (e.g. a write): if nothing has arrived by the first-byte
                // timeout, stop instead of blocking the full MaxWaitMs.
                if (!sawData && _tick() - start >= _options.FirstByteTimeoutMs) break;
                await _delay(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }
```

Also make sure `using System.Diagnostics;` is still present at the top of the file (it is — `Stopwatch` is now a field).

- [ ] **Step 4: Implement `SendBatchAsync`**

Append to `SerialSonuLink` (after `SendAsync`):

```csharp
    /// <summary>Paced-overlap pipelining (PROTOCOL.md "dread limits &amp; hazards"): the firmware
    /// drops zero-gap command bursts, but it DOES accept the next command while still streaming
    /// the previous response. So we self-clock — send N+1 once the first byte of response N has
    /// arrived — with PipelineMinPaceMs as a hard floor (30 ms proven; 25 ms is the cliff).
    /// Measured ~33 ms/chunk vs ~57 lockstep.
    ///
    /// Per the interface contract, the returned windows are NOT positionally aligned with
    /// <paramref name="commands"/>; callers match responses by content.</summary>
    public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        if (commands.Count == 0) return Array.Empty<string>();
        if (!_options.PipelineEnabled || commands.Count == 1)
        {
            var seq = new List<string>(commands.Count);
            foreach (var c in commands) seq.Add(await SendAsync(c, ct));
            return seq;
        }

        // ONE discard, before the first send. A mid-batch discard would destroy responses that
        // are still in flight — which is exactly what pipelining creates.
        _port.DiscardInBuffer();

        var windows = new List<string>(commands.Count);
        var pending = new StringBuilder();          // bytes accumulated since the last NUL
        var buf = new byte[4096];
        int sent = 0;
        long lastSendAt = 0;
        bool sawByteSinceSend = false;
        long start = _tick();
        long deadline = _options.MaxWaitMs + (long)_options.PipelineMinPaceMs * commands.Count;

        while (_tick() - start < deadline && (sent < commands.Count || windows.Count < commands.Count))
        {
            ct.ThrowIfCancellationRequested();

            long now = _tick();
            bool paceOk = sent == 0 || now - lastSendAt >= _options.PipelineMinPaceMs;
            // Self-clock: the previous response has STARTED arriving, so the device is listening
            // again. FirstByteTimeoutMs is the escape hatch — if the device ate the previous
            // command, nothing will ever arrive and the batch must not stall on it.
            bool previousMoving = sent == 0 || sawByteSinceSend
                                  || now - lastSendAt >= _options.FirstByteTimeoutMs;
            if (sent < commands.Count && paceOk && previousMoving)
            {
                var bytes = Encoding.ASCII.GetBytes(commands[sent]);
                _port.Write(bytes, 0, bytes.Length);
                _port.Write(Nul, 0, 1);
                sent++;
                lastSendAt = _tick();
                sawByteSinceSend = false;
                continue;
            }

            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                int n = _port.Read(buf, 0, Math.Min(avail, buf.Length));
                sawByteSinceSend = true;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == 0) { windows.Add(pending.ToString()); pending.Clear(); }
                    else pending.Append((char)buf[i]);
                }
            }
            else await _delay(_options.PipelinePollMs, ct);
        }
        return windows;
    }
```

- [ ] **Step 5: Run the batch tests**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SerialSonuLinkBatchTests`
Expected: PASS (10 tests)

- [ ] **Step 6: Run the existing serial tests — the `SendAsync` refactor must be invisible**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SerialSonuLinkTests`
Expected: PASS (5 tests, unchanged)

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: 666 passed, 0 failed (651 + 5 ScriptedSerialPortTests + 10 batch tests)

- [ ] **Step 8: Commit**

```bash
git add src/Sonulab.Core/Transport/SerialSonuLink.cs tests/Sonulab.Core.Tests/SerialSonuLinkBatchTests.cs
git commit -m "feat(transport): self-clocking paced-overlap SendBatchAsync on SerialSonuLink"
```

---

### Task 4: `SonuClient.DReadChunkRangeAsync` — batch, content-match, repair

**Files:**
- Modify: `src/Sonulab.Core/SonuClient.cs`
- Create: `tests/Sonulab.Core.Tests/BatchLinkDouble.cs`
- Test: `tests/Sonulab.Core.Tests/SonuClientBatchReadTests.cs` (create)

**Interfaces:**
- Consumes: `ISonuLink.SendBatchAsync` (Task 1); `ResponseParser.ChunkHex(string raw, int index, int chunk)`; `SonuCommands.DRead(string path, int index, int chunk)`.
- Produces: unchanged public signature `Task<byte[]> SonuClient.DReadChunkRangeAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)`. `DReadBlobAsync` inherits the speedup for free since it already delegates here.

- [ ] **Step 1: Write the test double**

Create `tests/Sonulab.Core.Tests/BatchLinkDouble.cs`:

```csharp
using System.Text.RegularExpressions;
using Sonulab.Core.Transport;

/// <summary>ISonuLink double that answers dreads from an in-memory blob and lets a test distort
/// the BATCH window list — omit a response, tear a value, or shift every position with an
/// unsolicited record — while the lockstep SendAsync repair path answers correctly. This is how
/// we prove the client identifies responses by content and never by position.</summary>
public sealed class BatchLinkDouble : ISonuLink
{
    private readonly byte[] _blob;
    private readonly int _index;

    public BatchLinkDouble(byte[] blob, int index) { _blob = blob; _index = index; }

    public List<string> BatchCommands { get; } = new();
    public List<string> LockstepCommands { get; } = new();

    /// <summary>Chunks the BATCH omits entirely (the firmware ate the command). The lockstep
    /// repair still recovers them.</summary>
    public HashSet<int> DropInBatch { get; } = new();

    /// <summary>Chunks whose batch response carries an odd-length (torn) hex value.</summary>
    public HashSet<int> TearInBatch { get; } = new();

    /// <summary>Chunks that never answer on ANY lane — batch or repair.</summary>
    public HashSet<int> DropEverywhere { get; } = new();

    /// <summary>Prepend an unsolicited record, shifting every batch window position by one.</summary>
    public bool InjectExtraWindow { get; set; }

    public bool IsOpen => true;
    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Close() { }

    public Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        LockstepCommands.Add(command);
        int chunk = ChunkOf(command);
        return Task.FromResult(DropEverywhere.Contains(chunk) ? "" : Window(chunk, tear: false));
    }

    public Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        BatchCommands.AddRange(commands);
        var windows = new List<string>();
        if (InjectExtraWindow) windows.Add("root\\sys\\_meters\\out:{\"value\":-42}\r\n");
        foreach (var c in commands)
        {
            int chunk = ChunkOf(c);
            if (DropInBatch.Contains(chunk) || DropEverywhere.Contains(chunk)) continue;
            windows.Add(Window(chunk, TearInBatch.Contains(chunk)));
        }
        return Task.FromResult<IReadOnlyList<string>>(windows);
    }

    private static int ChunkOf(string command) =>
        int.Parse(Regex.Match(command, @"""chunk"":(-?\d+)").Groups[1].Value);

    private string Window(int chunk, bool tear)
    {
        var hex = Convert.ToHexStringLower(_blob, (chunk - 1) * 128, 128);
        if (tear) hex = hex[..^1];              // odd length = torn record
        return $"root\\presets:{{\"index\":{_index},\"chunk\":{chunk},\"value\":\"{hex}\"}}\r\n";
    }

    /// <summary>Deterministic 128-byte-per-chunk test blob: chunk C is filled with byte C.</summary>
    public static byte[] MakeBlob(int chunks)
    {
        var blob = new byte[chunks * 128];
        for (int c = 1; c <= chunks; c++)
            for (int i = 0; i < 128; i++) blob[(c - 1) * 128 + i] = (byte)c;
        return blob;
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SonuClientBatchReadTests.cs`:

```csharp
using Sonulab.Core;
using Xunit;

public class SonuClientBatchReadTests
{
    private const string Path = @"root\presets";

    private static (SonuClient client, BatchLinkDouble link, byte[] blob) Make(int chunks = 8)
    {
        var blob = BatchLinkDouble.MakeBlob(chunks);
        var link = new BatchLinkDouble(blob, index: 0);
        return (new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0), link, blob);
    }

    [Fact]
    public async Task Multi_chunk_read_uses_a_single_batch_and_no_lockstep_commands()
    {
        var (client, link, blob) = Make();
        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        Assert.Equal(8, link.BatchCommands.Count);
        Assert.Empty(link.LockstepCommands);
    }

    [Fact]
    public async Task A_chunk_the_batch_dropped_is_repaired_lockstep()
    {
        var (client, link, blob) = Make();
        link.DropInBatch.Add(3);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);                                  // complete despite the drop
        Assert.Single(link.LockstepCommands);
        Assert.Contains("\"chunk\":3", link.LockstepCommands[0]);   // ONLY the missing chunk re-read
    }

    [Fact]
    public async Task A_torn_value_is_treated_as_missing_and_repaired()
    {
        var (client, link, blob) = Make();
        link.TearInBatch.Add(5);                                    // odd-length hex

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        Assert.Single(link.LockstepCommands);
        Assert.Contains("\"chunk\":5", link.LockstepCommands[0]);
    }

    [Fact]
    public async Task Shifted_windows_never_mis_attribute_chunk_data()
    {
        // An unsolicited record plus a dropped response shift every position. If the client
        // trusted window[i] it would silently return the WRONG chunk's bytes here.
        var (client, link, blob) = Make();
        link.InjectExtraWindow = true;
        link.DropInBatch.Add(2);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(blob, bytes);
        for (int c = 1; c <= 8; c++)
            Assert.All(Enumerable.Range(0, 128), i => Assert.Equal((byte)c, bytes[(c - 1) * 128 + i]));
    }

    [Fact]
    public async Task An_unrecoverable_chunk_contributes_zero_bytes_after_two_repair_attempts()
    {
        // Today's permissive contract: the short buffer reaches SlotBlobService's validated
        // wrappers, which fail loudly. The batch path must not change that.
        var (client, link, _) = Make();
        link.DropEverywhere.Add(4);

        var bytes = await client.DReadChunkRangeAsync(Path, 0, 1, 8);

        Assert.Equal(7 * 128, bytes.Length);
        Assert.Equal(2, link.LockstepCommands.Count(c => c.Contains("\"chunk\":4")));
    }

    [Fact]
    public async Task Single_chunk_read_stays_on_the_lockstep_path()
    {
        var (client, link, blob) = Make();
        var bytes = await client.DReadChunkRangeAsync(Path, 0, 3, 1);

        Assert.Equal(blob.Skip(2 * 128).Take(128).ToArray(), bytes);
        Assert.Empty(link.BatchCommands);
        Assert.Single(link.LockstepCommands);
    }

    [Fact]
    public async Task Zero_count_returns_empty_without_touching_the_link()
    {
        var (client, link, _) = Make();
        Assert.Empty(await client.DReadChunkRangeAsync(Path, 0, 1, 0));
        Assert.Empty(link.BatchCommands);
        Assert.Empty(link.LockstepCommands);
    }

    [Fact]
    public async Task DReadBlobAsync_reads_a_full_64_chunk_slot_through_the_batch_path()
    {
        var (client, link, blob) = Make(chunks: 64);
        var bytes = await client.DReadBlobAsync(Path, 0, 64);

        Assert.Equal(blob, bytes);
        Assert.Equal(64, link.BatchCommands.Count);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SonuClientBatchReadTests`
Expected: FAIL — `Multi_chunk_read_uses_a_single_batch_and_no_lockstep_commands` fails with 0 batch commands and 8 lockstep commands (the current implementation loops `SendAsync`).

- [ ] **Step 4: Implement the batch path**

In `src/Sonulab.Core/SonuClient.cs`, add the gated batch helper immediately after the private `SendAsync` method:

```csharp
    /// <summary>Foreground batch: the same gate and quiet-clock bookkeeping as
    /// <see cref="SendAsync"/>, held for the WHOLE burst. Pipelining happens WITHIN a burst;
    /// the background lane's quiet window still governs BETWEEN bursts, so a background scan
    /// can never interleave mid-batch (an interleaved dread is the HwCheck-documented way to
    /// get a commit silently discarded).</summary>
    private async Task<IReadOnlyList<string>> SendBatchGatedAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Volatile.Write(ref _lastForegroundTicks, _tick());
        var sw = Stopwatch.StartNew();
        try { return await _link.SendBatchAsync(commands, ct); }
        finally
        {
            sw.Stop();
            Volatile.Write(ref _lastForegroundTicks, _tick());
            _gate.Release();
            if (Log.IsTraceEnabled)
                Log.Trace("batch {0,5}ms  {1} cmds  {2}", sw.ElapsedMilliseconds, commands.Count,
                    commands.Count == 0 ? "" :
                    commands[0].Length > 70 ? commands[0][..70] + "…" : commands[0]);
        }
    }
```

Then replace `DReadChunkRangeAsync` entirely:

```csharp
    /// <summary>Lockstep re-read attempts for a chunk the pipelined batch did not deliver.
    /// Two covers the observed drop rate; past that the permissive short-buffer contract takes
    /// over and the validated read layer fails loudly.</summary>
    private const int ChunkRepairAttempts = 2;

    /// <summary>Dread chunks [firstChunk .. firstChunk+count-1] (1-based). PERMISSIVE like
    /// DReadBlobAsync: a missing/torn chunk contributes 0 bytes, shortening the result —
    /// callers that need integrity use SlotBlobService's validated wrappers.
    ///
    /// Multi-chunk reads go out as ONE paced-overlap batch (~33 ms/chunk vs ~57 lockstep).
    /// Responses are matched BY CONTENT, never by position: ResponseParser.ChunkHex verifies
    /// index AND chunk, so a window list shifted by an unsolicited record or a dropped response
    /// can never mis-attribute data — it just leaves a chunk unmatched, which the lockstep
    /// repair pass below re-reads.</summary>
    public async Task<byte[]> DReadChunkRangeAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<byte>();

        var hex = new string[count];
        if (count == 1)
        {
            var single = await SendAsync(SonuCommands.DRead(path, index, firstChunk), ct);
            hex[0] = ValidHex(ResponseParser.ChunkHex(single, index, firstChunk));
        }
        else
        {
            var commands = new string[count];
            for (int i = 0; i < count; i++) commands[i] = SonuCommands.DRead(path, index, firstChunk + i);
            var windows = await SendBatchGatedAsync(commands, ct);

            for (int i = 0; i < count; i++)
            {
                hex[i] = "";
                foreach (var w in windows)
                {
                    var h = ValidHex(ResponseParser.ChunkHex(w, index, firstChunk + i));
                    if (h.Length > 0) { hex[i] = h; break; }
                }
            }

            // Repair pass: re-read only what the batch missed, lockstep (~57 ms each), instead
            // of forfeiting the whole batch over one dropped command.
            for (int i = 0; i < count; i++)
            {
                for (int attempt = 1; attempt <= ChunkRepairAttempts && hex[i].Length == 0; attempt++)
                {
                    int chunk = firstChunk + i;
                    Log.Debug("pipelined dread missed {0} idx {1} chunk {2} — lockstep repair {3}/{4}",
                        path, index, chunk, attempt, ChunkRepairAttempts);
                    var raw = await SendAsync(SonuCommands.DRead(path, index, chunk), ct);
                    hex[i] = ValidHex(ResponseParser.ChunkHex(raw, index, chunk));
                }
            }
        }

        var bytes = new List<byte>(count * 128);
        foreach (var h in hex) bytes.AddRange(Convert.FromHexString(h));   // "" → 0 bytes, as before
        return bytes.ToArray();
    }

    /// <summary>A torn record can carry an odd-length hex value; Convert.FromHexString would
    /// throw past every caller. Treat it as missing instead — the resulting short buffer fails
    /// loudly at the validated-read layer.</summary>
    private static string ValidHex(string? hex) => hex is null || (hex.Length & 1) == 1 ? "" : hex;
```

Leave `DReadChunkRangeBackgroundAsync` exactly as it is — but simplify its inline odd-length guard to reuse the new helper if you like; behaviour must not change.

- [ ] **Step 5: Run the batch read tests**

Run: `dotnet test tests/Sonulab.Core.Tests --filter SonuClientBatchReadTests`
Expected: PASS (8 tests)

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: 674 passed, 0 failed (666 + 8). Pay attention to `SonuClientTests`, `SlotBlobReadValidationTests`, `BackupServiceTests`, `AmpServiceTests`, `IrServiceTests` and `PresetHeadReadTests` — they exercise the read paths through fakes and must be untouched by this change.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/SonuClient.cs tests/Sonulab.Core.Tests/BatchLinkDouble.cs tests/Sonulab.Core.Tests/SonuClientBatchReadTests.cs
git commit -m "perf(client): pipelined multi-chunk dread with content matching and lockstep repair"
```

---

### Task 5: Background-lane guard + documentation

**Files:**
- Modify: `tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs`
- Create: `docs/HARDWARE-VALIDATION-pipelining.md`
- Modify: `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `SonuClient.DReadChunkRangeAsync` (Task 4), `SonuClient.SendBackgroundAsync`.
- Produces: nothing new in code.

`CLAUDE.md` and the next-steps doc are shared with the parallel agent's branch. Keep both edits to the minimum lines described; if git reports a conflict at merge time, resolve by keeping **both** sides' changes.

- [ ] **Step 1: Write the failing test**

Append to `tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs`, inside the class:

```csharp
    /// <summary>Link whose BATCH send blocks until the test releases it, so we can observe what
    /// the background lane is allowed to do while a burst is in flight.</summary>
    private sealed class GatedBatchLink : ISonuLink
    {
        public readonly TaskCompletionSource Release = new();
        public readonly List<string> Commands = new();
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(Window(command));
        }

        public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
        {
            await Release.Task;
            lock (Commands) Commands.AddRange(commands);
            return commands.Select(Window).ToList();
        }

        /// <summary>A well-formed dread reply (128 zero bytes) so the read needs NO repair pass —
        /// otherwise the repair reads would pollute the command count this test asserts on.</summary>
        private static string Window(string command)
        {
            var m = System.Text.RegularExpressions.Regex.Match(command, @"""index"":(\d+),""chunk"":(-?\d+)");
            return m.Success
                ? $"root\\presets:{{\"index\":{m.Groups[1].Value},\"chunk\":{m.Groups[2].Value},\"value\":\"{new string('0', 256)}\"}}\r\n"
                : "";
        }
    }

    [Fact]
    public async Task Background_send_cannot_interleave_inside_a_pipelined_batch()
    {
        // The quiet window is 0 here, so the ONLY thing that can hold the background send back
        // is the client gate — which the batch must hold for the whole burst. An interleaved
        // dread inside a burst is the documented way to get a device commit silently discarded.
        long tick = 0;
        var link = new GatedBatchLink();
        var client = new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0,
            backgroundQuietMs: 0,
            tickSource: () => Volatile.Read(ref tick),
            backgroundPollDelay: _ => Task.Delay(1));

        var batch = client.DReadChunkRangeAsync(@"root\presets", 0, 1, 4);   // takes the gate, blocks
        await Task.Delay(50);
        var bg = client.SendBackgroundAsync(@"read root\sys\_name");
        await Task.Delay(50);

        Assert.False(bg.IsCompleted);
        lock (link.Commands) Assert.Empty(link.Commands);

        link.Release.SetResult();
        await batch;
        await bg.WaitAsync(TimeSpan.FromSeconds(5));
        lock (link.Commands) Assert.Equal(5, link.Commands.Count);   // 4 batched + 1 background
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.Core.Tests --filter Background_send_cannot_interleave_inside_a_pipelined_batch`
Expected: PASS if Task 4's gating is correct. If it FAILS (background command recorded while the batch is blocked), the gate is not held across the batch — fix `SendBatchGatedAsync` before continuing.

This is a guard test: it locks in behaviour Task 4 established. Passing immediately is the expected outcome.

- [ ] **Step 3: Write the hardware validation checklist**

Create `docs/HARDWARE-VALIDATION-pipelining.md`:

```markdown
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
- [ ] **4. Amp slot dump (96 chunks).** Dump one occupied amp slot both ways; byte-compare.
- [ ] **5. IR slot dump (32 chunks).** Dump one occupied IR slot both ways; byte-compare.
- [ ] **6. Repair rate.** Raise the file log target to Debug (`src/Namager.App/Logging.cs`) and
      count `pipelined dread missed` lines during a full backup. Above ~1 % of chunks means the
      30 ms floor is too aggressive on this hardware — raise `PipelineMinPaceMs` to 35 or 40 and
      re-run steps 2–3.
- [ ] **7. Live-preset sanity.** With a preset selected and audible, run a backup with pipelining
      ON. Audio must not glitch and the pedal must stay responsive.
- [ ] **8. Record the numbers** in `docs/perf-findings.md` (before/after, per-chunk ms, repair
      rate) and mark this checklist done with the date.

## Expected

~57 ms/chunk → ~33 ms/chunk (~1.7×). A 30-slot preset backup is 1920 chunks: roughly 110 s → 63 s.
```

- [ ] **Step 4: Update the next-steps doc**

In `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`, replace the heading of item 2 and add a status line directly beneath it:

```markdown
## 2. Paced-overlap serial pipelining (~1.7× on every bulk read) — BUILT 2026-07-24, hardware validation pending

**Status:** implemented on `worktree-feat-serial-pipelining` (transport + `SonuClient`
foreground bulk read only). Manual on-device checks: `docs/HARDWARE-VALIDATION-pipelining.md`.
**Deferred follow-up:** the usage scan is NOT accelerated. `DeviceRepository.ReadPresetHeadAsync`
requests one chunk per call so it can stop as soon as the amp/IR refs are complete; batching it
means grouping requests and over-reading up to `group-1` chunks past that stop point, and it
touches the scan path. Worth doing once the usage-map work has landed — a group of 4 would take
the scan from ~14 s to ~8 s.
```

- [ ] **Step 5: Update CLAUDE.md**

In `CLAUDE.md`, find the line under "Not done" reading:

```
Ranked follow-ups (dswap-based reorder ~7× faster, paced serial pipelining ~1.7×, riding review
minors): `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`.
```

Replace with:

```
Ranked follow-ups (dswap-based reorder ~7× faster, riding review minors):
`docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`. Paced serial pipelining is BUILT
(multi-chunk foreground `dread` overlaps sends at a 30 ms floor with lockstep repair; kill switch
`SerialLinkOptions.PipelineEnabled`) — on-device checks pending in
`docs/HARDWARE-VALIDATION-pipelining.md`. The background usage scan is deliberately not pipelined.
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: 675 passed, 0 failed (674 + 1 background-lane guard)

- [ ] **Step 7: Commit**

```bash
git add tests/Sonulab.Core.Tests/SonuClientBackgroundLaneTests.cs docs/HARDWARE-VALIDATION-pipelining.md docs/superpowers/2026-07-24-post-scan-fix-next-steps.md CLAUDE.md
git commit -m "test(client): background lane cannot interleave mid-batch; document pipelining"
```

---

## Done criteria

- `dotnet test` → 675 passed, 0 failed.
- A 64-chunk foreground blob read issues exactly one `SendBatchAsync` and, absent drops, zero lockstep repairs.
- `PipelineEnabled = false` restores today's behaviour with no code change.
- `docs/HARDWARE-VALIDATION-pipelining.md` exists and is unrun — the on-device numbers are Ed's to collect.
