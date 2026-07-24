# Device-Disconnect Handling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn an `IOException` mid-batch from an uncaught crash (HwCheck) / a raw unreadable error string plus a permanently-wedged UI (app) into a typed, latched, honestly-reported device disconnect.

**Architecture:** Each transport classifies its own I/O failures, closes its port, and throws a shared `DeviceDisconnectedException`. `SonuClient` — the single gate all device traffic passes through — latches the first one, raises `Disconnected` exactly once, and fails every later send instantly instead of re-attacking a dead port. `SlotBlobService.UploadAsync` enriches the exception with the at-risk slot. `ConnectionViewModel` turns the event into a dead state that disables Connect and explains why. HwCheck prints the same typed message and exits 2.

**Tech Stack:** .NET 10, C#, xUnit, Avalonia 12 (MVVM via CommunityToolkit.Mvvm), NLog.

**Spec:** `docs/superpowers/specs/2026-07-24-device-disconnect-handling-design.md`

## Global Constraints

- **Do NOT add FluentAvalonia** or any third-party icon/UI library (Avalonia 12 + built-in `FluentTheme` only).
- `Sonulab.Core` and `Sonulab.Transport.Wifi` have **no UI dependency** — no Avalonia types may be referenced from them.
- All existing tests must keep passing: `dotnet test` (490 tests at the time of writing).
- `OperationCanceledException` is **never** a disconnect. Every classification predicate must exclude it.
- `TimeoutException` is **never** a disconnect (transient; `SerialPort.Read` is only called after `BytesToRead > 0`).
- Only the **send** paths classify. `OpenAsync` is left alone so "VoidX-Control is holding COM6" (`UnauthorizedAccessException` at open) stays a probe failure.
- Exception message wording is fixed by the spec: `Device disconnected (USB).` and `Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.` (em dash, not hyphen).
- Status text is fixed by the spec: `Device disconnected — reconnect the pedal and restart NAMager` (em dash).
- Transport strings are exactly `"USB"` and `"WiFi"`.
- UI colors come from `Styles/SonulabTheme.axaml` tokens — no hex literals in `.axaml`. (No view changes are expected in this plan, but the rule stands.)

## Deviations from the spec (plan-time refinements)

Two naming decisions changed while mapping the spec onto the real code. Both are recorded here rather than silently applied:

1. **`SlotNoun`, not `SlotPath`.** The spec's §1 named a `SlotPath` property (`root\amp`), but the message it specifies says "Amp slot 12". `SlotBlobKind.Noun` (`src/Sonulab.Core/Services/SlotBlobService.cs:10`) already carries exactly `"Amp"` / `"IR"`, so the exception carries the noun and no path-to-noun mapping is needed anywhere.
2. **Enrichment lands in `SlotBlobService` only**, not in `AmpService` and `IrService` as well. Both of those are thin delegating fronts over a single `SlotBlobService _inner` (verified: `AmpService.cs:38-46`, `IrService.cs:21-30`), so wrapping `SlotBlobService.UploadAsync` covers amps and IRs from one place. Preset writes get the bare message — a disconnect mid-reorder leaves the *order* half-applied, not a slot half-written, so slot attribution would be misleading there.

## File Structure

**Create:**
- `src/Sonulab.Core/Transport/DeviceDisconnectedException.cs` — the typed exception, its message composition, and the `IsFatal` predicate shared by both transports.
- `tests/Sonulab.Core.Tests/DeviceDisconnectedExceptionTests.cs`
- `tests/Sonulab.Core.Tests/SerialSonuLinkDisconnectTests.cs`
- `tests/Sonulab.Core.Tests/SonuClientDisconnectLatchTests.cs`
- `tests/Sonulab.Core.Tests/SlotBlobDisconnectAttributionTests.cs`
- `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkDisconnectTests.cs`
- `tests/Namager.App.Tests/DeviceLostTests.cs`
- `docs/HARDWARE-VALIDATION-disconnect.md`

**Modify:**
- `src/Sonulab.Core/Transport/FakeSerialPort.cs` — add the `OnIo` fault-injection hook.
- `src/Sonulab.Core/Transport/SerialSonuLink.cs:41-79` and `:93-161` — extract cores, add classification wrappers.
- `src/Sonulab.Transport.Wifi/TcpSonuLink.cs:55-…` — same treatment.
- `tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs` — add the `OnIo` hook.
- `src/Sonulab.Core/SonuClient.cs:51`, `:76`, `:251` — latch.
- `src/Sonulab.Core/Services/SlotBlobService.cs:156` — slot attribution.
- `src/Namager.App/ViewModels/ConnectionViewModel.cs` — dead state + dispatch seam.
- `src/Namager.App/ViewModels/MainWindowViewModel.cs:113` — stop the usage scan on device loss.
- `tools/HwCheck/Program.cs` — top-level unhandled-exception guard.
- `CLAUDE.md` — record the new behavior and the pending hardware check.

---

### Task 1: The typed exception

