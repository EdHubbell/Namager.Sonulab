# BLE Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect to the StompStation over BLE (Nordic UART Service) as an automatic fallback when USB serial finds no pedal — same protocol, no new UI.

**Architecture:** Core gains an `ILinkProvider` abstraction (ordered list: USB first, BLE second) that `DeviceSession` iterates; the existing `SonuConnector` serial path becomes provider #1 unchanged. A new Windows-only project `Sonulab.Transport.Ble` implements the BLE link: all logic (fragmentation, NUL-terminated response collection, bounded buffering) sits above a thin `IGattPipe` seam so it's unit-testable with fakes; the ~100-line WinRT implementation sits below it, build-verified only.

**Tech Stack:** .NET 10; new TFM `net10.0-windows10.0.19041.0` for Sonulab.Transport.Ble, Sonulab.App, tools/HwCheck (WinRT GATT APIs come free with that TFM); xUnit; Avalonia 12.0.4 unchanged.

**Spec:** `docs/superpowers/specs/2026-07-16-ble-transport-design.md`

## Global Constraints

- NUS UUIDs (exact): service `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`, RX write `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`, TX notify `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`.
- `Sonulab.Core` stays `net10.0` (platform-neutral) — no WinRT types may leak into it.
- Wire semantics identical to serial: command + trailing NUL out; response bytes collected until a NUL terminator (idle-gap and first-byte timeouts as fallbacks, same defaults: Poll 10 ms / IdleGap 120 ms / MaxWait 2500 ms / FirstByte 300 ms); stale input discarded before each send.
- BLE open does NOT reset the ESP32 → no settle delay, no probe-retry loop needed at the link level (identity probe still runs once via `LinkProbe`).
- Existing `SerialSonuLink` and `SonuConnector` behavior unchanged; their existing tests must pass unmodified. `DeviceSession`/`ConnectionViewModel` signatures change (generalization) — their tests are adapted, assertions preserved.
- Tests are xUnit, file-scoped, no namespace, global `Xunit` using; no real Bluetooth/serial/network in any test.
- Full suite green before every commit (`dotnet test --nologo`, currently 443); conventional commits.
- Status bar copy (exact): connected `"{name} {version} — {compat message} ({transport})"` with transport `USB` or `Bluetooth`; both-fail `"Disconnected (no device found on USB or Bluetooth)"`.

---

### Task 1: Core — `ILinkProvider`, `LinkProbe`, `SerialLinkProvider`

**Files:**
- Create: `src/Sonulab.Core/Connection/ILinkProvider.cs`
- Create: `src/Sonulab.Core/Connection/LinkProbe.cs`
- Create: `src/Sonulab.Core/Connection/SerialLinkProvider.cs`
- Modify: `src/Sonulab.Core/Connection/SonuConnector.cs` (probe check delegates to `LinkProbe`; behavior identical)
- Test: `tests/Sonulab.Core.Tests/SerialLinkProviderTests.cs`

**Interfaces:**
- Consumes: existing `SonuConnector`, `ISonuLink`, `ISerialPortStream`, `SerialLinkOptions`, `ResponseParser.NonMeterRecords`, `NodeRecord.TryParse`.
- Produces (namespace `Sonulab.Core.Connection`):
  - `interface ILinkProvider { string Name { get; } Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default); }`
  - `static class LinkProbe { static Task<bool> VerifyAsync(ISonuLink link, CancellationToken ct = default); }` — sends `read root\sys\_name`, true iff a non-meter record parses with that path.
  - `sealed class SerialLinkProvider : ILinkProvider` with ctor `(Func<ISerialPortStream> portFactory, SerialLinkOptions? options = null, Func<IReadOnlyList<string>>? portNames = null, IReadOnlyList<int>? bauds = null)`; `Name == "USB"`; `portNames` defaults to a fresh `System.IO.Ports.SerialPort.GetPortNames()` **evaluated on every TryConnectAsync call** (fixes the stale-port-snapshot bug that hid a replugged pedal on a new COM number); `bauds` defaults to `new[] { 115200 }`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Core.Tests/SerialLinkProviderTests.cs`:

```csharp
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

public class SerialLinkProviderTests
{
    [Fact]
    public void Name_is_USB()
        => Assert.Equal("USB", new SerialLinkProvider(() => new FakeSerialPort()).Name);

    [Fact]
    public async Task Connects_via_existing_connector_path()
    {
        // FakePresetDevice speaks the real protocol through FakeSerialPort/FakeSonuLink plumbing.
        // Reuse the same fake the existing SonuConnector tests use.
        var dev = new FakePresetDevice();
        var provider = new SerialLinkProvider(
            dev.CreatePort,                       // same factory the SonuConnector tests use
            new SerialLinkOptions { FirstByteTimeoutMs = 50, MaxWaitMs = 200 },
            portNames: () => new[] { "COM6" });
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
    }

    [Fact]
    public async Task Port_names_are_enumerated_fresh_on_every_attempt()
    {
        int calls = 0;
        var provider = new SerialLinkProvider(
            () => new FakeSerialPort(),           // never answers -> no link
            new SerialLinkOptions { FirstByteTimeoutMs = 20, MaxWaitMs = 50 },
            portNames: () => { calls++; return new[] { "COM1" }; });
        await provider.TryConnectAsync();
        await provider.TryConnectAsync();
        Assert.Equal(2, calls);                   // stale-snapshot regression guard
    }

    [Fact]
    public async Task Returns_null_when_no_ports_present()
    {
        var provider = new SerialLinkProvider(
            () => new FakeSerialPort(), portNames: () => Array.Empty<string>());
        Assert.Null(await provider.TryConnectAsync());
    }
}
```

> Implementer note: check how `tests/Sonulab.Core.Tests` existing `SonuConnector`/transport tests construct their fake serial device (e.g. `FakePresetDevice` + a port factory, or `FakeSerialPort` scripted directly) and mirror that exact pattern for `Connects_via_existing_connector_path` — the intent is "provider connects through the same fake the connector tests use", not new fake infrastructure. Adjust the factory call to the real fake API, keeping the assertions.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --nologo --filter SerialLinkProviderTests`
Expected: FAIL — `ILinkProvider`/`SerialLinkProvider` do not exist (compile error).

- [ ] **Step 3: Implement**

Create `src/Sonulab.Core/Connection/ILinkProvider.cs`:

```csharp
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>One way of reaching the pedal (USB serial, BLE, ...). Providers are tried in
/// order by DeviceSession; a provider returns an OPEN, identity-verified link or null.</summary>
public interface ILinkProvider
{
    /// <summary>Short transport label shown in the connection status ("USB", "Bluetooth").</summary>
    string Name { get; }

    Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default);
}
```

Create `src/Sonulab.Core/Connection/LinkProbe.cs`:

```csharp
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>Identity probe shared by all transports: the pedal is the thing that answers
/// read root\sys\_name with a matching record (meter stream filtered out).</summary>
public static class LinkProbe
{
    public static async Task<bool> VerifyAsync(ISonuLink link, CancellationToken ct = default)
    {
        var resp = await link.SendAsync(@"read root\sys\_name", ct);
        return ResponseParser.NonMeterRecords(resp)
            .Any(r => NodeRecord.TryParse(r, out var nr) && nr.Path == @"root\sys\_name");
    }
}
```

In `src/Sonulab.Core/Connection/SonuConnector.cs`, replace the two probe-check lines

```csharp
                    var resp = await link.SendAsync(@"read root\sys\_name", ct);
                    bool ok = ResponseParser.NonMeterRecords(resp)
                        .Any(r => NodeRecord.TryParse(r, out var nr) && nr.Path == @"root\sys\_name");
```

with

```csharp
                    bool ok = await LinkProbe.VerifyAsync(link, ct);
```

(keep everything else in the file untouched; remove now-unused usings only if the compiler flags them).

Create `src/Sonulab.Core/Connection/SerialLinkProvider.cs`:

```csharp
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>USB-serial transport provider: wraps the existing SonuConnector scan.
/// Port names are enumerated FRESH on every attempt so a pedal replugged onto a new
/// COM number (COM6 -> COM9) is found without restarting the app.</summary>
public sealed class SerialLinkProvider : ILinkProvider
{
    private readonly Func<ISerialPortStream> _portFactory;
    private readonly SerialLinkOptions? _options;
    private readonly Func<IReadOnlyList<string>> _portNames;
    private readonly IReadOnlyList<int> _bauds;

    public SerialLinkProvider(
        Func<ISerialPortStream> portFactory,
        SerialLinkOptions? options = null,
        Func<IReadOnlyList<string>>? portNames = null,
        IReadOnlyList<int>? bauds = null)
    {
        _portFactory = portFactory;
        _options = options;
        _portNames = portNames ?? (() => System.IO.Ports.SerialPort.GetPortNames());
        _bauds = bauds ?? new[] { 115200 };
    }

    public string Name => "USB";

    public async Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default)
    {
        var ports = _portNames();
        if (ports.Count == 0) return null;
        return await new SonuConnector(_portFactory, _options).ConnectAsync(ports, _bauds, ct);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo`
Expected: all pass, including the untouched `SonuConnector` tests (extraction proof) and the four new ones.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Connection/ILinkProvider.cs src/Sonulab.Core/Connection/LinkProbe.cs src/Sonulab.Core/Connection/SerialLinkProvider.cs src/Sonulab.Core/Connection/SonuConnector.cs tests/Sonulab.Core.Tests/SerialLinkProviderTests.cs
git commit -m "feat: ILinkProvider abstraction - LinkProbe extraction, serial provider with fresh port enumeration"
```

---

### Task 2: Core — `DeviceSession` over providers; adapt callers

**Files:**
- Modify: `src/Sonulab.Core/Connection/DeviceSession.cs` (full rewrite below)
- Modify: `src/Sonulab.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs:75-83` (connector/session/ports block)
- Modify: `tools/HwCheck/Program.cs:29-45` (ports/connector/connect block)
- Test: `tests/Sonulab.Core.Tests/DeviceSessionTests.cs` (adapt), `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs` (adapt)

**Interfaces:**
- Consumes: `ILinkProvider`, `SerialLinkProvider` (Task 1).
- Produces:
  - `sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility, string? Transport = null)` — new `Transport` = winning provider's `Name`.
  - `DeviceSession` ctor `(IReadOnlyList<ILinkProvider> providers, CompatibilityChecker checker)`; `Task<SessionState> ConnectAsync(CancellationToken ct = default)` (no ports/bauds params); `Client`, `Disconnect`, `Dispose` unchanged.
  - `ConnectionViewModel` ctor `(DeviceSession session)` (ports param removed); connected status format `"{name} {version} — {message} ({transport})"`.

- [ ] **Step 1: Adapt the tests first (they define the new shape)**

In `tests/Sonulab.Core.Tests/DeviceSessionTests.cs`: keep every existing assertion; change construction to wrap the existing fake in a provider. Add one new test:

```csharp
    private sealed class FixedProvider(string name, Sonulab.Core.Transport.ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => Task.FromResult(link);
    }

    [Fact]
    public async Task Second_provider_is_tried_when_first_returns_null_and_transport_is_reported()
    {
        var dev = new FakePresetDevice();                    // same fake existing tests use
        await dev.OpenAsync();
        var workingLink = new SonuClientLinkFor(dev);        // however existing tests obtain an ISonuLink from the fake
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("Bluetooth", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var state = await session.ConnectAsync();
        Assert.True(state.Connected);
        Assert.Equal("Bluetooth", state.Transport);
    }
```

> Implementer note: `SonuClientLinkFor` is shorthand — obtain the open `ISonuLink` exactly the way the existing DeviceSessionTests do (they already connect a session against `FakePresetDevice`); reuse that helper/pattern verbatim. Every pre-existing test keeps its assertions; only construction changes to `new DeviceSession(new ILinkProvider[]{ ... }, checker)` + `ConnectAsync()`.

In `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`: same treatment — construction changes to the provider-based `DeviceSession` and `new ConnectionViewModel(session)`; assertions preserved, plus update any asserted status string to the new `"... ({transport})"` format with `(USB)`.

- [ ] **Step 2: Run to verify compile failure**

Run: `dotnet test tests/Sonulab.Core.Tests --nologo --filter DeviceSessionTests`
Expected: FAIL — `DeviceSession` has no provider ctor.

- [ ] **Step 3: Rewrite DeviceSession**

Replace the body of `src/Sonulab.Core/Connection/DeviceSession.cs` with:

```csharp
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

public sealed record SessionState(
    bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility, string? Transport = null);

public sealed class DeviceSession : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IReadOnlyList<ILinkProvider> _providers;
    private readonly CompatibilityChecker _checker;
    private ISonuLink? _link;

    public DeviceSession(IReadOnlyList<ILinkProvider> providers, CompatibilityChecker checker)
    {
        _providers = providers; _checker = checker;
    }

    public SonuClient? Client { get; private set; }

    public async Task<SessionState> ConnectAsync(CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            ISonuLink? link;
            try { link = await provider.TryConnectAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A broken transport (e.g. no Bluetooth radio) must not abort the whole scan.
                Log.Info("transport {0} unavailable: {1}", provider.Name, ex.Message);
                continue;
            }
            if (link is null) continue;

            try
            {
                _link = link;
                Client = new SonuClient(link);
                var swCompat = System.Diagnostics.Stopwatch.StartNew();
                var compat = await _checker.CheckAsync(Client, ct);
                Log.Info("PERF connect compat={0}ms transport={1}", swCompat.ElapsedMilliseconds, provider.Name);
                return new SessionState(true, compat.Device, compat, provider.Name);
            }
            catch
            {
                link.Close(); _link = null; Client = null;
                throw;
            }
        }
        Client = null;
        return new SessionState(false, null, null);
    }

    public void Disconnect() { _link?.Close(); _link = null; Client = null; }
    public void Dispose() => Disconnect();
}
```

- [ ] **Step 4: Adapt the three callers**

`src/Sonulab.App/ViewModels/ConnectionViewModel.cs` — remove `_ports`/`Bauds` fields, ctor becomes `(DeviceSession session)`, and `ConnectAsync` becomes:

```csharp
    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            var state = await _session.ConnectAsync();
            IsConnected = state.Connected;
            if (!state.Connected) { Status = "Disconnected (no device found)"; return; }

            WritesAllowed = state.Compatibility!.WritesAllowed;
            Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility!.Message} ({state.Transport})";
            Client = _session.Client;
            Repository = new DeviceRepository(_session.Client!);
            Reorder = new ReorderService(Repository);
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
        }
    }
