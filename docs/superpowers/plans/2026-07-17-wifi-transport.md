# WiFi Transport Implementation Plan (SP1: discover-and-connect)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect to the StompStation over WiFi (mDNS discovery + raw TCP port 8080, same protocol) as an automatic fallback after USB.

**Architecture:** Core gains the transport-agnostic `ILinkProvider` foundation (ordered providers iterated by `DeviceSession`; USB stays #1, unchanged behavior). A new platform-neutral project `Sonulab.Transport.Wifi` supplies `TcpSonuLink` (persistent socket behind an `ITcpConn` seam), a hand-rolled pure `MdnsResponseParser` (unit-tested against real captured datagrams), a thin `UdpMdnsQuerier`, and `WifiLinkProvider`.

**Tech Stack:** .NET 10 plain BCL (`System.Net.Sockets`) — no new packages, no TFM changes anywhere; xUnit.

**Spec:** `docs/superpowers/specs/2026-07-17-wifi-transport-design.md` (confirmed live facts in `PROTOCOL.md` §WiFi/TCP)

## Global Constraints

- TCP port **8080**; identical wire protocol to serial: command + trailing NUL out, response collected until the device's NUL terminator (idle-gap 120 ms / first-byte 300 ms / max-wait 2500 ms / poll 10 ms fallbacks — same defaults as `SerialLinkOptions`); stale input drained before each send.
- mDNS: PTR query for `_http._tcp.local` to `224.0.0.251:5353`; the pedal's record has TXT key `id=voidx` (THE filter — other devices advertise the same service type), SRV → port, A → IPv4. The pedal answers intermittently: **re-send the query every ~2 s** within the discovery window (single-shot queries were observed to miss).
- `Sonulab.Core` and all TFMs stay exactly as they are (`net10.0`; the new WiFi project is also `net10.0` — plain sockets, no WinRT).
- Existing `SerialSonuLink`/`SonuConnector` behavior unchanged; their tests pass unmodified. `DeviceSession`/`ConnectionViewModel` signatures generalize; their tests adapt, assertions preserved.
- Status copy (exact): connected `"{name} {version} — {compat message} ({transport})"` with transport `USB` or `WiFi`; both-fail `"Disconnected (no device found on USB or WiFi)"`.
- Tests: xUnit, file-scoped, no namespace (shared fakes may carry a namespace), global `Xunit` using; **no real network/serial in unit tests**. Live checks happen ONLY via `HwCheck` in Task 8 against the bench pedal at `192.168.8.241`, **read-only + session ops only — NO device writes of any kind tonight** (provisioning is SP2, supervised).
- Full suite green before every commit (`dotnet test --nologo`, currently 443); conventional commits.

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
  - `static class LinkProbe { static Task<bool> VerifyAsync(ISonuLink link, CancellationToken ct = default); }`
  - `sealed class SerialLinkProvider : ILinkProvider` ctor `(Func<ISerialPortStream> portFactory, SerialLinkOptions? options = null, Func<IReadOnlyList<string>>? portNames = null, IReadOnlyList<int>? bauds = null)`; `Name == "USB"`; `portNames` defaults to fresh `System.IO.Ports.SerialPort.GetPortNames()` **evaluated on every call** (fixes the stale port-snapshot bug: a pedal replugged onto a new COM number was invisible until app restart); `bauds` defaults `{ 115200 }`.

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
        var dev = new FakePresetDevice();
        var provider = new SerialLinkProvider(
            dev.CreatePort,
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
            () => new FakeSerialPort(),
            new SerialLinkOptions { FirstByteTimeoutMs = 20, MaxWaitMs = 50 },
            portNames: () => { calls++; return new[] { "COM1" }; });
        await provider.TryConnectAsync();
        await provider.TryConnectAsync();
        Assert.Equal(2, calls);
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

> Implementer note: mirror the exact fake-construction pattern the existing `SonuConnector`/transport tests use (`FakePresetDevice` + its port factory, or a scripted `FakeSerialPort`) for `Connects_via_existing_connector_path` — the intent is "provider connects through the same fake the connector tests use." Adjust the factory call to the real fake API, keeping the assertions.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.Core.Tests --nologo --filter SerialLinkProviderTests`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Implement**

Create `src/Sonulab.Core/Connection/ILinkProvider.cs`:

```csharp
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>One way of reaching the pedal (USB serial, WiFi, ...). Providers are tried in
/// order by DeviceSession; a provider returns an OPEN, identity-verified link or null.</summary>
public interface ILinkProvider
{
    /// <summary>Short transport label shown in the connection status ("USB", "WiFi").</summary>
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

In `src/Sonulab.Core/Connection/SonuConnector.cs`, replace

```csharp
                    var resp = await link.SendAsync(@"read root\sys\_name", ct);
                    bool ok = ResponseParser.NonMeterRecords(resp)
                        .Any(r => NodeRecord.TryParse(r, out var nr) && nr.Path == @"root\sys\_name");
```

with

```csharp
                    bool ok = await LinkProbe.VerifyAsync(link, ct);
```

(everything else untouched; drop now-unused usings only if flagged).

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
Expected: all pass, including untouched `SonuConnector` tests (extraction proof).

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/Connection/ tests/Sonulab.Core.Tests/SerialLinkProviderTests.cs
git commit -m "feat: ILinkProvider abstraction - LinkProbe extraction, serial provider with fresh port enumeration"
```

---

### Task 2: Core — `DeviceSession` over providers; adapt callers

**Files:**
- Modify: `src/Sonulab.Core/Connection/DeviceSession.cs` (full replacement below)
- Modify: `src/Sonulab.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs:75-83` (connector/session/ports block)
- Modify: `tools/HwCheck/Program.cs:29-45` (ports/connector/connect block)
- Test: `tests/Sonulab.Core.Tests/DeviceSessionTests.cs` (adapt), `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs` (adapt)

**Interfaces:**
- Consumes: `ILinkProvider`, `SerialLinkProvider` (Task 1).
- Produces:
  - `sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility, string? Transport = null)`
  - `DeviceSession` ctor `(IReadOnlyList<ILinkProvider> providers, CompatibilityChecker checker)`; `Task<SessionState> ConnectAsync(CancellationToken ct = default)`; `Client`/`Disconnect`/`Dispose` unchanged. Provider exceptions are caught and logged; the scan continues with the next provider.
  - `ConnectionViewModel` ctor `(DeviceSession session)`; connected status `"{name} {version} — {message} ({transport})"`.

- [ ] **Step 1: Adapt tests first**

In `tests/Sonulab.Core.Tests/DeviceSessionTests.cs`: keep every existing assertion; construction changes to wrap the existing fake in a provider. Add:

```csharp
    private sealed class FixedProvider(string name, Sonulab.Core.Transport.ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => Task.FromResult(link);
    }

    private sealed class ThrowingProvider : ILinkProvider
    {
        public string Name => "Broken";
        public Task<Sonulab.Core.Transport.ISonuLink?> TryConnectAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("transport unavailable");
    }

    [Fact]
    public async Task Second_provider_is_tried_when_first_returns_null_and_transport_is_reported()
    {
        var workingLink = /* obtain an open ISonuLink from FakePresetDevice the way existing tests do */;
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var state = await session.ConnectAsync();
        Assert.True(state.Connected);
        Assert.Equal("WiFi", state.Transport);
    }

    [Fact]
    public async Task Throwing_provider_is_skipped_not_fatal()
    {
        var workingLink = /* same helper as above */;
        var session = new DeviceSession(
            new ILinkProvider[] { new ThrowingProvider(), new FixedProvider("WiFi", workingLink) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var state = await session.ConnectAsync();
        Assert.True(state.Connected);
        Assert.Equal("WiFi", state.Transport);
    }
```

> Implementer note: `/* obtain an open ISonuLink ... */` — reuse the exact helper/pattern the existing DeviceSessionTests use to get a working link from `FakePresetDevice`. Only construction changes across the file; assertions stay.

In `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`: construction → provider-based session + `new ConnectionViewModel(session)`; update asserted connected-status strings to end with `(USB)` (or the provider name the test wires); leave the not-connected copy as-is for now (it changes in Task 6).

- [ ] **Step 2: Verify compile failure**

Run: `dotnet test tests/Sonulab.Core.Tests --nologo --filter DeviceSessionTests`
Expected: FAIL — no provider ctor.

- [ ] **Step 3: Rewrite DeviceSession**

Replace `src/Sonulab.Core/Connection/DeviceSession.cs` content with:

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
                // A broken transport (no network stack, no radio) must not abort the whole scan.
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

`src/Sonulab.App/ViewModels/ConnectionViewModel.cs` — remove `_ports`/`Bauds`, ctor `(DeviceSession session)`, and:

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

`src/Sonulab.App/ViewModels/MainWindowViewModel.cs` — replace the connector/session/ports block with:

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

(keep the adaptive-settle comment; delete the `GetPortNames`/`portList` lines; `using Sonulab.Core.Connection;` if missing).

`tools/HwCheck/Program.cs` — replace lines 29-45 with:

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

(keep every downstream line using `state`/`session` untouched).

- [ ] **Step 5: Full suite + build**

Run: `dotnet test --nologo && dotnet build --nologo`
Expected: all green; HwCheck compiles.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Core/Connection/DeviceSession.cs src/Sonulab.App/ViewModels/ tools/HwCheck/Program.cs tests/
git commit -m "refactor: DeviceSession iterates ordered link providers; transport surfaced in status"
```

---

### Task 3: `Sonulab.Transport.Wifi` — `TcpSonuLink` over `ITcpConn` (TDD)

**Files:**
- Create: `src/Sonulab.Transport.Wifi/Sonulab.Transport.Wifi.csproj`
- Create: `src/Sonulab.Transport.Wifi/ITcpConn.cs`
- Create: `src/Sonulab.Transport.Wifi/TcpLinkOptions.cs`
- Create: `src/Sonulab.Transport.Wifi/TcpSonuLink.cs`
- Create: `src/Sonulab.Transport.Wifi/SystemTcpConn.cs`
- Create: `tests/Sonulab.Transport.Wifi.Tests/Sonulab.Transport.Wifi.Tests.csproj`
- Create: `tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs`
- Create: `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkTests.cs`
- Modify: `Sonulab.slnx` (add both projects, existing entry format)

**Interfaces:**
- Consumes: `ISonuLink` (Core).
- Produces (namespace `Sonulab.Transport.Wifi`):
  - `interface ITcpConn { bool Connected { get; } int Available { get; } Task ConnectAsync(string host, int port, CancellationToken ct = default); Task SendAsync(byte[] data, CancellationToken ct = default); int Receive(byte[] buffer); void Close(); }`
  - `sealed class TcpLinkOptions { int PollMs=10; int IdleGapMs=120; int MaxWaitMs=2500; int FirstByteTimeoutMs=300; int ConnectTimeoutMs=3000; }` (all `{ get; init; }`)
  - `sealed class TcpSonuLink : ISonuLink` ctor `(ITcpConn conn, string host, int port, TcpLinkOptions? options = null)`
  - `sealed class SystemTcpConn : ITcpConn` (real sockets; not unit-tested)
  - Test double `FakeTcpConn` (Task 5 reuses it).

- [ ] **Step 1: Projects**

`src/Sonulab.Transport.Wifi/Sonulab.Transport.Wifi.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
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

`tests/Sonulab.Transport.Wifi.Tests/Sonulab.Transport.Wifi.Tests.csproj` — copy `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj`, keep `net10.0`, reference `..\..\src\Sonulab.Transport.Wifi\Sonulab.Transport.Wifi.csproj` + Core, drop any `<Compile Include>` of shared fakes. Add both projects to `Sonulab.slnx`.

- [ ] **Step 2: Write the failing tests**

Create `tests/Sonulab.Transport.Wifi.Tests/FakeTcpConn.cs`:

```csharp
using Sonulab.Transport.Wifi;

namespace Sonulab.Transport.Wifi.Tests;

/// <summary>Scripted TCP connection. Sends are recorded; when a NUL-terminated command has
/// accumulated, RespondWith (if set) queues response bytes for the Available/Receive pull side.</summary>
public sealed class FakeTcpConn : ITcpConn
{
    private readonly MemoryStream _pendingCmd = new();
    private readonly Queue<byte> _rx = new();
    public List<byte[]> Sends { get; } = new();
    public (string Host, int Port)? ConnectedTo { get; private set; }
    public bool Connected { get; private set; }
    public bool FailConnect { get; set; }
    public Func<string, byte[]>? RespondWith;   // command (no NUL) -> raw response bytes

    public int Available { get { lock (_rx) return _rx.Count; } }

    public Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        if (FailConnect) throw new InvalidOperationException("connect refused");
        Connected = true; ConnectedTo = (host, port);
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        Sends.Add(data);
        _pendingCmd.Write(data, 0, data.Length);
        var all = _pendingCmd.ToArray();
        int nul = Array.IndexOf(all, (byte)0);
        if (nul >= 0)
        {
            _pendingCmd.SetLength(0);
            var cmd = System.Text.Encoding.ASCII.GetString(all, 0, nul);
            if (RespondWith is not null) Feed(RespondWith(cmd));
        }
        return Task.CompletedTask;
    }

    public int Receive(byte[] buffer)
    {
        lock (_rx)
        {
            int n = Math.Min(buffer.Length, _rx.Count);
            for (int i = 0; i < n; i++) buffer[i] = _rx.Dequeue();
            return n;
        }
    }

    public void Close() => Connected = false;

    public void Feed(byte[] data) { lock (_rx) foreach (var b in data) _rx.Enqueue(b); }
    public void Feed(string ascii) => Feed(System.Text.Encoding.ASCII.GetBytes(ascii));
}
```

Create `tests/Sonulab.Transport.Wifi.Tests/TcpSonuLinkTests.cs`:

```csharp
using System.Text;
using Sonulab.Transport.Wifi;
using Sonulab.Transport.Wifi.Tests;

public class TcpSonuLinkTests
{
    private static readonly TcpLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static (TcpSonuLink link, FakeTcpConn conn) Open()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        link.OpenAsync().GetAwaiter().GetResult();
        return (link, conn);
    }

    [Fact]
    public async Task Open_connects_to_host_and_port_and_IsOpen_tracks_conn()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        Assert.False(link.IsOpen);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        Assert.Equal(("192.168.8.241", 8080), conn.ConnectedTo);
        link.Close();
        Assert.False(link.IsOpen);
    }

    [Fact]
    public async Task Command_is_sent_with_trailing_nul_and_response_collected_to_nul()
    {
        var (link, conn) = Open();
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0");
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.Contains("AMP Station", resp);
        var sent = conn.Sends.SelectMany(s => s).ToArray();
        Assert.Equal((byte)0, sent[^1]);
        Assert.Equal(@"read root\sys\_name", Encoding.ASCII.GetString(sent, 0, sent.Length - 1));
    }

    [Fact]
    public async Task Response_arriving_in_pieces_is_reassembled()
    {
        var (link, conn) = Open();
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\sys\\wifi\\ssid:{\"val");
        var task = link.SendAsync(@"read root\sys\wifi\ssid");
        await Task.Delay(10);
        conn.Feed("ue\":\"Duke Park Mesh\"}\0");
        var resp = await task;
        Assert.Contains("Duke Park Mesh", resp);
    }

    [Fact]
    public async Task Stale_bytes_before_send_are_drained()
    {
        var (link, conn) = Open();
        conn.Feed("leftover-garbage\r\n");
        conn.RespondWith = _ => Encoding.ASCII.GetBytes("root\\app\\preset:{\"value\":\"Pano-Verb\"}\0");
        var resp = await link.SendAsync(@"read root\app\preset");
        Assert.DoesNotContain("leftover", resp);
        Assert.Contains("Pano-Verb", resp);
    }

    [Fact]
    public async Task No_response_command_returns_empty_after_first_byte_timeout()
    {
        var (link, conn) = Open();                                  // RespondWith null -> silence
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await link.SendAsync(@"write root\app\x:{""value"":1}");
        Assert.Equal("", resp);
        Assert.True(sw.ElapsedMilliseconds < Fast.MaxWaitMs, "stops at FirstByteTimeout, not MaxWait");
    }

    [Fact]
    public async Task Send_on_closed_link_throws()
    {
        var conn = new FakeTcpConn();
        var link = new TcpSonuLink(conn, "192.168.8.241", 8080, Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read root"));
    }
}
```

- [ ] **Step 3: Verify failure**

Run: `dotnet test tests/Sonulab.Transport.Wifi.Tests --nologo`
Expected: FAIL — types missing.

- [ ] **Step 4: Implement**

Create `src/Sonulab.Transport.Wifi/ITcpConn.cs`:

```csharp
namespace Sonulab.Transport.Wifi;

/// <summary>Seam over the OS TCP socket (mirrors ISerialPortStream's pull model:
/// Available + Receive) so TcpSonuLink is unit-testable with a fake.</summary>
public interface ITcpConn
{
    bool Connected { get; }
    int Available { get; }
    Task ConnectAsync(string host, int port, CancellationToken ct = default);
    Task SendAsync(byte[] data, CancellationToken ct = default);
    /// <summary>Read up to buffer.Length of the currently Available bytes; returns count read.</summary>
    int Receive(byte[] buffer);
    void Close();
}
```

Create `src/Sonulab.Transport.Wifi/TcpLinkOptions.cs`:

```csharp
namespace Sonulab.Transport.Wifi;

/// <summary>Response-collection policy — same semantics and defaults as SerialLinkOptions'
/// read loop (identical wire protocol; PROTOCOL.md §WiFi/TCP), plus a connect timeout.</summary>
public sealed class TcpLinkOptions
{
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 2500;
    public int FirstByteTimeoutMs { get; init; } = 300;
    public int ConnectTimeoutMs { get; init; } = 3000;
}
```

Create `src/Sonulab.Transport.Wifi/TcpSonuLink.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Wifi;

/// <summary>ISonuLink over a persistent TCP socket (port 8080 on the pedal). Same bytes as
/// serial: command + NUL out, response collected until the device's NUL terminator.
/// No ESP32 reset on connect (that's a serial DTR/RTS artifact) — no settle delay; the
/// first-command-empty quirk is handled by the caller's probe retry (PROTOCOL.md §WiFi/TCP).</summary>
public sealed class TcpSonuLink : ISonuLink
{
    private static readonly byte[] Nul = { 0 };
    private readonly ITcpConn _conn;
    private readonly string _host;
    private readonly int _port;
    private readonly TcpLinkOptions _options;

    public TcpSonuLink(ITcpConn conn, string host, int port, TcpLinkOptions? options = null)
    {
        _conn = conn; _host = host; _port = port; _options = options ?? new TcpLinkOptions();
    }

    public bool IsOpen => _conn.Connected;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ConnectTimeoutMs);
        await _conn.ConnectAsync(_host, _port, timeout.Token);
    }

    public void Close() => _conn.Close();

    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!_conn.Connected) throw new InvalidOperationException("TCP link is not open.");

        // Drain stale bytes (serial: DiscardInBuffer). TCP has no meter stream, but a prior
        // command's tail could linger after a timeout.
        var drain = new byte[4096];
        while (_conn.Available > 0) _conn.Receive(drain);

        var bytes = Encoding.ASCII.GetBytes(command);
        await _conn.SendAsync(bytes, ct);
        await _conn.SendAsync(Nul, ct);

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        long lastData = 0;
        bool sawData = false;

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _conn.Available;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _conn.Receive(buf);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = sw.ElapsedMilliseconds;
                if (Array.IndexOf(buf, (byte)0, 0, n) >= 0) break;   // device NUL-terminates responses
            }
            else
            {
                if (sawData && sw.ElapsedMilliseconds - lastData >= _options.IdleGapMs) break;
                if (!sawData && sw.ElapsedMilliseconds >= _options.FirstByteTimeoutMs) break;
                await Task.Delay(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }
}
```

Create `src/Sonulab.Transport.Wifi/SystemTcpConn.cs`:

```csharp
using System.Net.Sockets;