**Files:**
- Create: `src/Sonulab.Core/Transport/DeviceDisconnectedException.cs`
- Test: `tests/Sonulab.Core.Tests/DeviceDisconnectedExceptionTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Sonulab.Core.Transport.DeviceDisconnectedException` with:
  - `DeviceDisconnectedException(string transport, Exception? inner = null, string? slotNoun = null, int? slotIndex = null, bool wasWriting = false)`
  - `string Transport { get; }`, `string? SlotNoun { get; }`, `int? SlotIndex { get; }`, `bool WasWriting { get; }`
  - `DeviceDisconnectedException ForSlot(string noun, int index, bool writing)`
  - `DeviceDisconnectedException Repeat()`
  - `static bool IsFatal(Exception ex)`

  Every later task uses these exact names.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/DeviceDisconnectedExceptionTests.cs`:

```csharp
using System.Net.Sockets;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceDisconnectedExceptionTests
{
    [Fact] public void Bare_message_names_the_transport()
    {
        var ex = new DeviceDisconnectedException("USB");
        Assert.Equal("Device disconnected (USB).", ex.Message);
        Assert.Equal("USB", ex.Transport);
        Assert.Null(ex.SlotNoun);
        Assert.False(ex.WasWriting);
    }

    [Fact] public void Derives_from_IOException_so_existing_catch_sites_still_fire()
    {
        // AmpListViewModel:429 and IrListViewModel:276 already catch IOException and show
        // ex.Message; they must keep working with no edit.
        Assert.IsAssignableFrom<System.IO.IOException>(new DeviceDisconnectedException("USB"));
    }

    [Fact] public void ForSlot_names_the_at_risk_slot_when_writing()
    {
        var ex = new DeviceDisconnectedException("USB").ForSlot("Amp", 12, writing: true);
        Assert.Equal(
            "Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.",
            ex.Message);
        Assert.Equal("Amp", ex.SlotNoun);
        Assert.Equal(12, ex.SlotIndex);
        Assert.True(ex.WasWriting);
    }

    [Fact] public void ForSlot_on_a_read_stays_bare()
    {
        // A dropped read damages nothing — do not scare the user about a slot that is fine.
        var ex = new DeviceDisconnectedException("WiFi").ForSlot("IR", 3, writing: false);
        Assert.Equal("Device disconnected (WiFi).", ex.Message);
        Assert.Equal(3, ex.SlotIndex);
    }

    [Fact] public void ForSlot_preserves_transport_and_inner()
    {
        var inner = new System.IO.IOException("port gone");
        var ex = new DeviceDisconnectedException("WiFi", inner).ForSlot("IR", 1, writing: true);
        Assert.Equal("WiFi", ex.Transport);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact] public void Repeat_returns_a_distinct_instance_wrapping_the_original()
    {
        // SonuClient rethrows a copy so each throw carries its own stack trace instead of
        // overwriting the latched instance's.
        var first = new DeviceDisconnectedException("USB");
        var again = first.Repeat();
        Assert.NotSame(first, again);
        Assert.Same(first, again.InnerException);
        Assert.Equal(first.Message, again.Message);
    }

    [Theory]
    [InlineData(typeof(System.IO.IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void IsFatal_matches_link_death(Type t)
        => Assert.True(DeviceDisconnectedException.IsFatal((Exception)Activator.CreateInstance(t)!));

    [Fact] public void IsFatal_matches_SocketException()
        => Assert.True(DeviceDisconnectedException.IsFatal(new SocketException(10054)));

    [Fact] public void IsFatal_excludes_cancellation()
    {
        // A user/caller cancel is not a disconnect. Misclassifying it would wedge the UI
        // permanently on a routine cancel.
        Assert.False(DeviceDisconnectedException.IsFatal(new OperationCanceledException()));
        Assert.False(DeviceDisconnectedException.IsFatal(new TaskCanceledException()));
    }

    [Fact] public void IsFatal_excludes_timeout_and_already_classified()
    {
        Assert.False(DeviceDisconnectedException.IsFatal(new TimeoutException()));
        Assert.False(DeviceDisconnectedException.IsFatal(new DeviceDisconnectedException("USB")));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~DeviceDisconnectedExceptionTests"`

Expected: build FAILS — `The type or namespace name 'DeviceDisconnectedException' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Sonulab.Core/Transport/DeviceDisconnectedException.cs`:

```csharp
using System.Net.Sockets;

namespace Sonulab.Core.Transport;

/// <summary>The link to the pedal died mid-conversation (cable pulled, pedal powered off, socket
/// reset). Distinct from "this command failed": the transport has already closed its port and
/// SonuClient latches the first one, so nothing further will succeed on this link.
///
/// Derives from IOException deliberately — AmpListViewModel and IrListViewModel already
/// `catch (IOException ex)` and display ex.Message, so they report a disconnect correctly with no
/// edit. The trade-off: a catch written for FILE I/O at those sites will also catch a device drop.
/// Both of those sites already span a file read AND a device upload, so that is their intent.</summary>
public sealed class DeviceDisconnectedException : IOException
{
    /// <summary>"USB" or "WiFi".</summary>
    public string Transport { get; }

    /// <summary>User-facing noun of the slot list in play ("Amp", "IR"), when known.
    /// Supplied by SlotBlobService, which knows it as SlotBlobKind.Noun.</summary>
    public string? SlotNoun { get; }

    public int? SlotIndex { get; }

    /// <summary>True when the drop interrupted a WRITE burst, meaning the slot may be half-written
    /// and the rollback path is dead too. Only then is the slot named in the message.</summary>
    public bool WasWriting { get; }

    public DeviceDisconnectedException(string transport, Exception? inner = null,
        string? slotNoun = null, int? slotIndex = null, bool wasWriting = false)
        : base(Compose(transport, slotNoun, slotIndex, wasWriting), inner)
    {
        Transport = transport;
        SlotNoun = slotNoun;
        SlotIndex = slotIndex;
        WasWriting = wasWriting;
    }

    /// <summary>A copy carrying slot context. Callers that know which slot was in play attach it on
    /// the way out (SlotBlobService.UploadAsync).</summary>
    public DeviceDisconnectedException ForSlot(string noun, int index, bool writing) =>
        new(Transport, InnerException, noun, index, writing);

    /// <summary>A fresh instance wrapping this one, for SonuClient's latch. Rethrowing a single
    /// stored instance would overwrite its stack trace on every throw.</summary>
    public DeviceDisconnectedException Repeat() =>
        new(Transport, this, SlotNoun, SlotIndex, WasWriting);

    private static string Compose(string transport, string? noun, int? index, bool writing)
    {
        var s = $"Device disconnected ({transport}).";
        if (writing && noun is not null && index is not null)
            s += $" {noun} slot {index} may be partially written — verify it after reconnecting.";
        return s;
    }

    /// <summary>Does this exception mean the link is dead? Shared by SerialSonuLink and
    /// TcpSonuLink so there is ONE definition.
    ///
    /// Excludes cancellation (a routine user cancel must not wedge the session) and
    /// TimeoutException (transient — Read is only called after BytesToRead > 0, so it should not
    /// fire, and if it does it is not proof the device is gone). Already-classified exceptions
    /// pass through unwrapped.</summary>
    public static bool IsFatal(Exception ex) =>
        ex is not DeviceDisconnectedException
        && ex is not OperationCanceledException
        && ex is IOException or ObjectDisposedException or UnauthorizedAccessException
              or InvalidOperationException or SocketException;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~DeviceDisconnectedExceptionTests"`

Expected: PASS (13 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Transport/DeviceDisconnectedException.cs tests/Sonulab.Core.Tests/DeviceDisconnectedExceptionTests.cs
git commit -m "feat(core): typed DeviceDisconnectedException with slot attribution"
```

---

### Task 2: Serial transport classifies its own death

**Files:**
- Modify: `src/Sonulab.Core/Transport/FakeSerialPort.cs`
- Modify: `src/Sonulab.Core/Transport/SerialSonuLink.cs:41-79` (`SendAsync`), `:93-161` (`SendBatchAsync`)
- Test: `tests/Sonulab.Core.Tests/SerialSonuLinkDisconnectTests.cs`

**Interfaces:**
- Consumes: `DeviceDisconnectedException(string, Exception?)`, `DeviceDisconnectedException.IsFatal` (Task 1).
- Produces: `FakeSerialPort.OnIo` (`Action<string>?`, invoked with `"write"` / `"read"` / `"bytes"` / `"discard"`). `SerialSonuLink.SendAsync` and `SendBatchAsync` now throw `DeviceDisconnectedException("USB", inner)` and leave `IsOpen == false`.

- [ ] **Step 1: Add the fault-injection hook to `FakeSerialPort`**

This is test infrastructure, not behavior — no test drives it directly, Step 2's tests do.

In `src/Sonulab.Core/Transport/FakeSerialPort.cs`, add the property after `Responder` (line 16):

```csharp
    /// <summary>Fault injection for disconnect tests: invoked before each I/O operation with
    /// "write", "read", "bytes" or "discard". Throw from here to simulate a yanked cable.
    /// Deliberately general — a test counts calls itself and throws whatever it wants.</summary>
    public Action<string>? OnIo { get; set; }
```

Then invoke it at the top of each I/O member:

```csharp
    public void DiscardInBuffer() { OnIo?.Invoke("discard"); _in.Clear(); }
    public int BytesToRead { get { OnIo?.Invoke("bytes"); return _in.Count; } }
```

and as the first line of the existing `Write(byte[], int, int)` and `Read(byte[], int, int)` bodies:

```csharp
        OnIo?.Invoke("write");
```
```csharp
        OnIo?.Invoke("read");
```

Do NOT invoke it from `Open`/`Close` — `Close` is called from the fault handler itself, and invoking there would risk re-entrancy in tests.

- [ ] **Step 2: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SerialSonuLinkDisconnectTests.cs`:

```csharp
using System.IO;
using Sonulab.Core.Transport;
using Xunit;

public class SerialSonuLinkDisconnectTests
{
    static SerialLinkOptions Fast => new()
    { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 500, FirstByteTimeoutMs = 50,
      PipelineMinPaceMs = 1, PipelinePollMs = 1 };

    static string[] Commands(int n)
    {
        var c = new string[n];
        for (int i = 0; i < n; i++) c[i] = $@"dread root\amp:{{""index"":0,""chunk"":{i + 1}}}";
        return c;
    }

    [Fact] public async Task SendAsync_translates_a_write_IOException()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.Equal("USB", ex.Transport);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact] public async Task SendAsync_closes_the_port_so_IsOpen_stops_lying()
    {
        // The zombie-link bug: SerialPort.IsOpen stays true after an unplug until someone calls
        // Close(). Nothing did, so every later operation re-attacked a dead handle.
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        Assert.True(link.IsOpen);

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task SendBatchAsync_translates_a_mid_batch_IOException()
    {
        // The reported failure. FakeSerialPort.Write is called TWICE per command (payload, then
        // the NUL), so write #5 lands partway through the third command of a ten-command batch.
        int writes = 0;
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "write" && ++writes == 5) throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => link.SendBatchAsync(Commands(10)));
        Assert.Equal("USB", ex.Transport);
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task SendBatchAsync_translates_a_read_IOException()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        port.OnIo = op => { if (op == "read") throw new IOException("device removed"); };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendBatchAsync(Commands(10)));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task Cancellation_is_not_a_disconnect()
    {
        var port = new FakeSerialPort { Responder = _ => "root\\x:{\"value\":1}\0" };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => link.SendBatchAsync(Commands(10), cts.Token));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
        Assert.True(link.IsOpen);   // a cancel must NOT close the port
    }

    [Fact] public async Task Not_open_guard_still_reports_a_caller_bug_not_a_disconnect()
    {
        // IsFatal matches InvalidOperationException (SerialPort raises it on a closed handle), so
        // the link's own precondition check must sit OUTSIDE the classification try.
        var link = new SerialSonuLink(new FakeSerialPort(), "COM6", 115200, Fast);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read x"));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SerialSonuLinkDisconnectTests"`

Expected: FAIL — the disconnect tests report `IOException` where `DeviceDisconnectedException` was expected.

- [ ] **Step 4: Implement the classification**

In `src/Sonulab.Core/Transport/SerialSonuLink.cs`, **rename** the existing `SendAsync` body to `SendCoreAsync` and add a thin wrapper. Do not reindent the body — move it wholesale. The `if (!_port.IsOpen)` guard moves UP into the wrapper.

Replace line 41's signature block so it reads:

```csharp
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        // The precondition check stays OUTSIDE the try: IsFatal matches InvalidOperationException
        // (SerialPort raises it on a closed handle), so leaving this inside would silently
        // reclassify a caller bug as a device disconnect.
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        try { return await SendCoreAsync(command, ct); }
        catch (Exception ex) when (DeviceDisconnectedException.IsFatal(ex)) { throw Fault(ex); }
    }

    private async Task<string> SendCoreAsync(string command, CancellationToken ct)
    {
        _port.DiscardInBuffer();
        // ... the rest of the ORIGINAL SendAsync body, unchanged, from `var bytes = ...`
        //     through `return sb.ToString();`
    }
```

Apply the identical treatment to `SendBatchAsync` (line 93). Keep the existing XML doc comment on the public `SendBatchAsync`; the core gets no doc comment.

```csharp
    public async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        if (commands.Count == 0) return Array.Empty<string>();
        try { return await SendBatchCoreAsync(commands, ct); }
        catch (Exception ex) when (DeviceDisconnectedException.IsFatal(ex)) { throw Fault(ex); }
    }

    private async Task<IReadOnlyList<string>> SendBatchCoreAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        if (!_options.PipelineEnabled || commands.Count == 1)
        {
            var seq = new List<string>(commands.Count);
            foreach (var c in commands) seq.Add(await SendAsync(c, ct));
            return seq;
        }
        // ... the rest of the ORIGINAL SendBatchAsync body, unchanged, from the
        //     `// ONE discard, before the first send.` comment through `return windows;`
    }
```

Note the lockstep fallback inside `SendBatchCoreAsync` still calls the **public** `SendAsync`, which classifies on its own. `IsFatal` returns false for an already-classified `DeviceDisconnectedException`, so it propagates out of the batch wrapper unwrapped — exactly right.

Add the fault helper at the end of the class, before the closing brace:

```csharp
    /// <summary>Close the port and translate. Closing is what stops IsOpen from lying — a real
    /// SerialPort reports IsOpen == true after an unplug until someone closes it.</summary>
    private DeviceDisconnectedException Fault(Exception inner)
    {
        try { _port.Close(); } catch { /* already gone — the throw below is the real signal */ }
        return new DeviceDisconnectedException("USB", inner);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SerialSonuLink"`

Expected: PASS — the new disconnect tests plus every pre-existing `SerialSonuLinkTests`, `SerialSonuLinkBatchTests`, and `SerialSonuLinkPacingPrecisionTests` case.

- [ ] **Step 6: Run the full Core suite for regressions**

Run: `dotnet test tests/Sonulab.Core.Tests`

Expected: PASS. `SonuConnectorTests` matters most here — `SonuConnector:49` already catches everything per port, so a probe-time `DeviceDisconnectedException` must still advance to the next port with no code change.

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.Core/Transport/FakeSerialPort.cs src/Sonulab.Core/Transport/SerialSonuLink.cs tests/Sonulab.Core.Tests/SerialSonuLinkDisconnectTests.cs
git commit -m "feat(core): SerialSonuLink classifies link death and closes the port"
```

---

### Task 3: WiFi transport classifies its own death

**Files:**
- Modify: `src/Sonulab.Transport.Wifi/TcpSonuLink.cs:55` (`SendAsync`)
- Modify: `tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs`
- Test: `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkDisconnectTests.cs`

**Interfaces:**
- Consumes: `DeviceDisconnectedException`, `.IsFatal` (Task 1).
- Produces: `FakeTcpConn.OnIo` (`Action<string>?`, invoked with `"send"` / `"receive"`). `TcpSonuLink.SendAsync` throws `DeviceDisconnectedException("WiFi", inner)` and closes the socket.

- [ ] **Step 1: Add the fault hook to `FakeTcpConn`**

In `tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs`, add after `RespondWith` (line 15):

```csharp
    /// <summary>Fault injection for disconnect tests: invoked with "send" or "receive" before
    /// each operation. Throw from here to simulate a dropped socket.</summary>
    public Action<string>? OnIo { get; set; }
```

Add `OnIo?.Invoke("send");` as the first line of `SendAsync`, and `OnIo?.Invoke("receive");` as the first line of `Receive`.

- [ ] **Step 2: Write the failing tests**

Create `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkDisconnectTests.cs`:

```csharp
using System.Net.Sockets;
using Sonulab.Core.Transport;
using Sonulab.Transport.Wifi;
using Xunit;

namespace Sonulab.Transport.Wifi.Tests;

public class TcpSonuLinkDisconnectTests
{
    static TcpLinkOptions Fast => new() { PollMs = 2, MaxWaitMs = 300, ConnectTimeoutMs = 200 };

    [Fact] public async Task SendAsync_translates_a_socket_reset()
    {
        var conn = new FakeTcpConn { RespondWith = _ => "root\\x:{\"value\":1}\0"u8.ToArray() };
        var link = new TcpSonuLink(conn, "10.0.0.5", 8080, Fast);
        await link.OpenAsync();
        conn.OnIo = op => { if (op == "send") throw new SocketException(10054); };  // ECONNRESET

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.Equal("WiFi", ex.Transport);
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact] public async Task SendAsync_closes_the_socket_on_fault()
    {
        var conn = new FakeTcpConn { RespondWith = _ => "root\\x:{\"value\":1}\0"u8.ToArray() };
        var link = new TcpSonuLink(conn, "10.0.0.5", 8080, Fast);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        conn.OnIo = op => { if (op == "send") throw new System.IO.IOException("broken pipe"); };

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => link.SendAsync("read x"));
        Assert.False(link.IsOpen);
    }

    [Fact] public async Task Not_open_guard_still_reports_a_caller_bug()
    {
        var link = new TcpSonuLink(new FakeTcpConn(), "10.0.0.5", 8080, Fast);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read x"));
        Assert.IsNotType<DeviceDisconnectedException>(ex);
    }
}
```

If `TcpLinkOptions` does not expose one of `PollMs` / `MaxWaitMs` / `ConnectTimeoutMs` under those names, match the property names used in the existing `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkTests.cs` — do not invent new options.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Transport.Wifi.Tests --filter "FullyQualifiedName~TcpSonuLinkDisconnectTests"`

Expected: FAIL — raw `SocketException` / `IOException` instead of `DeviceDisconnectedException`.

- [ ] **Step 4: Implement**

In `src/Sonulab.Transport.Wifi/TcpSonuLink.cs`, apply the same extract-and-wrap as Task 2. The guard at line 57 moves into the wrapper; everything from `DrainAvailable();` (line 61) to the method's `return` moves into `SendCoreAsync` unchanged.

```csharp
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        // Outside the try, same reasoning as SerialSonuLink: IsFatal matches
        // InvalidOperationException, so this precondition must not be reclassified.
        if (!_conn.Connected) throw new InvalidOperationException("TCP link is not open.");
        try { return await SendCoreAsync(command, ct); }
        catch (Exception ex) when (DeviceDisconnectedException.IsFatal(ex)) { throw Fault(ex); }
    }

    private async Task<string> SendCoreAsync(string command, CancellationToken ct)
    {
        // ... the ORIGINAL SendAsync body from `DrainAvailable();` onward, unchanged.
    }

    /// <summary>Close the socket and translate. The _pending response-debt counter needs no
    /// unwinding — the link is closed and SonuClient latches it, so nothing reads it again.</summary>
    private DeviceDisconnectedException Fault(Exception inner)
    {
        try { _conn.Close(); } catch { /* already gone */ }
        return new DeviceDisconnectedException("WiFi", inner);
    }
```

Add `using Sonulab.Core.Transport;` at the top if it is not already present.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Transport.Wifi.Tests`

Expected: PASS — new tests plus all pre-existing `TcpSonuLinkTests`, `MdnsMessagesTests`, `WifiLinkProviderTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Transport.Wifi/TcpSonuLink.cs tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkDisconnectTests.cs
git commit -m "feat(wifi): TcpSonuLink classifies link death and closes the socket"
```

---

### Task 4: The latch in `SonuClient`

**Files:**
- Modify: `src/Sonulab.Core/SonuClient.cs:51` (`SendAsync`), `:76` (`SendBatchGatedAsync`), `:251` (`SendBackgroundAsync`)
- Test: `tests/Sonulab.Core.Tests/SonuClientDisconnectLatchTests.cs`

**Interfaces:**
- Consumes: `DeviceDisconnectedException`, `.Repeat()` (Task 1); transports that throw it (Tasks 2–3).
- Produces: `SonuClient.IsDisconnected` (`bool`), `SonuClient.Disconnected` (`event Action<DeviceDisconnectedException>?`). Task 6 subscribes to `Disconnected`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SonuClientDisconnectLatchTests.cs`:

```csharp
using System.IO;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientDisconnectLatchTests
{
    /// <summary>A link that dies on the Nth send and counts how many times it was touched.</summary>
    private sealed class DyingLink(int dieOnSend) : ISonuLink
    {
        public int Sends;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (++Sends >= dieOnSend)
            {
                IsOpen = false;
                throw new DeviceDisconnectedException("USB", new IOException("cable pulled"));
            }
            return Task.FromResult("root\\x:{\"value\":1}\r\n");
        }
    }

    static SonuClient Client(ISonuLink link) =>
        new(link, readRetryAttempts: 1, readRetryDelayMs: 0, backgroundQuietMs: 0,
            tickSource: () => 0, backgroundPollDelay: _ => Task.CompletedTask);

    [Fact] public async Task First_disconnect_surfaces_and_sets_IsDisconnected()
    {
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        Assert.False(c.IsDisconnected);

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        Assert.True(c.IsDisconnected);
    }

    [Fact] public async Task Later_sends_fail_instantly_without_touching_the_dead_link()
    {
        // This is the point of the latch: PresetListViewModel's failure handler immediately calls
        // ReloadAsync(), which used to re-attempt a 30-slot read against a dead port and throw a
        // second raw I/O error.
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        int touchedSoFar = link.Sends;

        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read y"));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.ReadListAsync(@"root\amp"));
        Assert.Equal(touchedSoFar, link.Sends);
    }

    [Fact] public async Task Repeated_failures_carry_the_original_message()
    {
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));
        var again = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read y"));
        Assert.Equal("Device disconnected (USB).", again.Message);
        Assert.Equal("USB", again.Transport);
    }

    [Fact] public async Task Disconnected_is_raised_exactly_once_under_concurrency()
    {
        int raised = 0;
        var c = Client(new DyingLink(dieOnSend: 1));
        c.Disconnected += _ => Interlocked.Increment(ref raised);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await c.SendRawAsync($"read {i}"); } catch (DeviceDisconnectedException) { }
        }));
        await Task.WhenAll(tasks);

        Assert.Equal(1, raised);
    }

    [Fact] public async Task Batch_read_latches_too()
    {
        // Multi-chunk dread goes through SendBatchGatedAsync, a different gate method.
        var link = new DyingLink(dieOnSend: 1);
        var c = Client(link);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => c.DReadChunkRangeAsync(@"root\amp", 0, 1, 8));
        Assert.True(c.IsDisconnected);
    }

    [Fact] public async Task Background_lane_returns_immediately_on_a_latched_client()
    {
        // SendBackgroundAsync calls _link.SendAsync DIRECTLY, bypassing the private SendAsync, and
        // its while(true) quiet-window loop would otherwise keep polling a corpse forever.
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendRawAsync("read x"));

        var background = c.SendBackgroundAsync(@"read root\amp");
        var finished = await Task.WhenAny(background, Task.Delay(2000));
        Assert.Same(background, finished);
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => background);
    }

    [Fact] public async Task Background_lane_latches_its_own_failure()
    {
        var c = Client(new DyingLink(dieOnSend: 1));
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => c.SendBackgroundAsync("read x"));
        Assert.True(c.IsDisconnected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SonuClientDisconnectLatchTests"`

Expected: build FAILS — `'SonuClient' does not contain a definition for 'IsDisconnected'`.

- [ ] **Step 3: Implement the latch**

In `src/Sonulab.Core/SonuClient.cs`, add the fields after `_lastForegroundTicks` (line 20):

```csharp
    private DeviceDisconnectedException? _dead;
```

Add the public surface after the constructor (after line 49):

```csharp
    /// <summary>True once the link has died. Latched — a SonuClient never recovers; the app
    /// requires a restart to reconnect (ConnectionViewModel documents why re-opening a live
    /// session wedges the ESP32).</summary>
    public bool IsDisconnected => Volatile.Read(ref _dead) is not null;

    /// <summary>Raised EXACTLY ONCE, on the thread of the failing send, when the link dies.
    /// Subscribers in a UI must marshal to their own thread.</summary>
    public event Action<DeviceDisconnectedException>? Disconnected;

    private void ThrowIfDead()
    {
        // A copy, not the stored instance: rethrowing one instance overwrites its stack trace
        // on every throw.
        if (Volatile.Read(ref _dead) is { } d) throw d.Repeat();
    }

    private void Latch(DeviceDisconnectedException ex)
    {
        if (Interlocked.CompareExchange(ref _dead, ex, null) is null)
        {
            Log.Error(ex, "device link lost ({0}) — session is dead until the app restarts", ex.Transport);
            Disconnected?.Invoke(ex);
        }
    }
```

Then wire the three call sites. `SendAsync` (line 51):

```csharp
    private async Task<string> SendAsync(string command, CancellationToken ct)
    {
        ThrowIfDead();
        await _gate.WaitAsync(ct);
        Volatile.Write(ref _lastForegroundTicks, _tick());
        var sw = Stopwatch.StartNew();
        try { return await _link.SendAsync(command, ct); }
        catch (DeviceDisconnectedException ex) { Latch(ex); throw; }
        finally
        {
            // ... existing finally block, unchanged
        }
    }
```

`SendBatchGatedAsync` (line 76) — identically, `ThrowIfDead();` as the first statement and:

```csharp
        try { return await _link.SendBatchAsync(commands, ct); }
        catch (DeviceDisconnectedException ex) { Latch(ex); throw; }
        finally
        {
            // ... existing finally block, unchanged
        }
```

`SendBackgroundAsync` (line 251) — the check goes **inside** the loop, right after the cancellation check, so a client latched while this method is parked exits on the next poll:

```csharp
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDead();
            if (_tick() - Volatile.Read(ref _lastForegroundTicks) >= _backgroundQuietMs
                && await _gate.WaitAsync(0, ct))
            {
                try
                {
                    if (_tick() - Volatile.Read(ref _lastForegroundTicks) >= _backgroundQuietMs)
                    {
                        try { return await _link.SendAsync(command, ct); }
                        catch (DeviceDisconnectedException ex) { Latch(ex); throw; }
                    }
                }
                finally { _gate.Release(); }
            }
            await _bgPollDelay(ct);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SonuClientDisconnectLatchTests"`

Expected: PASS (7 tests).

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests`

Expected: PASS. Watch `SonuClientBackgroundLaneTests` and `SonuClientBatchThreadingTests` — the latch adds a read of `_dead` inside their hot paths and must not change their timing assertions.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Core/SonuClient.cs tests/Sonulab.Core.Tests/SonuClientDisconnectLatchTests.cs
git commit -m "feat(core): SonuClient latches the first disconnect and short-circuits later sends"
```

---

### Task 5: Name the at-risk slot

**Files:**
- Modify: `src/Sonulab.Core/Services/SlotBlobService.cs:156` (`UploadAsync`)
- Test: `tests/Sonulab.Core.Tests/SlotBlobDisconnectAttributionTests.cs`

**Interfaces:**
- Consumes: `DeviceDisconnectedException.ForSlot(string noun, int index, bool writing)` (Task 1); `SonuClient` latch behavior (Task 4).
- Produces: nothing new in the public API — `SlotBlobService.UploadAsync` (and therefore `AmpService.UploadAmpAsync` / `IrService.UploadIrAsync`) now throws a `DeviceDisconnectedException` whose `Message` names the slot.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SlotBlobDisconnectAttributionTests.cs`:

```csharp
using System.IO;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

public class SlotBlobDisconnectAttributionTests
{
    /// <summary>Answers the name-table list read, then dies on the Nth send thereafter.</summary>
    private sealed class DyingAfterListLink(int dieOnSend) : ISonuLink
    {
        public int Sends;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (++Sends >= dieOnSend)
            {
                IsOpen = false;
                throw new DeviceDisconnectedException("USB", new IOException("cable pulled"));
            }
            if (command.StartsWith("read root\\"))
                return Task.FromResult("root\\amp:{\"value\":[\"\",\"\",\"\"]}\r\n");
            // Unreachable in these tests (every case dies on send 1 or 2, before any ACK is read).
            return Task.FromResult("");
        }
    }

    static SlotBlobService Service(ISonuLink link, SlotBlobKind kind) =>
        new(new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0),
            kind, Path.Combine(Path.GetTempPath(), "namager-test-backups"),
            msg => new InvalidOperationException(msg), paceMs: 0, settleMs: 0);

    [Fact] public async Task Upload_interrupted_mid_slot_names_the_amp_slot()
    {
        // Send 1 = the name-table list read; send 2 = the first dwrite. Dying on 2 puts the drop
        // squarely inside the write burst.
        var svc = Service(new DyingAfterListLink(dieOnSend: 2), SlotBlobKind.Amp);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(12, new byte[12288], "Test Amp"));

        Assert.Equal(
            "Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.",
            ex.Message);
        Assert.Equal("Amp", ex.SlotNoun);
        Assert.Equal(12, ex.SlotIndex);
        Assert.True(ex.WasWriting);
    }

    [Fact] public async Task Upload_interrupted_names_the_ir_slot_with_the_IR_noun()
    {
        var svc = Service(new DyingAfterListLink(dieOnSend: 2), SlotBlobKind.Ir);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(3, new byte[4096], "Test IR"));

        Assert.Contains("IR slot 3 may be partially written", ex.Message);
    }

    [Fact] public async Task A_read_is_not_reported_as_a_half_write()
    {
        // Dying on send 1 kills the pre-write name-table READ. Nothing was written, so the message
        // must stay bare rather than accusing a slot that is fine.
        var svc = Service(new DyingAfterListLink(dieOnSend: 1), SlotBlobKind.Amp);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(12, new byte[12288], "Test Amp"));

        Assert.Equal("Device disconnected (USB).", ex.Message);
        Assert.False(ex.WasWriting);
    }

    [Fact] public async Task Read_paths_stay_bare()
    {
        var svc = Service(new DyingAfterListLink(dieOnSend: 1), SlotBlobKind.Amp);
        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => svc.ListAsync());
        Assert.Equal("Device disconnected (USB).", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SlotBlobDisconnectAttributionTests"`

Expected: FAIL — the two attribution tests get the bare `Device disconnected (USB).` message.

- [ ] **Step 3: Implement**

In `src/Sonulab.Core/Services/SlotBlobService.cs`, rename `UploadAsync` to `UploadCoreAsync` (make it `private`) and add a public wrapper immediately above it, keeping the existing XML doc comment on the public method.

The wrapper needs to know whether the write burst had begun when the link died. Track it in a
`StrongBox<bool>` passed *into* the core rather than in an instance field — `SlotBlobService` is
shared and must stay reentrancy-safe.

```csharp
    /// <summary>Guarded upload (the hardware-verified HwCheck sequence):
    /// ... EXISTING doc comment, copied verbatim, unchanged ...</summary>
    public async Task UploadAsync(int slot, byte[] payload, string name,
        IProgress<SlotUploadProgress>? progress = null, CancellationToken ct = default)
    {
        var writing = new StrongBox<bool>(false);
        try { await UploadCoreAsync(slot, payload, name, writing, progress, ct); }
        catch (DeviceDisconnectedException ex) when (!ex.WasWriting)
        {
            // The link died mid-upload. The rollback path is dead too, so the honest thing is to
            // name the slot. `writing.Value` keeps a pre-write failure (the name-table read at the
            // top of the burst) from being reported as a half-write; the `!ex.WasWriting` filter
            // stops a re-attribution if this ever ends up wrapped twice.
            throw ex.ForSlot(_kind.Noun, slot, writing.Value);
        }
    }

    private async Task UploadCoreAsync(int slot, byte[] payload, string name,
        StrongBox<bool> writing, IProgress<SlotUploadProgress>? progress, CancellationToken ct)
    {
        // ... the ORIGINAL UploadAsync body, unchanged except for the one line noted below.
    }
```

Add `using System.Runtime.CompilerServices;` at the top of the file for `StrongBox<T>`.

Inside `UploadCoreAsync`, set the flag as the **first** statement of the existing local function `WriteChunkAckedAsync` (currently line 179), before the `DWriteChunkAsync` call:

```csharp
        async Task WriteChunkAckedAsync(int chunk, byte[] data, int expectNext)
        {
            writing.Value = true;          // from here on, a drop may have half-written the slot
            var raw = await _client.DWriteChunkAsync(_kind.ListPath, slot, chunk, data, ct);
            // ... rest unchanged
        }
```

Leave `DeleteAsync`, `RenameAsync`, and `SwapAsync` alone: each is a single `dwrite`/`dswap`, so there is no partial-write window worth reporting, and the bare message is accurate.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Sonulab.Core.Tests --filter "FullyQualifiedName~SlotBlobDisconnectAttributionTests"`

Expected: PASS (4 tests).

- [ ] **Step 5: Run the Core suite**

Run: `dotnet test tests/Sonulab.Core.Tests`

Expected: PASS — especially `AmpServiceTests`, `IrServiceTests`, `FakeSlotBlobDeviceTests`, `SlotBlobReadValidationTests`, which exercise `UploadAsync` heavily.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Core/Services/SlotBlobService.cs tests/Sonulab.Core.Tests/SlotBlobDisconnectAttributionTests.cs
git commit -m "feat(core): name the at-risk slot when an upload is cut short"
```

---

### Task 6: The app's dead state

**Files:**
- Modify: `src/Namager.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:113`
- Test: `tests/Namager.App.Tests/DeviceLostTests.cs`

**Interfaces:**
- Consumes: `SonuClient.Disconnected` (Task 4), `DeviceDisconnectedException.Message` (Task 1).
- Produces: `ConnectionViewModel.IsDeviceLost` (`bool`, observable), `ConnectionViewModel.DeviceLost` (`event EventHandler?`), and a fourth constructor parameter `Action<Action>? dispatch = null`.

**Note on the dispatch seam:** `AmpListViewModel:39`, `IrListViewModel:31`, and `Tone3000ViewModel:30` already use exactly this pattern (`_dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));`). Follow it verbatim — do not invent a new mechanism. It is required here because `Disconnected` fires on whatever thread the failing send was on, and unit tests have no running dispatcher loop.

**Note on ordering:** set `IsDeviceLost` **before** `IsConnected`. `CanConnect => !IsConnected && !IsDeviceLost`, and `OnIsConnectedChanged` calls `NotifyCanExecuteChanged()`; setting the loss flag first means `CanConnect` is already false when that fires.

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.App.Tests/DeviceLostTests.cs`:

```csharp
using System.IO;
using Namager.App.ViewModels;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceLostTests
{
    private sealed class FixedProvider(string name, ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(link);
    }

    /// <summary>Identifies as a real pedal, then dies on demand.</summary>
    private sealed class KillableLink : ISonuLink
    {
        public bool Kill;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (Kill) { IsOpen = false; throw new DeviceDisconnectedException("USB", new IOException("cable pulled")); }
            return Task.FromResult(command switch
            {
                @"read root\sys\_name"    => "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n",
                @"read root\sys\_id"      => "root\\sys\\_id:{\"value\":\"abc\"}\r\n",
                @"read root\sys\_ver"     => "root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n",
                @"read root\sys\_arch"    => "root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n",
                @"read root\sys\_license" => "root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n",
                @"browse root\presets"    => "root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n",
                @"browse root\amp"        => "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n",
                @"browse root\ir"         => "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n",
                _ => "",
            });
        }
    }

    static (ConnectionViewModel Vm, KillableLink Link) Connected()
    {
        var link = new KillableLink();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", link) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        // Synchronous dispatch: unit tests have no Avalonia dispatcher loop to drain.
        var vm = new ConnectionViewModel(session, null, null, dispatch: a => a());
        vm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (vm, link);
    }

    [Fact] public async Task Device_loss_flips_the_session_to_a_dead_state()
    {
        var (vm, link) = Connected();
        Assert.True(vm.IsConnected);

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.False(vm.IsConnected);
        Assert.True(vm.IsDeviceLost);
        Assert.Equal("Device disconnected — reconnect the pedal and restart NAMager", vm.Status);
    }

    [Fact] public async Task Connect_stays_disabled_after_a_loss()
    {
        // Without the IsDeviceLost latch, IsConnected = false would RE-ENABLE Connect — the
        // reconnect-in-place this design rejects (re-opening a live session wedged the ESP32).
        var (vm, link) = Connected();
        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact] public async Task DeviceLost_event_fires_once()
    {
        var (vm, link) = Connected();
        int fired = 0;
        vm.DeviceLost += (_, _) => fired++;

        link.Kill = true;
        for (int i = 0; i < 3; i++)
            await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Equal(1, fired);
    }

    [Fact] public async Task Status_bar_gets_the_slot_naming_message()
    {
        var status = new FakeStatusService();
        var link = new KillableLink();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", link) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, null, status, dispatch: a => a());
        await vm.ConnectCommand.ExecuteAsync(null);

        link.Kill = true;
        await Assert.ThrowsAsync<DeviceDisconnectedException>(() => vm.Client!.SendRawAsync("read x"));

        Assert.Contains(status.Failures, f => f.Contains("Device disconnected"));
    }
}
```

If `FakeStatusService` does not expose a `Failures` collection under that name, read `tests/Namager.App.Tests/FakeStatusService.cs` and use whatever it records failures into — do not add a new member unless it genuinely has none.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~DeviceLostTests"`

Expected: build FAILS — `'ConnectionViewModel' does not contain a definition for 'IsDeviceLost'`.

- [ ] **Step 3: Implement `ConnectionViewModel`**

In `src/Namager.App/ViewModels/ConnectionViewModel.cs`:

Add the field and the constructor parameter (the seam matches `AmpListViewModel:39` exactly):

```csharp
    private readonly Action<Action> _dispatch;

    public ConnectionViewModel(DeviceSession session,
                               Namager.App.Services.IUsagePingService? usage = null,
                               Namager.App.Services.IStatusService? status = null,
                               Action<Action>? dispatch = null)
    { _session = session; _usage = usage;
      _statusService = status ?? Namager.App.Services.NullStatusService.Instance;
      _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a)); }
```

Add the observable property next to `_isConnected` (line 23):

```csharp
    /// <summary>Latched once the link dies mid-session. Keeps Connect disabled: re-opening the
    /// transport on a session whose link is gone is the reconnect path this design rejects
    /// (see CanConnect's note below). Recovery is an app restart.</summary>
    [ObservableProperty] private bool _isDeviceLost;
```

Update the guard and its notifier (line 35-36):

```csharp
    private bool CanConnect => !IsConnected && !IsDeviceLost;
    partial void OnIsConnectedChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();
    partial void OnIsDeviceLostChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();
```

Add the event next to `Connected` (line 30):

```csharp
    /// <summary>Raised once when the link dies mid-session, after the VM has entered its dead
    /// state. MainWindowViewModel uses it to stop the background usage scan.</summary>
    public event EventHandler? DeviceLost;
```

Subscribe in `ConnectAsync`, immediately after `Client = _session.Client;` (line 56):

```csharp
            Client = _session.Client;
            Client!.Disconnected += OnDeviceDisconnected;
```

And add the handler at the end of the class:

```csharp
    /// <summary>Fires on the thread of the failing send, so marshal before touching bound state.
    /// Idempotent: SonuClient raises this once, but the guard keeps a second source harmless.</summary>
    private void OnDeviceDisconnected(DeviceDisconnectedException ex) => _dispatch(() =>
    {
        if (IsDeviceLost) return;
        IsDeviceLost = true;                  // BEFORE IsConnected: CanConnect must already be false
        IsConnected = false;
        Status = "Device disconnected — reconnect the pedal and restart NAMager";
        _statusService.Failure(ex.Message);   // carries the at-risk slot when an upload was cut short
        _statusService.SetIdleSummary("Device disconnected");
        try { _session.Disconnect(); } catch { /* the transport already closed itself */ }
        DeviceLost?.Invoke(this, EventArgs.Empty);
    });
```

Add `using Sonulab.Core.Transport;` to the file's usings.

- [ ] **Step 4: Stop the background scan on loss**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, immediately after line 113 (`_connection = new ConnectionViewModel(...)`) and before the `_connection.Connected += ...` handler:

```csharp
        // The scan's link is dead; its task must not keep polling a corpse. (SonuClient's latch
        // makes each attempt fail instantly, so this is about ending it, not about cost.)
        _connection.DeviceLost += (_, _) => _usageService?.Stop();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~DeviceLostTests"`

Expected: PASS (4 tests).

- [ ] **Step 6: Run the full app suite**

Run: `dotnet test tests/Namager.App.Tests`

Expected: PASS. `ConnectionViewModelTests` matters most — the constructor gained an optional fourth parameter, so every existing call site must still compile and behave identically. `CrashGuardTests` must also still pass.

- [ ] **Step 7: Commit**

```bash
git add src/Namager.App/ViewModels/ConnectionViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/DeviceLostTests.cs
git commit -m "feat(app): honest dead state on device loss instead of a wedged Connect button"
```

---

### Task 7: HwCheck guard, docs, and full verification

**Files:**
- Modify: `tools/HwCheck/Program.cs`
- Create: `docs/HARDWARE-VALIDATION-disconnect.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `DeviceDisconnectedException` (Task 1). No new public surface.

**Why a handler and not a `try`:** `tools/HwCheck/Program.cs` is ~1060 lines of top-level statements with `return N` exit codes threaded throughout. Wrapping it in a `try` means reindenting the whole file — a large diff with real regression risk on the tool used for hardware validation. Top-level `async Main` compiles to a synchronous `GetAwaiter().GetResult()` on the main thread, so an unhandled exception surfaces through `AppDomain.CurrentDomain.UnhandledException`.

- [ ] **Step 1: Add the guard**

In `tools/HwCheck/Program.cs`, insert immediately after the `using` block (after `using Sonulab.Core.Transport;`, line 24) and **before** the `static int? ArgAfter(...)` local function. It must be the first executable statement in the file so the guard is armed before any device work begins. `Sonulab.Core.Transport` is already in the usings, so `DeviceDisconnectedException` resolves with no new import.

```csharp
// Last-resort guard. Without it an IOException mid-batch killed this harness outright with a raw
// stack trace. Exit 2 = the device went away; 1 = any other unhandled failure. The existing
// 0/3/4 result codes are unaffected.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is DeviceDisconnectedException dx)
    {
        Console.Error.WriteLine($"DEVICE LOST: {dx.Message}");
        Environment.Exit(2);
    }
    Console.Error.WriteLine($"FAILED: {e.ExceptionObject}");
    Environment.Exit(1);
};
```

- [ ] **Step 2: Verify HwCheck builds**

Run: `dotnet build tools/HwCheck`

Expected: build succeeds, no warnings introduced.

- [ ] **Step 3: Verify the guard fires without hardware**

Run: `dotnet run --project tools/HwCheck -- --port COM_NONEXISTENT`

Expected: the harness reports no device found and exits normally (there is nothing to disconnect from), **not** a raw stack trace. This confirms the handler is armed and does not misfire on the ordinary no-device path. Record the actual exit code with `echo $LASTEXITCODE` (PowerShell).

- [ ] **Step 4: Run the entire test suite**

Run: `dotnet test`

Expected: PASS — all pre-existing tests plus the ~30 added across Tasks 1–6. Do not proceed until this is green; report the actual pass/fail counts rather than assuming.

- [ ] **Step 5: Write the hardware-validation checklist**

Create `docs/HARDWARE-VALIDATION-disconnect.md`:

```markdown
# Hardware validation — device disconnect