```

(The "no device found on USB or Bluetooth" copy lands in Task 6 when BLE actually joins the list.)

`src/Sonulab.App/ViewModels/MainWindowViewModel.cs` — replace the connector/session/ports block (currently building `SonuConnector`, `DeviceSession`, `GetPortNames` snapshot, and `new ConnectionViewModel(session, portList)`) with:

```csharp
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
        var providers = new List<ILinkProvider>
        {
            // Fresh port enumeration per connect: a pedal replugged onto a new COM number
            // is found without restarting the app.
            new SerialLinkProvider(() => new SystemSerialPort(), options),
        };
        var session = new DeviceSession(providers, new CompatibilityChecker(FirmwareCatalog.Default));

        _connection = new ConnectionViewModel(session);
```

(keep the surrounding comment about adaptive settle; delete the `GetPortNames`/`portList` lines — the provider owns enumeration now; add `using Sonulab.Core.Connection;` if not present).

`tools/HwCheck/Program.cs` — replace lines 29-45 (ports/options/connector/session/connect) with:

```csharp
// Ports: `--port COMx` pins a port; otherwise the provider auto-discovers by probing every
// present COM port fresh at connect time (whichever answers `read root\sys\_name` is the pedal).
int portFlag = Array.IndexOf(args, "--port");
Func<IReadOnlyList<string>> portNames = portFlag >= 0 && portFlag + 1 < args.Length
    ? () => new[] { args[portFlag + 1] }
    : () => System.IO.Ports.SerialPort.GetPortNames();
bool writeTest = Array.IndexOf(args, "--write-test") >= 0;
bool reorderTest = Array.IndexOf(args, "--reorder-test") >= 0;

var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
var providers = new List<ILinkProvider>
{
    new SerialLinkProvider(() => new SystemSerialPort(), options, portNames),
};
var checker = new CompatibilityChecker(FirmwareCatalog.Default);

Console.WriteLine("Connecting (USB serial, auto-discover) ...");
using var session = new DeviceSession(providers, checker);
var state = await session.ConnectAsync();
if (!state.Connected)
{
    Console.WriteLine("RESULT: NOT CONNECTED — no StompStation answered on any COM port.");
    Console.WriteLine("  Check: (1) VoidX-Control is CLOSED — it holds the COM port exclusively;");
    Console.WriteLine("         (2) the pedal is connected via USB (the CH340 'USB-SERIAL' port).");
    return 1;
}
```

(The `ports.Length == 0` early-exit disappears — an empty port list simply yields NOT CONNECTED. Keep every downstream line that uses `state`/`session` untouched.)

- [ ] **Step 5: Full suite**

Run: `dotnet test --nologo && dotnet build --nologo`
Expected: all green (adapted DeviceSession/ConnectionViewModel tests included), HwCheck compiles.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Core/Connection/DeviceSession.cs src/Sonulab.App/ViewModels/ConnectionViewModel.cs src/Sonulab.App/ViewModels/MainWindowViewModel.cs tools/HwCheck/Program.cs tests/
git commit -m "refactor: DeviceSession iterates ordered link providers; transport surfaced in status"
```

---

### Task 3: `Sonulab.Transport.Ble` project — `BleSonuLink` over `IGattPipe` (TDD)

**Files:**
- Create: `src/Sonulab.Transport.Ble/Sonulab.Transport.Ble.csproj`
- Create: `src/Sonulab.Transport.Ble/IGattPipe.cs`
- Create: `src/Sonulab.Transport.Ble/BleLinkOptions.cs`
- Create: `src/Sonulab.Transport.Ble/BleSonuLink.cs`
- Create: `tests/Sonulab.Transport.Ble.Tests/Sonulab.Transport.Ble.Tests.csproj`
- Create: `tests/Sonulab.Transport.Ble.Tests/FakeGattPipe.cs`
- Create: `tests/Sonulab.Transport.Ble.Tests/BleSonuLinkTests.cs`
- Modify: `Sonulab.slnx` (add both projects; follow the existing entry format in that file)