namespace Sonulab.Transport.Wifi;

/// <summary>Real-socket ITcpConn. Deliberately thin — logic lives in TcpSonuLink.
/// Not unit-tested (live checks via HwCheck --wifi).</summary>
public sealed class SystemTcpConn : ITcpConn
{
    private TcpClient? _client;

    public bool Connected => _client?.Connected == true;
    public int Available => _client?.Available ?? 0;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        var c = _client ?? throw new InvalidOperationException("TCP connection is not open.");
        return c.GetStream().WriteAsync(data, 0, data.Length, ct);
    }

    public int Receive(byte[] buffer)
    {
        var c = _client ?? throw new InvalidOperationException("TCP connection is not open.");
        return c.GetStream().Read(buffer, 0, Math.Min(buffer.Length, c.Available));
    }

    public void Close() { _client?.Close(); _client = null; }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --nologo`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.Transport.Wifi/ tests/Sonulab.Transport.Wifi.Tests/ Sonulab.slnx
git commit -m "feat: TcpSonuLink - persistent-socket transport over ITcpConn seam"
```

---

### Task 4: mDNS — pure parser + query builder (TDD with real captured datagrams)

**Files:**
- Create: `src/Sonulab.Transport.Wifi/MdnsRecord.cs`
- Create: `src/Sonulab.Transport.Wifi/MdnsMessages.cs` (query builder + response parser)
- Test: `tests/Sonulab.Transport.Wifi.Tests/MdnsMessagesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `Sonulab.Transport.Wifi`):
  - `sealed record MdnsRecord(string InstanceName, string Host, string Address, int Port, string? DeviceName)`
  - `static class MdnsMessages { static byte[] BuildHttpTcpPtrQuery(); static MdnsRecord? TryParsePedal(byte[] datagram); }` — `TryParsePedal` returns the record iff the datagram advertises `_http._tcp.local` with TXT containing `id=voidx`; null otherwise (including malformed input — it must never throw).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Transport.Wifi.Tests/MdnsMessagesTests.cs`:

```csharp
using Sonulab.Transport.Wifi;

public class MdnsMessagesTests
{
    // Real datagram captured from the pedal (192.168.8.241) on 2026-07-17:
    // PTR _http._tcp.local -> voidxc7e811051914272110b41dc7c558._http._tcp.local
    // SRV port=8080 target=voidxc7e811051914272110b41dc7c558.local
    // TXT id=voidx, MAC=10:B4:1D:C7:C5:5A, name=AMP Station ; A -> 192.168.8.241
    private const string PedalB64 =
        "AACEAAABAAMAAAABBV9odHRwBF90Y3AFbG9jYWwAAAwAAcAMAAwAAQAAEZQAJCF2b2lkeGM3ZTgxMTA1MTkxNDI3MjExMGI0MWRjN2M1NTjADMAuACEAAQAAAHgAKgAAAAAfkCF2b2lkeGM3ZTgxMTA1MTkxNDI3MjExMGI0MWRjN2M1NTjAF8AuABAAAQAAEZQAMAhpZD12b2lkeBVNQUM9MTA6QjQ6MUQ6Qzc6QzU6NUEQbmFtZT1BTVAgU3RhdGlvbsBkAAEAAQAAAHgABMCoCPE=";

    // Real decoy captured the same night: a Canon printer also advertising _http._tcp
    // (TXT has path=/ but NO id=voidx) — the parser must reject it.
    private const string CanonB64 =
        "AACAAAAAAAEAAAAFBV9odHRwBF90Y3AFbG9jYWwAAAwAAQAAD6AAFhNDYW5vbiBNRjc1MEMgU2VyaWVzwAwLQ2Fub25mMTA0N2TAFwABAAEAAAB4AATAqAigwCgAIQABAAAAeAAIAAAAAABQwD7AKAAQAAEAAA+gAAcGcGF0aD0vwD4ALwABAAAPoAAIwD4ABEAAAATAKAAvAAEAAA+gAAnAKAAFAACAAEA=";

    [Fact]
    public void Parses_the_real_pedal_datagram()
    {
        var rec = MdnsMessages.TryParsePedal(Convert.FromBase64String(PedalB64));
        Assert.NotNull(rec);
        Assert.Equal("voidxc7e811051914272110b41dc7c558", rec!.InstanceName);
        Assert.Equal("voidxc7e811051914272110b41dc7c558.local", rec.Host);
        Assert.Equal("192.168.8.241", rec.Address);
        Assert.Equal(8080, rec.Port);
        Assert.Equal("AMP Station", rec.DeviceName);
    }

    [Fact]
    public void Rejects_the_canon_printer_decoy()
        => Assert.Null(MdnsMessages.TryParsePedal(Convert.FromBase64String(CanonB64)));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Malformed_datagrams_return_null_never_throw(byte[] junk)
        => Assert.Null(MdnsMessages.TryParsePedal(junk));

    [Fact]
    public void Truncated_real_datagram_returns_null()
    {
        var full = Convert.FromBase64String(PedalB64);
        Assert.Null(MdnsMessages.TryParsePedal(full[..40]));
    }

    [Fact]
    public void Query_is_a_wellformed_ptr_question_for_http_tcp_local()
    {
        var q = MdnsMessages.BuildHttpTcpPtrQuery();
        // Header: id=0, flags=0, QD=1, AN/NS/AR=0
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        // QNAME _http._tcp.local, QTYPE=PTR(12), QCLASS=IN(1)
        Assert.Equal((byte)5, q[12]);
        Assert.Equal("_http", System.Text.Encoding.ASCII.GetString(q, 13, 5));
        Assert.Equal((byte)4, q[18]);
        Assert.Equal("_tcp", System.Text.Encoding.ASCII.GetString(q, 19, 4));
        Assert.Equal((byte)5, q[23]);
        Assert.Equal("local", System.Text.Encoding.ASCII.GetString(q, 24, 5));
        Assert.Equal(0, q[29]);
        Assert.Equal(12, (q[30] << 8) | q[31]);
        Assert.Equal(1, (q[32] << 8) | q[33]);
    }
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Sonulab.Transport.Wifi.Tests --nologo --filter MdnsMessagesTests`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement**