Requires the pedal on USB and **VoidX-Control CLOSED**. Nothing here writes to an occupied slot
except check 3, which deliberately damages an EMPTY slot — pick one that is empty and expendable.

## 1. HwCheck, read interrupted (exit code)

1. `dotnet run --project tools/HwCheck -- --dump-irs`
2. Pull the USB cable while the dump is streaming.
3. Expect on stderr: `DEVICE LOST: Device disconnected (USB).`
4. Expect exit code `2` (`echo $LASTEXITCODE`).
5. NOT expected: a raw .NET stack trace, or the process hanging.

## 2. App, read interrupted (dead state)

1. Launch the app, connect, open the Presets tab.
2. Press Refresh and pull the cable while it reads.
3. Expect the status bar to read `Device disconnected — reconnect the pedal and restart NAMager`.
4. Expect the Connect button to be DISABLED (not re-enabled).
5. Expect the app to stay responsive — tabs switch, nothing crashes.
6. Replug the cable; expect the app to stay in the dead state (recovery is a restart, by design).
7. Check `tonemanager.log` for one `device link lost (USB)` error entry — exactly one, not one per
   subsequent operation.

## 3. App, upload interrupted (slot attribution)

1. Restart the app, connect, open the Amps tab.
2. Start an upload to a known-EMPTY slot.
3. Pull the cable once the progress bar is past the backup stage and into Writing.
4. Expect a message naming that slot, e.g.
   `Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.`