**Interfaces:**
- Consumes: `ISonuLink` (Core).
- Produces (namespace `Sonulab.Transport.Ble`):
  - `interface IGattPipe { bool IsConnected { get; } int MaxWriteSize { get; } event Action<byte[]>? NotificationReceived; Task ConnectAsync(CancellationToken ct = default); Task WriteAsync(byte[] fragment, CancellationToken ct = default); void Disconnect(); }`
  - `sealed class BleLinkOptions { int PollMs = 10; int IdleGapMs = 120; int MaxWaitMs = 2500; int FirstByteTimeoutMs = 300; int MaxBufferBytes = 262_144; }` (all `{ get; init; }`)
  - `sealed class BleSonuLink : ISonuLink, IDisposable` ctor `(IGattPipe pipe, BleLinkOptions? options = null)`
  - Test double `FakeGattPipe` (test project) — Task 4 reuses it.

- [ ] **Step 1: Create the two projects**

`src/Sonulab.Transport.Ble/Sonulab.Transport.Ble.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Windows TFM: WinRT GATT APIs (Task 5). Core stays net10.0/platform-neutral. -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NLog" Version="6.1.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Sonulab.Core\Sonulab.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/Sonulab.Transport.Ble.Tests/Sonulab.Transport.Ble.Tests.csproj` — copy `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj` and change: `TargetFramework` to `net10.0-windows10.0.19041.0`, project reference to `..\..\src\Sonulab.Transport.Ble\Sonulab.Transport.Ble.csproj` (plus Core), no `<Compile Include>` of shared fakes.

Add both to `Sonulab.slnx` following the file's existing project-entry syntax.

- [ ] **Step 2: Write the failing tests**

Create `tests/Sonulab.Transport.Ble.Tests/FakeGattPipe.cs`:

```csharp
using Sonulab.Transport.Ble;

namespace Sonulab.Transport.Ble.Tests;

/// <summary>Scripted GATT pipe. Writes are recorded; when a NUL-terminated command has
/// accumulated, RespondWith (if set) supplies notification chunks to emit.</summary>
public sealed class FakeGattPipe : IGattPipe
{
    private readonly MemoryStream _pending = new();
    public List<byte[]> Writes { get; } = new();
    public bool IsConnected { get; private set; }
    public int MaxWriteSize { get; set; } = 20;
    public bool FailConnect { get; set; }
    public Func<string, IEnumerable<byte[]>>? RespondWith { get; set; }   // command (no NUL) -> notification chunks
    public event Action<byte[]>? NotificationReceived;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (FailConnect) throw new InvalidOperationException("connect failed");
        IsConnected = true; return Task.CompletedTask;
    }

    public Task WriteAsync(byte[] fragment, CancellationToken ct = default)
    {
        Writes.Add(fragment);
        _pending.Write(fragment, 0, fragment.Length);
        var all = _pending.ToArray();
        int nul = Array.IndexOf(all, (byte)0);
        if (nul >= 0)
        {
            _pending.SetLength(0);
            var cmd = System.Text.Encoding.ASCII.GetString(all, 0, nul);
            if (RespondWith is not null)
                foreach (var chunk in RespondWith(cmd)) NotificationReceived?.Invoke(chunk);
        }
        return Task.CompletedTask;
    }

    public void Disconnect() => IsConnected = false;

    public void Notify(byte[] data) => NotificationReceived?.Invoke(data);
    public void Notify(string ascii) => Notify(System.Text.Encoding.ASCII.GetBytes(ascii));
}
```

Create `tests/Sonulab.Transport.Ble.Tests/BleSonuLinkTests.cs`:

```csharp
using System.Text;
using Sonulab.Transport.Ble;
using Sonulab.Transport.Ble.Tests;

public class BleSonuLinkTests
{
    private static readonly BleLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static (BleSonuLink link, FakeGattPipe pipe) Open()
    {
        var pipe = new FakeGattPipe();
        var link = new BleSonuLink(pipe, Fast);
        link.OpenAsync().GetAwaiter().GetResult();
        return (link, pipe);
    }

    [Fact]
    public async Task Open_connects_and_IsOpen_tracks_pipe()
    {
        var pipe = new FakeGattPipe();
        var link = new BleSonuLink(pipe, Fast);
        Assert.False(link.IsOpen);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        link.Close();
        Assert.False(link.IsOpen);
    }

    [Fact]
    public async Task Command_is_fragmented_to_MaxWriteSize_with_trailing_nul()
    {
        var (link, pipe) = Open();
        pipe.MaxWriteSize = 8;
        var cmd = @"read root\sys\_name";                      // 19 chars + NUL = 20 bytes -> 8+8+4
        pipe.RespondWith = _ => new[] { Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP\"}\r\n\0") };

        await link.SendAsync(cmd);

        Assert.Equal(new[] { 8, 8, 4 }, pipe.Writes.Select(w => w.Length));
        var reassembled = pipe.Writes.SelectMany(w => w).ToArray();
        Assert.Equal((byte)0, reassembled[^1]);
        Assert.Equal(cmd, Encoding.ASCII.GetString(reassembled, 0, reassembled.Length - 1));
    }

    [Fact]
    public async Task Response_split_across_notifications_is_reassembled_until_nul()
    {
        var (link, pipe) = Open();
        pipe.RespondWith = _ => new[]
        {
            Encoding.ASCII.GetBytes("root\\sys\\_name:{\"val"),
            Encoding.ASCII.GetBytes("ue\":\"AMP Station\"}\r\n\0"),
        };
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.Contains("AMP Station", resp);
    }

    [Fact]
    public async Task Stale_meter_spam_before_send_is_discarded()
    {
        var (link, pipe) = Open();
        pipe.Notify("root\\sys\\_meters\\_in0:{\"value\":0.1}\r\n");   // arrives before the command
        pipe.RespondWith = _ => new[] { Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP\"}\r\n\0") };
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.DoesNotContain("_meters", resp);                        // buffer was cleared at send
    }

    [Fact]
    public async Task No_response_command_returns_empty_after_first_byte_timeout()
    {
        var (link, pipe) = Open();                                     // RespondWith null -> silence
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await link.SendAsync(@"write root\app\x:{""value"":1}");
        Assert.Equal("", resp);
        Assert.True(sw.ElapsedMilliseconds < Fast.MaxWaitMs, "should stop at FirstByteTimeout, not MaxWait");
    }

    [Fact]
    public async Task Buffer_overflow_faults_and_closes_the_link()
    {
        var (link, pipe) = Open();
        var opts = new BleLinkOptions { PollMs = 1, FirstByteTimeoutMs = 40, MaxWaitMs = 200, MaxBufferBytes = 64 };
        var smallLink = new BleSonuLink(pipe, opts);
        await smallLink.OpenAsync();
        pipe.RespondWith = _ => new[] { new byte[128] };               // no NUL, over the 64-byte cap
        await Assert.ThrowsAsync<InvalidOperationException>(() => smallLink.SendAsync("read root"));
        Assert.False(smallLink.IsOpen);
    }

    [Fact]
    public async Task Send_on_closed_link_throws()
    {
        var pipe = new FakeGattPipe();
        var link = new BleSonuLink(pipe, Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read root"));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Sonulab.Transport.Ble.Tests --nologo`