Create `src/Sonulab.Transport.Wifi/MdnsRecord.cs`:

```csharp
namespace Sonulab.Transport.Wifi;

/// <summary>The pedal's parsed mDNS advertisement (PROTOCOL.md §WiFi/TCP).</summary>
public sealed record MdnsRecord(string InstanceName, string Host, string Address, int Port, string? DeviceName);
```

Create `src/Sonulab.Transport.Wifi/MdnsMessages.cs`:

```csharp
using System.Text;

namespace Sonulab.Transport.Wifi;

/// <summary>Hand-rolled one-shot mDNS (RFC 6762/1035 subset): build the _http._tcp.local PTR
/// query; parse a response datagram into the pedal's record. The pedal is identified by the
/// TXT key id=voidx (other devices — printers etc. — advertise the same service type).</summary>
public static class MdnsMessages
{
    private const string ServiceName = "_http._tcp.local";

    public static byte[] BuildHttpTcpPtrQuery()
    {
        var ms = new MemoryStream();
        // Header: ID=0, flags=0, QDCOUNT=1, ANCOUNT=NSCOUNT=ARCOUNT=0
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in ServiceName.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 12, 0, 1 });   // QTYPE=PTR, QCLASS=IN
        return ms.ToArray();
    }

    public static MdnsRecord? TryParsePedal(byte[] datagram)
    {
        try { return ParsePedal(datagram); }
        catch { return null; }   // malformed/truncated input must never throw to callers
    }

    private static MdnsRecord? ParsePedal(byte[] d)
    {
        if (d.Length < 12) return null;
        int qd = (d[4] << 8) | d[5];
        int answers = ((d[6] << 8) | d[7]) + ((d[8] << 8) | d[9]) + ((d[10] << 8) | d[11]);
        int off = 12;
        for (int i = 0; i < qd; i++) { (_, off) = ReadName(d, off); off += 4; }

        string? instance = null, host = null, address = null, deviceName = null;
        int port = 0;
        bool isVoidx = false;

        for (int i = 0; i < answers; i++)
        {
            var (name, o1) = ReadName(d, off);
            off = o1;
            if (off + 10 > d.Length) return null;
            int rtype = (d[off] << 8) | d[off + 1];
            int rdlen = (d[off + 8] << 8) | d[off + 9];
            int rdOff = off + 10;
            if (rdOff + rdlen > d.Length) return null;

            switch (rtype)
            {
                case 12: // PTR: _http._tcp.local -> instance
                {
                    var (target, _) = ReadName(d, rdOff);
                    if (name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                        instance = target.Split('.')[0];
                    break;
                }
                case 33: // SRV: priority(2) weight(2) port(2) target
                {
                    port = (d[rdOff + 4] << 8) | d[rdOff + 5];
                    (host, _) = ReadName(d, rdOff + 6);
                    instance ??= name.Split('.')[0];
                    break;
                }
                case 16: // TXT: length-prefixed key=value strings
                {
                    int p = rdOff;
                    while (p < rdOff + rdlen)
                    {
                        int len = d[p];
                        var kv = Encoding.UTF8.GetString(d, p + 1, len);
                        if (kv == "id=voidx") isVoidx = true;
                        else if (kv.StartsWith("name=", StringComparison.Ordinal)) deviceName = kv[5..];
                        p += 1 + len;
                    }
                    break;
                }
                case 1: // A
                    if (rdlen == 4) address = $"{d[rdOff]}.{d[rdOff + 1]}.{d[rdOff + 2]}.{d[rdOff + 3]}";
                    break;
            }
            off = rdOff + rdlen;
        }

        return isVoidx && instance is not null && host is not null && address is not null && port > 0
            ? new MdnsRecord(instance, host, address, port, deviceName)
            : null;
    }

    /// <summary>RFC 1035 name decoding with compression pointers. Returns (name, offset after
    /// the name at the ORIGINAL position — pointers do not advance it).</summary>
    private static (string Name, int NextOffset) ReadName(byte[] d, int off)
    {
        var parts = new List<string>();
        int next = -1;
        int hops = 0;
        while (true)
        {
            if (off >= d.Length || ++hops > 64) throw new InvalidDataException("bad name");
            int len = d[off];
            if (len == 0) { if (next < 0) next = off + 1; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (next < 0) next = off + 2;
                off = ((len & 0x3F) << 8) | d[off + 1];
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(d, off + 1, len));
            off += 1 + len;
        }
        return (string.Join('.', parts), next);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo`
