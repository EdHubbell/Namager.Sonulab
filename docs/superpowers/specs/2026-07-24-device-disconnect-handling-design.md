# Device-disconnect handling — design

Date: 2026-07-24
Status: approved, ready for planning

## Problem

An `IOException` raised mid-batch by the serial transport propagates uncaught. Observed: it kills
`tools/HwCheck` outright with a stack trace.

### What actually happens today

The app's command paths are **already** crash-guarded — `PresetListViewModel.RunAsync`
(`src/Namager.App/ViewModels/PresetListViewModel.cs:46`), `RefreshAsync` (`:78`), the Amp/IR
equivalents, `ConnectionViewModel:75`, and the background scan (`PresetUsageService:145`) all
`catch (Exception)`. The comment at `PresetListViewModel:48` records that the v0.9.1 field crash was
fixed exactly this way. So an `IOException` mid-batch does **not** tear down the process.

It fails in a quieter, worse way:

1. The user sees `Operation failed: The I/O operation has been aborted because of either a thread
   exit or an application request` — not "device disconnected".
2. `ConnectionViewModel.IsConnected` stays `true`, so `CanConnect` (`:35`) stays `false`. The Connect
   button is permanently disabled with no explanation.
3. The link is a zombie. `SerialSonuLink.IsOpen` → `SystemSerialPort.IsOpen` → `SerialPort.IsOpen`,
   which stays `true` after an unplug until someone calls `Close()`. Nothing does. Every subsequent
   operation throws again, including the `await ReloadAsync()` inside the failure handler
   (`PresetListViewModel:57`), which re-attempts a 30-slot read against a dead port.
4. Mid-batch specifically: `SerialSonuLink.SendBatchAsync` throws out of `_port.Write` / `_port.Read`
   with `sent` commands already on the wire and no cleanup. If this happens during a `dwrite` burst
   rather than a `dread`, the slot is left half-written — and the caller's rollback path is also dead.

**The fix is not "add a catch".** The catches exist. Nothing distinguishes *this command failed* from
*the device is gone*.

## Decisions taken

| Decision | Choice |
|---|---|
| Recovery depth | Honest dead state. Detect, close, surface, short-circuit. Reconnect requires an app restart — preserves the "never re-open a live session" rule documented at `ConnectionViewModel:32`. No reconnect-in-place, no auto-reconnect. |
| Where classification lives | In each transport (each knows its own exception vocabulary); latched in `SonuClient` (the one gate all traffic passes through). |
| HwCheck | Shares the app's typed exception and message, so the dev harness stays a proving ground for the app's disconnect handling. |
| Half-write reporting | The exception carries slot context, so the message names the at-risk slot. |

## Design

### 1. The exception

New `src/Sonulab.Core/Transport/DeviceDisconnectedException.cs`, deriving from `IOException`:

```csharp
public sealed class DeviceDisconnectedException : IOException
{
    public string Transport { get; }          // "USB" | "WiFi"
    public string? SlotPath { get; }          // root\amp, root\ir, root\presets
    public int? SlotIndex { get; }
    public bool WasWriting { get; }
    public DeviceDisconnectedException ForSlot(string path, int index, bool writing);
}
```

`ForSlot` returns a **new** instance carrying the same inner exception plus slot context (see §4).

The message is composed from that context:

- bare: `Device disconnected (USB).`
- enriched: `Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.`

**Deriving from `IOException` is deliberate.** `AmpListViewModel:429` and `IrListViewModel:276`
already `catch (IOException ex)` and display `ex.Message`, so they improve with no edit.

Trade-off, accepted: a `catch (IOException)` written for *file* I/O can now also catch a device drop.
Both sites above already span a file read and a device upload, so catching both is their existing
intent. Sites that need to tell the two apart can type-test for `DeviceDisconnectedException` first.

### 2. Classification, in each transport