Expected: FAIL — `BleSonuLink`/`IGattPipe`/`BleLinkOptions` do not exist (compile error).

- [ ] **Step 4: Implement**

Create `src/Sonulab.Transport.Ble/IGattPipe.cs`:

```csharp
namespace Sonulab.Transport.Ble;

/// <summary>Seam over the OS BLE stack (Nordic UART Service). Everything above this
/// interface is unit-testable; the WinRT implementation below it is build-verified only.</summary>
public interface IGattPipe
{
    bool IsConnected { get; }
    /// <summary>Usable payload bytes per ATT write (negotiated MTU minus ATT header); 20 if unnegotiated.</summary>
    int MaxWriteSize { get; }
    event Action<byte[]>? NotificationReceived;
    /// <summary>Connect and subscribe to NUS TX notifications.</summary>
    Task ConnectAsync(CancellationToken ct = default);
    /// <summary>One Write-Without-Response to NUS RX.</summary>
    Task WriteAsync(byte[] fragment, CancellationToken ct = default);
    void Disconnect();
}
```

Create `src/Sonulab.Transport.Ble/BleLinkOptions.cs`:

```csharp
namespace Sonulab.Transport.Ble;

/// <summary>Response-collection policy — same semantics and defaults as SerialLinkOptions'
/// read loop (the wire protocol is identical); plus a bounded receive buffer.</summary>
public sealed class BleLinkOptions
{
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 2500;
    public int FirstByteTimeoutMs { get; init; } = 300;
    public int MaxBufferBytes { get; init; } = 262_144;
}
```

Create `src/Sonulab.Transport.Ble/BleSonuLink.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Ble;

/// <summary>ISonuLink over BLE (Nordic UART Service). Same bytes as serial: command + NUL
/// out (fragmented to the ATT write size), response collected until the device's NUL
/// terminator. BLE has no DTR/RTS, so opening does NOT reset the ESP32 — no settle delay.</summary>
public sealed class BleSonuLink : ISonuLink, IDisposable
{
    private readonly IGattPipe _pipe;
    private readonly BleLinkOptions _options;
    private readonly object _lock = new();
    private readonly MemoryStream _rx = new();
    private bool _overflowed;

    public BleSonuLink(IGattPipe pipe, BleLinkOptions? options = null)
    {
        _pipe = pipe;
        _options = options ?? new BleLinkOptions();
        _pipe.NotificationReceived += OnNotification;
    }

    public bool IsOpen => _pipe.IsConnected;

    public Task OpenAsync(CancellationToken ct = default) => _pipe.ConnectAsync(ct);

    public void Close() => _pipe.Disconnect();

    public void Dispose()
    {
        _pipe.NotificationReceived -= OnNotification;
        Close();
    }

    private void OnNotification(byte[] data)
    {
        lock (_lock)
        {
            if (_rx.Length + data.Length > _options.MaxBufferBytes) { _overflowed = true; return; }
            _rx.Write(data, 0, data.Length);
        }
    }

    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!_pipe.IsConnected) throw new InvalidOperationException("BLE link is not open.");
        lock (_lock) { _rx.SetLength(0); _overflowed = false; }   // discard stale meter spam (serial: DiscardInBuffer)

        var payload = new byte[Encoding.ASCII.GetByteCount(command) + 1];
        Encoding.ASCII.GetBytes(command, 0, command.Length, payload, 0);   // trailing 0 already zeroed
        int max = Math.Max(1, _pipe.MaxWriteSize);
        for (int off = 0; off < payload.Length; off += max)
        {
            ct.ThrowIfCancellationRequested();
            int len = Math.Min(max, payload.Length - off);
            var frag = new byte[len];
            Array.Copy(payload, off, frag, 0, len);
            await _pipe.WriteAsync(frag, ct);
        }

        // Collect until the device's NUL terminator; idle-gap / first-byte timeouts as fallback —
        // identical policy to SerialSonuLink.SendAsync.
        var sw = Stopwatch.StartNew();
        long lastData = 0;
        int lastLen = 0;
        bool sawData = false;
        byte[] latest = Array.Empty<byte>();

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            bool overflowed;
            lock (_lock) { overflowed = _overflowed; latest = _rx.ToArray(); }
            if (overflowed)
            {
                Close();
                throw new InvalidOperationException("BLE receive buffer overflowed; link closed.");
            }
            if (latest.Length > lastLen)
            {
                sawData = true;
                lastData = sw.ElapsedMilliseconds;
                lastLen = latest.Length;
                if (Array.IndexOf(latest, (byte)0) >= 0) break;
            }
            else
            {
                if (sawData && sw.ElapsedMilliseconds - lastData >= _options.IdleGapMs) break;
                if (!sawData && sw.ElapsedMilliseconds >= _options.FirstByteTimeoutMs) break;
            }
            await Task.Delay(_options.PollMs, ct);
        }
        return Encoding.ASCII.GetString(latest);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --nologo`
Expected: all green (new BLE tests + entire existing suite; the slnx now builds the new projects).

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Transport.Ble/ tests/Sonulab.Transport.Ble.Tests/ Sonulab.slnx
git commit -m "feat: BleSonuLink - NUS transport core, fragmentation + nul-terminated collection over IGattPipe"
```

---

### Task 4: `BleLinkProvider` (testable; no WinRT yet)

**Files:**
- Create: `src/Sonulab.Transport.Ble/BleLinkProvider.cs`
- Test: `tests/Sonulab.Transport.Ble.Tests/BleLinkProviderTests.cs`

**Interfaces:**
- Consumes: `ILinkProvider`, `LinkProbe` (Task 1); `BleSonuLink`, `IGattPipe`, `BleLinkOptions`, `FakeGattPipe` (Task 3).
- Produces: `sealed class BleLinkProvider : ILinkProvider` with ctor `(Func<CancellationToken, Task<IGattPipe?>> pipeLocator, BleLinkOptions? options = null)`; `Name == "Bluetooth"`. The locator abstracts "scan + connect a pipe" so Task 5's WinRT pieces plug in without touching this class.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Transport.Ble.Tests/BleLinkProviderTests.cs`:

```csharp
using System.Text;
using Sonulab.Transport.Ble;
using Sonulab.Transport.Ble.Tests;

public class BleLinkProviderTests
{
    private static readonly BleLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static FakeGattPipe PedalPipe()
    {
        var pipe = new FakeGattPipe();
        pipe.RespondWith = cmd => cmd == @"read root\sys\_name"
            ? new[] { Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n\0") }
            : Array.Empty<byte[]>();
        return pipe;
    }

    [Fact]
    public void Name_is_Bluetooth()
        => Assert.Equal("Bluetooth", new BleLinkProvider(_ => Task.FromResult<IGattPipe?>(null)).Name);

    [Fact]
    public async Task Returns_verified_open_link_when_pedal_found()
    {
        var provider = new BleLinkProvider(_ => Task.FromResult<IGattPipe?>(PedalPipe()), Fast);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
    }

    [Fact]
    public async Task Returns_null_when_scan_finds_nothing()
    {
        var provider = new BleLinkProvider(_ => Task.FromResult<IGattPipe?>(null), Fast);
        Assert.Null(await provider.TryConnectAsync());
    }

    [Fact]
    public async Task Returns_null_and_closes_when_device_fails_identity_probe()
    {
        var pipe = new FakeGattPipe();                     // silent device: never answers
        var provider = new BleLinkProvider(_ => Task.FromResult<IGattPipe?>(pipe), Fast);
        Assert.Null(await provider.TryConnectAsync());
        Assert.False(pipe.IsConnected);                    // link closed after failed probe
    }

    [Fact]
    public async Task Locator_exception_bubbles_as_null_result_is_not_required_DeviceSession_handles_it()
    {
        // DeviceSession catches provider exceptions; the provider itself may let them fly.
        var provider = new BleLinkProvider(_ => throw new InvalidOperationException("no radio"), Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TryConnectAsync());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sonulab.Transport.Ble.Tests --nologo --filter BleLinkProviderTests`
Expected: FAIL — `BleLinkProvider` does not exist.

- [ ] **Step 3: Implement**

Create `src/Sonulab.Transport.Ble/BleLinkProvider.cs`:

```csharp
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Ble;

/// <summary>Bluetooth transport provider: locate a NUS pipe (scan + GATT connect, injected
/// as a locator so tests use fakes), open a BleSonuLink over it, verify identity via the
/// shared LinkProbe. Returns null when nothing is found or the device isn't the pedal.</summary>
public sealed class BleLinkProvider : ILinkProvider
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly Func<CancellationToken, Task<IGattPipe?>> _pipeLocator;
    private readonly BleLinkOptions? _options;

    public BleLinkProvider(Func<CancellationToken, Task<IGattPipe?>> pipeLocator, BleLinkOptions? options = null)
    {
        _pipeLocator = pipeLocator; _options = options;
    }

    public string Name => "Bluetooth";

    public async Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default)
    {
        var pipe = await _pipeLocator(ct);
        if (pipe is null) return null;

        var link = new BleSonuLink(pipe, _options);
        try
        {
            if (!pipe.IsConnected) await link.OpenAsync(ct);
            if (await LinkProbe.VerifyAsync(link, ct))
            {
                Log.Info("PERF connect transport=Bluetooth");
                return link;
            }
        }
        catch (OperationCanceledException) { link.Close(); throw; }
        catch { /* fall through to close */ }
        link.Close();
        return null;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Transport.Ble/BleLinkProvider.cs tests/Sonulab.Transport.Ble.Tests/BleLinkProviderTests.cs
git commit -m "feat: BleLinkProvider - locate pipe, open link, shared identity probe"
```

---

### Task 5: WinRT layer — `BleDeviceScanner` + `WinRtGattPipe` (build-verified)

**Files:**
- Create: `src/Sonulab.Transport.Ble/BleDeviceScanner.cs`
- Create: `src/Sonulab.Transport.Ble/WinRtGattPipe.cs`

**Interfaces:**
- Consumes: `IGattPipe` (Task 3).
- Produces:
  - `static class BleUuids { static readonly Guid Service/Rx/Tx }`
  - `sealed class BleDeviceScanner` with `Task<ulong?> FindPedalAsync(TimeSpan timeout, CancellationToken ct = default)` — returns the Bluetooth address of the first NUS advertiser, or null (timeout, cancelled, radio off/absent — never throws).
  - `sealed class WinRtGattPipe : IGattPipe` ctor `(ulong bluetoothAddress)`.
  - `static class BleTransport { static Func<CancellationToken, Task<IGattPipe?>> PipeLocator(TimeSpan scanTimeout) }` — the composition Task 6/7 hand to `BleLinkProvider`.

No unit tests (WinRT is the untestable seam by design — spec §Testing); this task is build-verified and exercised by the hardware checklist.

- [ ] **Step 1: Implement the scanner + UUIDs**

Create `src/Sonulab.Transport.Ble/BleDeviceScanner.cs`:

```csharp
using Windows.Devices.Bluetooth.Advertisement;

namespace Sonulab.Transport.Ble;

public static class BleUuids
{
    public static readonly Guid Service = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid Rx = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"); // host->device (Write)
    public static readonly Guid Tx = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"); // device->host (Notify)
}

/// <summary>Finds the pedal by its Nordic UART Service advertisement. First match wins
/// (multi-pedal disambiguation is out of scope per spec). Never throws: radio off/absent,
/// timeout, and cancellation all return null.</summary>
public sealed class BleDeviceScanner
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public async Task<ulong?> FindPedalAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BluetoothLEAdvertisementWatcher watcher;
        try
        {
            watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleUuids.Service);
            watcher.Received += (_, e) => tcs.TrySetResult(e.BluetoothAddress);
            watcher.Stopped += (_, _) => tcs.TrySetResult(null);   // radio turned off mid-scan
            watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Info("BLE scan unavailable: {0}", ex.Message);      // no Bluetooth stack / radio absent
            return null;
        }

        await using var reg = ct.Register(() => tcs.TrySetResult(null));
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None));
        try { watcher.Stop(); } catch { /* already stopped */ }
        var address = winner == tcs.Task ? tcs.Task.Result : null;
        Log.Info("BLE scan result: {0}", address is null ? "none" : $"found 0x{address:X}");
        return address;
    }
}
```