Expected: all green — the parser proves itself against the real pedal datagram and rejects the real printer decoy.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Transport.Wifi/MdnsRecord.cs src/Sonulab.Transport.Wifi/MdnsMessages.cs tests/Sonulab.Transport.Wifi.Tests/MdnsMessagesTests.cs
git commit -m "feat: hand-rolled mDNS query builder + pedal-record parser, tested on real captured datagrams"
```

---

### Task 5: `UdpMdnsQuerier` + `WifiLinkProvider`

**Files:**
- Create: `src/Sonulab.Transport.Wifi/IMdnsQuerier.cs`
- Create: `src/Sonulab.Transport.Wifi/UdpMdnsQuerier.cs`
- Create: `src/Sonulab.Transport.Wifi/WifiLinkProvider.cs`
- Test: `tests/Sonulab.Transport.Wifi.Tests/WifiLinkProviderTests.cs`

**Interfaces:**
- Consumes: `ILinkProvider`, `LinkProbe` (Task 1); `TcpSonuLink`, `ITcpConn`, `TcpLinkOptions`, `FakeTcpConn` (Task 3); `MdnsMessages`, `MdnsRecord` (Task 4).
- Produces (namespace `Sonulab.Transport.Wifi`):
  - `interface IMdnsQuerier { Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default); }`
  - `sealed class UdpMdnsQuerier : IMdnsQuerier` — real multicast; re-sends the query every 2 s within the window (the pedal answers intermittently); never throws (any socket/network error → null). Thin; not unit-tested.
  - `sealed class WifiLinkProvider : ILinkProvider` ctor `(IMdnsQuerier querier, TimeSpan discoveryTimeout, Func<ITcpConn>? connFactory = null, TcpLinkOptions? options = null, int probeAttempts = 3, int probeRetryDelayMs = 200)`; `Name == "WiFi"`; plus `static WifiLinkProvider ForKnownEndpoint(string host, int port = 8080, Func<ITcpConn>? connFactory = null, TcpLinkOptions? options = null)` (skips mDNS — HwCheck `--ip`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.Transport.Wifi.Tests/WifiLinkProviderTests.cs`:

```csharp
using System.Text;
using Sonulab.Transport.Wifi;
using Sonulab.Transport.Wifi.Tests;

public class WifiLinkProviderTests
{
    private static readonly TcpLinkOptions Fast = new()
    { PollMs = 1, IdleGapMs = 30, MaxWaitMs = 500, FirstByteTimeoutMs = 40 };

    private static readonly MdnsRecord Pedal =
        new("voidxc7e8", "voidxc7e8.local", "192.168.8.241", 8080, "AMP Station");

    private sealed class FakeQuerier(MdnsRecord? result) : IMdnsQuerier
    {
        public Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static FakeTcpConn AnsweringConn()
    {
        var conn = new FakeTcpConn();
        conn.RespondWith = cmd => cmd == @"read root\sys\_name"
            ? Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0")
            : Array.Empty<byte>();
        return conn;
    }

    [Fact]
    public void Name_is_WiFi()
        => Assert.Equal("WiFi", new WifiLinkProvider(new FakeQuerier(null), TimeSpan.Zero).Name);

    [Fact]
    public async Task Connects_to_discovered_endpoint_and_verifies_identity()
    {
        var conn = AnsweringConn();
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
        Assert.Equal(("192.168.8.241", 8080), conn.ConnectedTo);
    }

    [Fact]
    public async Task Returns_null_when_discovery_finds_nothing()
    {
        var provider = new WifiLinkProvider(new FakeQuerier(null), TimeSpan.FromSeconds(1),
            connFactory: () => new FakeTcpConn(), options: Fast);
        Assert.Null(await provider.TryConnectAsync());
    }

    [Fact]
    public async Task Probe_retries_cover_the_empty_first_response_quirk()
    {
        // Live finding: the FIRST command on a fresh TCP connection can come back as an
        // empty record; the provider must retry the probe, not give up.
        int n = 0;
        var conn = new FakeTcpConn();
        conn.RespondWith = cmd =>
        {
            if (cmd != @"read root\sys\_name") return Array.Empty<byte>();
            n++;
            return n == 1
                ? Encoding.ASCII.GetBytes("\r\n\0")                    // the observed empty record
                : Encoding.ASCII.GetBytes("root\\sys\\_name:{\"value\":\"AMP Station\"}\0");
        };
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast, probeAttempts: 3, probeRetryDelayMs: 1);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Silent_device_fails_probe_and_link_is_closed()
    {
        var conn = new FakeTcpConn();                                   // never answers
        var provider = new WifiLinkProvider(new FakeQuerier(Pedal), TimeSpan.FromSeconds(1),
            connFactory: () => conn, options: Fast, probeAttempts: 2, probeRetryDelayMs: 1);
        Assert.Null(await provider.TryConnectAsync());
        Assert.False(conn.Connected);
    }

    [Fact]
    public async Task ForKnownEndpoint_skips_discovery()
    {
        var conn = AnsweringConn();
        var provider = WifiLinkProvider.ForKnownEndpoint("10.0.0.5", 8080, () => conn, Fast);
        var link = await provider.TryConnectAsync();
        Assert.NotNull(link);
        Assert.Equal(("10.0.0.5", 8080), conn.ConnectedTo);
    }
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test tests/Sonulab.Transport.Wifi.Tests --nologo --filter WifiLinkProviderTests`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement**

Create `src/Sonulab.Transport.Wifi/IMdnsQuerier.cs`:

```csharp
namespace Sonulab.Transport.Wifi;

public interface IMdnsQuerier
{
    /// <summary>Browse for the pedal; null when nothing valid answered within the timeout.
    /// Implementations must not throw for network-level failures.</summary>
    Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default);
}
```

Create `src/Sonulab.Transport.Wifi/UdpMdnsQuerier.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Sonulab.Transport.Wifi;

/// <summary>Real multicast mDNS browse. The pedal answers intermittently, so the query is
/// RE-SENT every 2 s within the window (a single-shot query was observed to miss). Thin
/// (socket plumbing only); parsing is the unit-tested MdnsMessages.</summary>
public sealed class UdpMdnsQuerier : IMdnsQuerier
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = MdnsMessages.BuildHttpTcpPtrQuery();
            var deadline = DateTime.UtcNow + timeout;
            var nextSend = DateTime.MinValue;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= nextSend)
                {
                    await udp.SendAsync(query, query.Length, Multicast);
                    nextSend = DateTime.UtcNow.AddSeconds(2);
                }

                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recvCts.CancelAfter(300);
                try
                {
                    var result = await udp.ReceiveAsync(recvCts.Token);
                    var rec = MdnsMessages.TryParsePedal(result.Buffer);
                    if (rec is not null)
                    {
                        Log.Info("mDNS found pedal {0} at {1}:{2}", rec.InstanceName, rec.Address, rec.Port);
                        return rec;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // receive window elapsed — loop re-sends / keeps listening
                }
            }
            Log.Info("mDNS browse: no pedal within {0}", timeout);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Info("mDNS unavailable: {0}", ex.Message);   // no network / multicast blocked
            return null;
        }
    }
}
```