`SerialSonuLink.SendAsync` and `SendBatchAsync` each get **one** `try` around the existing method
body, starting *after* the `if (!_port.IsOpen) throw new InvalidOperationException("Serial link is
not open.")` guard (`SerialSonuLink.cs:43` and `:95`). That guard stays outside the `try` — it is the
link's own precondition check, not a device failure, and `IsLinkFatal` matches
`InvalidOperationException` (which `SerialPort` raises on a closed handle), so leaving it inside
would silently reclassify a caller bug as a disconnect.

```csharp
catch (Exception ex) when (IsLinkFatal(ex))
{
    try { _port.Close(); } catch { }
    throw new DeviceDisconnectedException("USB", ex);
}
```

One outer `try` rather than per-call wrappers: `_port.BytesToRead` is polled in the hot loop of both
methods, and a `Func<T>` wrapper would allocate a closure per poll. Any of these I/O touchpoints
failing is fatal to the link anyway, so the coarse boundary loses nothing.

```csharp
private static bool IsLinkFatal(Exception ex) =>
    ex is not DeviceDisconnectedException          // already classified — do not re-wrap
    && ex is not OperationCanceledException        // user/caller cancel is NOT a disconnect
    && ex is IOException or ObjectDisposedException
          or UnauthorizedAccessException or InvalidOperationException;
```

`TimeoutException` stays **unclassified** as transient: `SerialPort.Read` is only called after
`BytesToRead > 0` so it should not fire, and if it does it is not proof the device is gone.

`TcpSonuLink.SendAsync` gets the same shape for `SocketException` / `IOException` /
`ObjectDisposedException`, with transport `"WiFi"`. Its `_pending` response-debt state needs no
unwinding — the link is closed and latched, so nothing reads it again.

**Only the send paths classify.** `OpenAsync` is left alone, so "VoidX-Control is holding COM6"
(`UnauthorizedAccessException` at open) stays a probe failure, not a disconnect.

### 3. Connect path — no changes needed

`SonuConnector:49` already catches everything per port and advances to the next one, so a
`DeviceDisconnectedException` thrown while probing a port that isn't the pedal is absorbed exactly as
today. This is asserted by a test rather than changed by code.

### 4. The latch, in `SonuClient`

```csharp
private DeviceDisconnectedException? _dead;
public bool IsDisconnected => Volatile.Read(ref _dead) is not null;
public event Action<DeviceDisconnectedException>? Disconnected;
```

Check-and-latch goes in **three** places, not one:

- `SendAsync` (private, `SonuClient.cs:51`)
- `SendBatchGatedAsync` (`:76`)
- `SendBackgroundAsync` (`:251`) — **the one that matters most.** It calls `_link.SendAsync`
  directly, bypassing the private `SendAsync`, and its `while (true)` quiet-window loop would
  otherwise keep polling a corpse. The latch check goes at the top of that loop so it exits on the
  first iteration.

`Interlocked.CompareExchange` so first-wins and `Disconnected` fires exactly once even under
concurrent sends. Later sends throw a **fresh copy** wrapping the latched instance, not `throw _dead`
— rethrowing a single instance resets its stack trace on every throw.

The payoff shows up in `PresetListViewModel.RunAsync`'s failure handler (`:57`): its
`await ReloadAsync()` currently attempts a 30-slot read against a dead port and throws a second raw
I/O error. With the latch it returns in microseconds with the right message.

The bounded-retry loop in `PresetUsageService.ScanLoopAsync:137` (3 passes, 500 ms apart) likewise
becomes cheap instead of three more full failed passes.

### 5. Slot attribution

No plumbing through `SonuClient`. The write loops in `AmpService`, `IrService`, and `SlotBlobService`
already know the path and index, so they enrich on the way out:

```csharp
catch (DeviceDisconnectedException ex) { throw ex.ForSlot(path, index, writing: true); }
```

Localized and explicit; no new parameters threaded through the call graph.

### 6. App wiring — the dead state

`ConnectionViewModel.ConnectAsync` subscribes to `Disconnected` immediately after
`Client = _session.Client;` (`:56`). The handler marshals to the UI thread via
`Dispatcher.UIThread.Post` (the event fires on whatever thread the failing send was on;
`Sonulab.Core` has no Avalonia dependency and must not gain one), then:

- `IsConnected = false`
- `IsDeviceLost = true`
- `_session.Disconnect()`
- `Status = "Device disconnected — reconnect the pedal and restart NAMager"`
- `_statusService.Failure(...)` with the exception's message, so a half-written slot is named
- raises `ConnectionViewModel.DeviceLost` (the event; note the property is `IsDeviceLost` — a
  property and an event cannot share a name on the same type)

**A second latch is required**, and follows directly from the "no reconnect-in-place" decision:
`CanConnect => !IsConnected` (`:35`), so setting `IsConnected = false` alone would re-enable the
Connect button — the exact reconnect this design rejects. Hence:

```csharp
[ObservableProperty] private bool _isDeviceLost;
private bool CanConnect => !IsConnected && !IsDeviceLost;
partial void OnIsDeviceLostChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();
```

The Connect button stays disabled, with the status text explaining why.

`MainWindowViewModel` subscribes to `ConnectionViewModel.DeviceLost` to call `_usageService?.Stop()`
— it owns that field (`:120`) and the background scan must not linger against a dead link.

### 7. HwCheck

`tools/HwCheck/Program.cs` is ~1060 lines of top-level statements with `return N` exit codes threaded
throughout. Wrapping it in a `try` means reindenting the entire file: a large diff with real
regression risk on the tool used for hardware validation.

Instead, a handler at the top of the file:

```csharp
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is DeviceDisconnectedException dx)
    { Console.Error.WriteLine($"DEVICE LOST: {dx.Message}"); Environment.Exit(2); }
    Console.Error.WriteLine($"FAILED: {e.ExceptionObject}"); Environment.Exit(1);
};
```

Top-level `async Main` compiles to a synchronous `GetAwaiter().GetResult()` on the main thread, so an
exception from an awaited call surfaces through this handler. Exit code 2 = device lost, 1 = any
other unhandled failure. The existing `0/3/4` result codes are untouched.

The port is not closed on this path, but the transport has already closed itself in §2, and the
device is gone regardless.

### 8. Tests

`FakeSerialPort` gains one general fault-injection hook rather than a set of counters:

```csharp
public Action<string>? OnIo { get; set; }   // invoked with "write" | "read" | "bytes" | "discard"
```

A test throws whatever it wants at whatever call count. Cases:

| Area | Assertion |
|---|---|
| `SerialSonuLink` | `IOException` on the 3rd write of a 10-command batch → `DeviceDisconnectedException`; `IsOpen == false` afterwards |
| `SerialSonuLink` | `OperationCanceledException` mid-batch still surfaces as cancellation, not disconnect |
| `SonuClient` | second send throws instantly without touching the link |
| `SonuClient` | `Disconnected` raised exactly once across concurrent sends |
| `SonuClient` | `SendBackgroundAsync` returns immediately on a latched client instead of spinning |
| `AmpService` | upload interrupted mid-slot → message names the slot |
| `ConnectionViewModel` | `Disconnected` → `IsConnected` false, `IsDeviceLost` true, `ConnectCommand.CanExecute` false, expected status string |
| `SonuConnector` | a probe-time `DeviceDisconnectedException` still advances to the next port |

`TcpSonuLink`'s classification is covered through its existing `ITcpConn` seam with a fake that
throws `SocketException`.

## Out of scope

- Reconnect without restarting the app (rejected above; `ConnectionViewModel:32` records why).
- Automatic background reconnect.
- Rolling back a half-written slot. The link is dead, so rollback is impossible by construction —
  this design reports the damage rather than repairing it.
- `TimeoutException` classification (§2).

## Hardware validation

A new `docs/HARDWARE-VALIDATION-disconnect.md` checklist: unplug the pedal mid-`--dump-irs` (expect
`DEVICE LOST:` + exit 2), and unplug it mid-amp-upload in the app (expect the named-slot message, a
disabled Connect button, and a live UI).