- [ ] **Step 2: Implement the pipe + locator**

Create `src/Sonulab.Transport.Ble/WinRtGattPipe.cs`:

```csharp
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Sonulab.Transport.Ble;

/// <summary>WinRT implementation of the GATT seam. Deliberately thin — all logic lives in
/// BleSonuLink above the IGattPipe interface. Not unit-tested (hardware checklist covers it).</summary>
public sealed class WinRtGattPipe : IGattPipe
{
    private readonly ulong _address;
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _tx;

    public WinRtGattPipe(ulong bluetoothAddress) => _address = bluetoothAddress;

    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _rx is not null;

    public int MaxWriteSize { get; private set; } = 20;   // ATT default payload until session reports MTU

    public event Action<byte[]>? NotificationReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask(ct)
                  ?? throw new InvalidOperationException("BLE device unreachable.");

        var services = await _device.GetGattServicesForUuidAsync(BleUuids.Service, BluetoothCacheMode.Uncached).AsTask(ct);
        var service = services.Services.FirstOrDefault()
                      ?? throw new InvalidOperationException("NUS service not found on device.");

        _rx = (await service.GetCharacteristicsForUuidAsync(BleUuids.Rx).AsTask(ct)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("NUS RX characteristic not found.");
        _tx = (await service.GetCharacteristicsForUuidAsync(BleUuids.Tx).AsTask(ct)).Characteristics.FirstOrDefault()
              ?? throw new InvalidOperationException("NUS TX characteristic not found.");

        _tx.ValueChanged += (_, e) => NotificationReceived?.Invoke(e.CharacteristicValue.ToArray());
        var status = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"NUS notify subscription failed: {status}.");

        var session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct);
        if (session?.MaxPduSize > 3) MaxWriteSize = session.MaxPduSize - 3;   // ATT header
    }

    public async Task WriteAsync(byte[] fragment, CancellationToken ct = default)
    {
        var rx = _rx ?? throw new InvalidOperationException("BLE pipe is not connected.");
        var status = await rx.WriteValueAsync(fragment.AsBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(ct);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"BLE write failed: {status}.");
    }

    public void Disconnect()
    {
        _rx = null; _tx = null;
        _device?.Dispose();
        _device = null;
    }
}

/// <summary>Composition used by the app and HwCheck: scan, then open a pipe to the winner.</summary>
public static class BleTransport
{
    public static Func<CancellationToken, Task<IGattPipe?>> PipeLocator(TimeSpan scanTimeout) =>
        async ct =>
        {
            var address = await new BleDeviceScanner().FindPedalAsync(scanTimeout, ct);
            return address is null ? null : new WinRtGattPipe(address.Value);
        };
}
```

- [ ] **Step 3: Build-verify + full suite**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: clean build (WinRT types resolve via the `net10.0-windows10.0.19041.0` TFM), all tests green. If `AsTask(ct)` overloads are missing on any IAsyncOperation, add `using System.WindowsRuntimeSystemExtensions;`-equivalent or drop the ct argument for that call and note it in the commit message — do not add packages.

- [ ] **Step 4: Commit**

```bash
git add src/Sonulab.Transport.Ble/BleDeviceScanner.cs src/Sonulab.Transport.Ble/WinRtGattPipe.cs
git commit -m "feat: WinRT BLE layer - NUS advertisement scanner, GATT pipe, composition locator"
```

---

### Task 6: App wiring — TFM bump, BLE fallback provider, status copy

**Files:**
- Modify: `src/Sonulab.App/Sonulab.App.csproj` (TFM)
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs` (add BLE provider)
- Modify: `src/Sonulab.App/ViewModels/ConnectionViewModel.cs` (failure copy)
- Modify: `tests/Sonulab.App.Tests/Sonulab.App.Tests.csproj` (TFM must match app)
- Test: `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs` (failure-copy assertion)

**Interfaces:**
- Consumes: `BleLinkProvider`, `BleTransport.PipeLocator` (Tasks 4-5), `DeviceSession` providers (Task 2).
- Produces: user-facing behavior — Connect tries USB then BLE; status `"... (USB)"` / `"... (Bluetooth)"`; both-fail copy exactly `"Disconnected (no device found on USB or Bluetooth)"`.

- [ ] **Step 1: TFM bumps**

In `src/Sonulab.App/Sonulab.App.csproj` and `tests/Sonulab.App.Tests/Sonulab.App.Tests.csproj`, change

```xml
    <TargetFramework>net10.0</TargetFramework>
```

to

```xml
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

In the app csproj add the project reference:

```xml
    <ProjectReference Include="..\Sonulab.Transport.Ble\Sonulab.Transport.Ble.csproj" />
```

- [ ] **Step 2: Update the failure-copy test**

In `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`, the not-connected test's expected status becomes:

```csharp
        Assert.Equal("Disconnected (no device found on USB or Bluetooth)", vm.Status);
```

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter ConnectionViewModelTests`
Expected: FAIL on that assertion (old copy).

- [ ] **Step 3: Implement**

`src/Sonulab.App/ViewModels/ConnectionViewModel.cs` — the not-connected line becomes:

```csharp
            if (!state.Connected) { Status = "Disconnected (no device found on USB or Bluetooth)"; return; }
```

`src/Sonulab.App/ViewModels/MainWindowViewModel.cs` — extend the provider list from Task 2:

```csharp
        var providers = new List<ILinkProvider>
        {
            new SerialLinkProvider(() => new SystemSerialPort(), options),
            // BLE fallback: 4s NUS scan; skipped silently (null) when Bluetooth is off/absent.
            new Sonulab.Transport.Ble.BleLinkProvider(
                Sonulab.Transport.Ble.BleTransport.PipeLocator(TimeSpan.FromSeconds(4))),
        };
```

- [ ] **Step 4: Full suite + build**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: all green under the new TFM. Then sanity-check packaging still works:

```powershell
dotnet publish src/Sonulab.App -c Release -r win-x64 --self-contained true -p:Version=0.0.1
dotnet build src/Sonulab.Installer -c Release -p:ProductVersion=0.0.1
```

Expected: publish + MSI build succeed (the TFM changes the publish path segment to `net10.0-windows10.0.19041.0` — if the installer's default `PublishDir` in `src/Sonulab.Installer/Sonulab.Installer.wixproj` embeds `net10.0`, update that default to the new segment and include it in this commit).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/ tests/Sonulab.App.Tests/ src/Sonulab.Installer/ 
git commit -m "feat: app connects via USB then BLE fallback; windows TFM"
```