5. Replug, restart the app, and verify slot 12's actual state on the device.

## 4. WiFi equivalent

1. `dotnet run --project tools/HwCheck -- --wifi --dump-irs`
2. Power-cycle the pedal (or disable the AP) mid-dump.
3. Expect `DEVICE LOST: Device disconnected (WiFi).` and exit code `2`.
```

- [ ] **Step 6: Update `CLAUDE.md`**

In the **Critical conventions & gotchas** section, add after the "Device writes are destructive" bullet:

```markdown
- **Link death is typed:** transports throw `DeviceDisconnectedException` (Sonulab.Core/Transport) and
  close their own port; `SonuClient` latches the first one, raises `Disconnected` once, and fails all
  later sends instantly. The app enters a dead state (Connect disabled, "reconnect and restart");
  HwCheck prints `DEVICE LOST:` and exits 2. Reconnect-in-place is deliberately NOT supported —
  re-opening a live session resets the ESP32 and wedges the pedal.
```

In the **Not done** section, add:

```markdown
Disconnect handling is SHIPPED (typed `DeviceDisconnectedException`, `SonuClient` latch, app dead
state, HwCheck exit 2); on-device checks pending in `docs/HARDWARE-VALIDATION-disconnect.md`.
```

- [ ] **Step 7: Final full build and test**

Run: `dotnet build` then `dotnet test`

Expected: both succeed. Report the actual test count.

- [ ] **Step 8: Commit**

```bash
git add tools/HwCheck/Program.cs docs/HARDWARE-VALIDATION-disconnect.md CLAUDE.md
git commit -m "feat(hwcheck): typed device-lost guard with exit code 2; docs"
```

---

## Verification summary

| Spec section | Task |
|---|---|
| §1 The exception | 1 |
| §2 Classification in transports (serial) | 2 |
| §2 Classification in transports (TCP) | 3 |
| §3 Connect path unchanged | 2 (Step 6 regression check on `SonuConnectorTests`) |
| §4 The latch in `SonuClient` | 4 |
| §5 Slot attribution | 5 |
| §6 App dead state | 6 |
| §7 HwCheck | 7 |
| §8 Tests | 1–6 (each task's own tests) |
| Hardware validation | 7 |

## Known follow-ups (deliberately not in this plan)

- Reconnect without restarting the app. Requires tearing down and rebuilding every tab VM against a
  new `SonuClient`; `ConnectionViewModel:32` records why a partial teardown wedges the pedal.
- Preset-write slot attribution. A drop mid-reorder leaves the order half-applied rather than a slot
  half-written, so the bare message is the honest one until there is a design for reporting order
  damage.