Create `src/Sonulab.Transport.Wifi/WifiLinkProvider.cs`:

```csharp
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Wifi;

/// <summary>WiFi transport provider: mDNS-discover the pedal, open a persistent TCP link,
/// verify identity via the shared LinkProbe (with retries — the first command on a fresh
/// socket can return an empty record; PROTOCOL.md §WiFi/TCP).</summary>
public sealed class WifiLinkProvider : ILinkProvider
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IMdnsQuerier _querier;
    private readonly TimeSpan _discoveryTimeout;
    private readonly Func<ITcpConn> _connFactory;
    private readonly TcpLinkOptions? _options;
    private readonly int _probeAttempts;
    private readonly int _probeRetryDelayMs;

    public WifiLinkProvider(
        IMdnsQuerier querier,
        TimeSpan discoveryTimeout,
        Func<ITcpConn>? connFactory = null,
        TcpLinkOptions? options = null,
        int probeAttempts = 3,
        int probeRetryDelayMs = 200)
    {
        _querier = querier;
        _discoveryTimeout = discoveryTimeout;
        _connFactory = connFactory ?? (() => new SystemTcpConn());
        _options = options;
        _probeAttempts = Math.Max(1, probeAttempts);
        _probeRetryDelayMs = probeRetryDelayMs;
    }

    /// <summary>Bench/diagnostic path (HwCheck --ip): pin a known endpoint, skip mDNS.</summary>
    public static WifiLinkProvider ForKnownEndpoint(
        string host, int port = 8080, Func<ITcpConn>? connFactory = null, TcpLinkOptions? options = null)
        => new(new FixedQuerier(new MdnsRecord("pinned", host, host, port, null)),
               TimeSpan.Zero, connFactory, options);

    private sealed class FixedQuerier(MdnsRecord rec) : IMdnsQuerier
    {
        public Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<MdnsRecord?>(rec);
    }

    public string Name => "WiFi";

    public async Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default)
    {
        var rec = await _querier.DiscoverPedalAsync(_discoveryTimeout, ct);
        if (rec is null) return null;

        var link = new TcpSonuLink(_connFactory(), rec.Address, rec.Port, _options);
        try
        {
            await link.OpenAsync(ct);
            for (int attempt = 0; attempt < _probeAttempts; attempt++)
            {
                if (await LinkProbe.VerifyAsync(link, ct))
                {
                    Log.Info("PERF connect transport=WiFi endpoint={0}:{1} attempts={2}",
                        rec.Address, rec.Port, attempt + 1);
                    return link;
                }
                if (attempt + 1 < _probeAttempts) await Task.Delay(_probeRetryDelayMs, ct);
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
git add src/Sonulab.Transport.Wifi/IMdnsQuerier.cs src/Sonulab.Transport.Wifi/UdpMdnsQuerier.cs src/Sonulab.Transport.Wifi/WifiLinkProvider.cs tests/Sonulab.Transport.Wifi.Tests/WifiLinkProviderTests.cs
git commit -m "feat: WifiLinkProvider - mDNS discovery, TCP link, probe retries for first-command quirk"
```

---

### Task 6: App wiring — WiFi fallback provider + status copy

**Files:**
- Modify: `src/Sonulab.App/Sonulab.App.csproj` (project reference only — TFM unchanged)
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs` (add WiFi provider)
- Modify: `src/Sonulab.App/ViewModels/ConnectionViewModel.cs` (failure copy)
- Test: `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs` (failure-copy assertion)

**Interfaces:**
- Consumes: `WifiLinkProvider`, `UdpMdnsQuerier` (Task 5); provider-based session (Task 2).
- Produces: Connect tries USB then WiFi; both-fail copy exactly `"Disconnected (no device found on USB or WiFi)"`.

- [ ] **Step 1: Update the failure-copy test**

In `tests/Sonulab.App.Tests/ConnectionViewModelTests.cs`, the not-connected expectation becomes:

```csharp
        Assert.Equal("Disconnected (no device found on USB or WiFi)", vm.Status);
```

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter ConnectionViewModelTests`
Expected: FAIL on that assertion.

- [ ] **Step 2: Implement**

`ConnectionViewModel.cs` not-connected line:

```csharp
            if (!state.Connected) { Status = "Disconnected (no device found on USB or WiFi)"; return; }
```

`Sonulab.App.csproj` — add:

```xml
    <ProjectReference Include="..\Sonulab.Transport.Wifi\Sonulab.Transport.Wifi.csproj" />
```

`MainWindowViewModel.cs` — extend the provider list from Task 2:

```csharp
        var providers = new List<ILinkProvider>
        {
            new SerialLinkProvider(() => new SystemSerialPort(), options),
            // WiFi fallback: ~3s mDNS browse (query re-sent every 2s); returns null silently
            // when no network / multicast blocked / no pedal on the LAN.
            new Sonulab.Transport.Wifi.WifiLinkProvider(
                new Sonulab.Transport.Wifi.UdpMdnsQuerier(), TimeSpan.FromSeconds(3)),
        };
```

- [ ] **Step 3: Full suite + build**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: all green. TFM unchanged, so the installer's PublishDir is unaffected — no packaging changes.

- [ ] **Step 4: Commit**

```bash
git add src/Sonulab.App/ tests/Sonulab.App.Tests/ConnectionViewModelTests.cs
git commit -m "feat: app connects via USB then WiFi mDNS fallback"
```

---

### Task 7: HwCheck `--wifi [--ip <addr>]`

**Files:**
- Modify: `tools/HwCheck/HwCheck.csproj` (add WiFi project reference; TFM unchanged)
- Modify: `tools/HwCheck/Program.cs` (flags + provider selection + header comment)

**Interfaces:**
- Consumes: `WifiLinkProvider`, `UdpMdnsQuerier` (Task 5); provider-based session (Task 2).
- Produces: `--wifi` runs any existing mode over WiFi via mDNS; `--wifi --ip <addr>` pins the endpoint (skips mDNS). WiFi-only when the flag is present (no serial attempted — deterministic bench).

- [ ] **Step 1: Reference**

`tools/HwCheck/HwCheck.csproj` — add `<ProjectReference Include="..\..\src\Sonulab.Transport.Wifi\Sonulab.Transport.Wifi.csproj" />`.