---

### Task 7: HwCheck `--ble`

**Files:**
- Modify: `tools/HwCheck/HwCheck.csproj` (TFM + BLE project reference)
- Modify: `tools/HwCheck/Program.cs` (flag + provider selection + header comment)

**Interfaces:**
- Consumes: `BleLinkProvider`, `BleTransport` (Tasks 4-5), provider-based session (Task 2).
- Produces: `dotnet run --project tools/HwCheck -- --ble [...]` runs any existing mode over BLE only (no serial attempted — deterministic bench behavior).

- [ ] **Step 1: TFM + reference**

In `tools/HwCheck/HwCheck.csproj`: `TargetFramework` → `net10.0-windows10.0.19041.0`; add `<ProjectReference Include="..\..\src\Sonulab.Transport.Ble\Sonulab.Transport.Ble.csproj" />`.

- [ ] **Step 2: Flag + provider selection**

In `tools/HwCheck/Program.cs`, add to the header comment block:

```csharp
//   dotnet run --project tools/HwCheck -- --ble [...]    # any mode over Bluetooth (NUS) instead of USB serial
```

and replace the provider-list construction from Task 2 with:

```csharp
bool useBle = Array.IndexOf(args, "--ble") >= 0;
var providers = useBle
    ? new List<ILinkProvider>
    {
        new Sonulab.Transport.Ble.BleLinkProvider(
            Sonulab.Transport.Ble.BleTransport.PipeLocator(TimeSpan.FromSeconds(6))),
    }
    : new List<ILinkProvider>
    {
        new SerialLinkProvider(() => new SystemSerialPort(), options, portNames),
    };
```

and make the connect banner transport-aware:

```csharp
Console.WriteLine(useBle ? "Connecting (Bluetooth NUS scan) ..." : "Connecting (USB serial, auto-discover) ...");
```

In the NOT CONNECTED branch, add a BLE hint line:

```csharp
    if (useBle) Console.WriteLine("         (BLE mode: pedal powered on, Windows Bluetooth ON, VoidX app fully closed)");
```

- [ ] **Step 3: Build + suite**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: clean, all green.

- [ ] **Step 4: Commit**

```bash
git add tools/HwCheck/
git commit -m "feat: HwCheck --ble - run any bench mode over the BLE transport"
```

---

### Task 8: Docs — hardware checklist, README, CLAUDE.md

**Files:**
- Create: `docs/HARDWARE-VALIDATION-ble.md`
- Modify: `README.md` (one paragraph in the Install section)
- Modify: `CLAUDE.md` (architecture + build/run lines)

- [ ] **Step 1: Write the checklist**

Create `docs/HARDWARE-VALIDATION-ble.md`:

```markdown
# Manual validation — BLE transport

Bench gate for the BLE (Nordic UART) transport. Prereqs: pedal powered on, Windows
Bluetooth ON, **VoidX app fully closed on ALL devices** (phone included — the pedal
serves one BLE client at a time).

## Bench (HwCheck)
- [ ] `dotnet run --project tools/HwCheck -- --ble` → connects, identifies fw, lists presets
      (USB cable UNPLUGGED — proves the radio path)
- [ ] Same command with USB also plugged in → still works (transports don't fight)
- [ ] `dotnet run --project tools/HwCheck -- --ble --browse` → full read-only dump completes

## App
- [ ] USB unplugged → Connect → status ends "(Bluetooth)"; presets list loads
- [ ] Preset select + edit a parameter over BLE (live change audible/visible on pedal)
- [ ] USB plugged in → Connect → status ends "(USB)" (serial wins the order)
- [ ] Bluetooth radio OFF + USB unplugged → Connect → "Disconnected (no device found on
      USB or Bluetooth)" after the ~4s scan window; no error dialog, no crash
- [ ] One guarded amp upload over BLE (small .vxamp) — note wall-clock vs USB here: ____
- [ ] Drop test: connected over BLE, power the pedal off → next operation shows the
      Error status (no hang, no crash); power on, Connect reconnects

## Record
- BLE connect time vs USB (from logs/sonulab.log PERF lines): ____
- Negotiated MTU (MaxWriteSize) if logged: ____
```

- [ ] **Step 2: README + CLAUDE.md touch-ups**

`README.md` — in the Install section, after the "Close VoidX-Control" sentence, add:

```markdown
StompStation Manager connects over USB first and falls back to **Bluetooth** automatically
(pedal powered on, Windows Bluetooth enabled) — handy when a cable or USB port lets you down.
```

`CLAUDE.md` — update the architecture list: add `src/Sonulab.Transport.Ble` (one line: "BLE NUS transport (Windows-only, IGattPipe seam; logic unit-tested, WinRT layer bench-validated)"), note HwCheck's `--ble` flag in the harness line, and change the "usually COM6" wording to mention the USB→BLE fallback.

- [ ] **Step 3: Full suite one last time + commit**

Run: `dotnet test --nologo`
Expected: all green.

```bash
git add docs/HARDWARE-VALIDATION-ble.md README.md CLAUDE.md
git commit -m "docs: BLE hardware validation checklist; README/CLAUDE.md transport notes"
```

---

## Execution notes

- Strict order 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 (each consumes the previous task's interfaces).
- Tasks 1-4 are pure TDD against fakes. Task 5 is the deliberate no-unit-test WinRT seam (build-verified; hardware checklist is its gate). Tasks 6-7 change TFMs — expect the first build after each to restore new runtime packs (slow once, then cached).
- Amendment vs spec (documented in Task 3): no shared "reassembly helper" extraction — meter filtering already lives in `ResponseParser` (shared), and serial's pull-model loop vs BLE's push-model buffer don't unify cleanly; the collection *policy* (NUL-stop + idle-gap + first-byte) is mirrored instead. Spec's intent (no duplicated protocol knowledge) is honored via the shared `LinkProbe` and `ResponseParser`.
- The hardware checklist (Task 8) is Ed's gate, run when at the bench with Windows Bluetooth available. Everything else is autonomous.
```