- [ ] **Step 2: Flags**

In `tools/HwCheck/Program.cs`, add to the header comment block:

```csharp
//   dotnet run --project tools/HwCheck -- --wifi [--ip <addr>] [...]  # any mode over WiFi (mDNS discovery; --ip pins the endpoint)
```

Replace the provider-list construction from Task 2 with:

```csharp
bool useWifi = Array.IndexOf(args, "--wifi") >= 0;
int ipFlag = Array.IndexOf(args, "--ip");
string? pinnedIp = ipFlag >= 0 && ipFlag + 1 < args.Length ? args[ipFlag + 1] : null;

var providers = useWifi
    ? new List<ILinkProvider>
    {
        pinnedIp is not null
            ? Sonulab.Transport.Wifi.WifiLinkProvider.ForKnownEndpoint(pinnedIp)
            : new Sonulab.Transport.Wifi.WifiLinkProvider(
                new Sonulab.Transport.Wifi.UdpMdnsQuerier(), TimeSpan.FromSeconds(6)),
    }
    : new List<ILinkProvider>
    {
        new SerialLinkProvider(() => new SystemSerialPort(), options, portNames),
    };
```

Transport-aware banner + failure hint:

```csharp
Console.WriteLine(useWifi
    ? (pinnedIp is not null ? $"Connecting (WiFi, pinned {pinnedIp}:8080) ..." : "Connecting (WiFi, mDNS discovery) ...")
    : "Connecting (USB serial, auto-discover) ...");
```

and in the NOT CONNECTED branch:

```csharp
    if (useWifi) Console.WriteLine("         (WiFi mode: pedal powered + on the same network; multicast/mDNS not blocked)");
```

- [ ] **Step 3: Build + suite**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: clean, all green.

- [ ] **Step 4: Commit**

```bash
git add tools/HwCheck/
git commit -m "feat: HwCheck --wifi - any bench mode over TCP, mDNS or pinned IP"
```

---

### Task 8: Live validation (bench pedal at 192.168.8.241) + docs

**Files:**
- Create: `docs/HARDWARE-VALIDATION-wifi.md`
- Modify: `README.md` (one sentence), `CLAUDE.md` (architecture + HwCheck lines)

**READ-ONLY CONSTRAINT:** every live command in this task is read-only or session-level (connect/probe/list/browse). **NO writes to the pedal** — no preset saves, no wifi-node writes, nothing guarded.

- [ ] **Step 1: Live checks (controller/agent runs these tonight; results pasted into the doc)**

```bash
dotnet run --project tools/HwCheck -- --wifi --ip 192.168.8.241     # pinned: connect + identify + list presets
dotnet run --project tools/HwCheck -- --wifi                        # mDNS: discover + connect + list
dotnet run --project tools/HwCheck -- --wifi --browse "root\sys\wifi"  # read-only browse over WiFi
```

Expected: all three connect, identify `AMP Station 2.5.1`, list 25/30 presets; the browse dumps the wifi nodes. Record wall-clock connect times from the PERF log lines.

- [ ] **Step 2: Write `docs/HARDWARE-VALIDATION-wifi.md`**

```markdown
# Manual validation — WiFi transport (SP1)

Live results from the overnight bench run are recorded under "Bench (verified)".
Ed's remaining checks are under "App (Ed)".

## Bench (verified <date>, pedal at 192.168.8.241 on "Duke Park Mesh")
- [ ] `HwCheck --wifi --ip 192.168.8.241` → connected, fw identified, presets listed
- [ ] `HwCheck --wifi` (mDNS) → discovered + connected (instance voidx<id>, SRV port 8080)
- [ ] `HwCheck --wifi --browse root\sys\wifi` → wifi nodes dumped read-only
- [ ] Connect times (PERF): pinned ____ ms; mDNS ____ ms

## App (Ed)
- [ ] Pedal on WiFi, USB unplugged → Connect → status ends "(WiFi)"; presets load
- [ ] Preset select + parameter edit over WiFi (audible on pedal)
- [ ] USB plugged in → Connect → status ends "(USB)" (serial wins the order)
- [ ] Pedal off + no USB → Connect → "Disconnected (no device found on USB or WiFi)" after
      the ~3s browse window; no crash
- [ ] Drop test: connected over WiFi, power the pedal off → next operation shows the Error
      status; power on, Connect reconnects
- [ ] (Optional timing) one guarded amp upload over WiFi vs USB: ____ vs ____ — SUPERVISED,
      not part of the overnight run
```

(Fill the Bench checkboxes/timings from Step 1's actual output before committing.)

- [ ] **Step 3: README + CLAUDE.md**

`README.md` — in the Install section (after the VoidX-Control sentence):

```markdown
StompStation Manager connects over USB first and falls back to **WiFi** automatically when the
pedal is on your network (same protocol, auto-discovered via mDNS) — handy when a cable or USB
port lets you down.
```

`CLAUDE.md` — add `src/Sonulab.Transport.Wifi` to the architecture list ("WiFi TCP transport — mDNS discovery + persistent socket on 8080; ITcpConn seam, parser tested on real captured datagrams"), note `--wifi [--ip]` in the HwCheck line, and mention the USB→WiFi fallback in the gotchas ("VoidX must be closed" applies to the COM port; WiFi coexistence observed OK but close VoidX if flaky).

- [ ] **Step 4: Full suite + commit**

Run: `dotnet test --nologo`

```bash
git add docs/HARDWARE-VALIDATION-wifi.md README.md CLAUDE.md
git commit -m "docs: WiFi validation results + README/CLAUDE.md transport notes"
```

---

## Execution notes

- Strict order 1 → 8. Tasks 1-2 are the shared foundation (identical shape to the shelved BLE plan's Tasks 1-2 — when BLE is un-shelved, only its transport tasks remain).
- Task 8's live checks run against the bench pedal Ed left powered at `192.168.8.241` — **read-only/session ops only; zero device writes overnight** (provisioning is SP2, supervised). If the pedal is unreachable (rebooted to a new DHCP lease, mesh moved it), run mDNS discovery to find the new IP; if still nothing, mark the Bench section "pedal unreachable — pending" and commit the docs anyway.
- The app GUI checks in the validation doc are Ed's morning checklist, not the overnight run's.
