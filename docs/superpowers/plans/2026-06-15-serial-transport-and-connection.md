# Serial Transport & Connection Implementation Plan (Plan 2 of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the real USB-serial transport (`SerialSonuLink` over `System.IO.Ports`), port/baud auto-probe, and the firmware compatibility gate (version check + structural preflight) so the app can connect to a StompStation on `COM6` and decide whether writes are safe.

**Architecture:** A thin `ISerialPortStream` adapter isolates `System.IO.Ports` so all framing/read-window logic is unit-testable against a `FakeSerialPort` (no hardware). `SerialSonuLink` implements the Plan‑1 `ISonuLink` (write ASCII + `\0`, read a meter-tolerant time window). `SonuConnector` probes ports×bauds. `CompatibilityChecker` reads `root\sys\_ver/_arch/_license` and asserts the structural slot metadata before allowing writes. `DeviceSession` orchestrates open→probe→identify→compat. The only non-unit-tested piece is the real `SystemSerialPort` adapter, covered by a manual hardware checklist (Task 9).

**Tech Stack:** .NET 10, C#, `System.IO.Ports` (add package), xUnit. Builds on `Sonulab.Core` from Plan 1 (`SonuClient`, `ISonuLink`, `NodeRecord`, `ResponseParser`).

**Spec reference:** design spec §4 (connection), §8 (firmware compatibility & write-gating). `PROTOCOL.md` for framing (commands NUL-terminated; responses CRLF records; meters streamed) and the slot metadata (`count:30`, `chunk:128`, presets `size:8192`/`pst_pst`, amp `12288`/`vxamp`, ir `4096`/`wav_44100`).

---

## Public API defined by this plan

```csharp
namespace Sonulab.Core.Transport;

public interface ISerialPortStream : IDisposable {
    bool IsOpen { get; }
    void Open(string portName, int baudRate);
    void Close();
    void DiscardInBuffer();
    int BytesToRead { get; }
    void Write(byte[] buffer, int offset, int count);
    int Read(byte[] buffer, int offset, int count);
}

public sealed class SystemSerialPort : ISerialPortStream { public SystemSerialPort(); /* wraps SerialPort */ }

public sealed class SerialLinkOptions {
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;   // stop after this much silence following data
    public int MaxWaitMs { get; init; } = 1500;  // hard cap per command
}

public sealed class SerialSonuLink : ISonuLink {
    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate, SerialLinkOptions? options = null);
    public bool IsOpen { get; }
    public Task OpenAsync(CancellationToken ct = default);
    public void Close();
    public Task<string> SendAsync(string command, CancellationToken ct = default);
}

namespace Sonulab.Core.Connection;

public sealed class SonuConnector {
    public SonuConnector(Func<ISerialPortStream> portFactory, SerialLinkOptions? options = null);
    // Returns an opened+verified link, or null if nothing on any port/baud answered `read root\sys\_name`.
    public Task<SerialSonuLink?> ConnectAsync(IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default);
}

namespace Sonulab.Core.Connection;

public enum CompatibilityStatus { Tested, UntestedNewer, Unknown, StructuralMismatch }
public sealed record TestedFirmware(string License, string Arch, string Version);
public sealed record DeviceInfo(string Name, string Id, string Version, string Arch, string License);
public sealed record CompatibilityResult(CompatibilityStatus Status, bool WritesAllowed, string Message, DeviceInfo Device);

public sealed class CompatibilityChecker {
    public CompatibilityChecker(IReadOnlyList<TestedFirmware> tested);
    public Task<CompatibilityResult> CheckAsync(SonuClient client, CancellationToken ct = default);
}

public sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility);
public sealed class DeviceSession {
    public DeviceSession(SonuConnector connector, CompatibilityChecker checker);
    public Task<SessionState> ConnectAsync(IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default);
    public SonuClient? Client { get; }
}
```

Plan‑1 `SonuClient` additions (Task 5):
```csharp
public Task<IReadOnlyList<NodeRecord>> BrowseRecordsAsync(string path, CancellationToken ct = default);
```

## File structure

```
src/Sonulab.Core/
  Sonulab.Core.csproj                 (modify: add System.IO.Ports package)
  Transport/ISerialPortStream.cs      (create)
  Transport/SystemSerialPort.cs       (create)
  Transport/FakeSerialPort.cs         (create — test double, lives in Core so tests + future tools can use it)
  Transport/SerialLinkOptions.cs      (create)
  Transport/SerialSonuLink.cs         (create)
  Transport/FakeSonuLink.cs           (modify: add SeedBrowse, Task 6)
  SonuClient.cs                       (modify: add BrowseRecordsAsync, Task 5)
  Connection/SonuConnector.cs         (create)
  Connection/CompatibilityChecker.cs  (create — also holds the enums/records above)
  Connection/DeviceSession.cs         (create)
tests/Sonulab.Core.Tests/
  FakeSerialPortTests.cs
  SerialSonuLinkTests.cs
  SonuConnectorTests.cs
  BrowseRecordsTests.cs
  CompatibilityCheckerTests.cs
  DeviceSessionTests.cs
docs/
  HARDWARE-VALIDATION-plan2.md        (create — manual checklist, Task 9)
```

---

### Task 1: ISerialPortStream + SystemSerialPort adapter

**Files:** Create `src/Sonulab.Core/Transport/ISerialPortStream.cs`, `src/Sonulab.Core/Transport/SystemSerialPort.cs`; Modify `src/Sonulab.Core/Sonulab.Core.csproj`.

The real adapter cannot be unit-tested without hardware; it is a thin pass-through verified by the Task 9 manual checklist. Keep it trivial so there is nothing to test.

- [ ] **Step 1: Add the System.IO.Ports package**

Run: `dotnet add src/Sonulab.Core package System.IO.Ports`
Expected: package added; `dotnet build` still succeeds.

- [ ] **Step 2: Create the interface**

`src/Sonulab.Core/Transport/ISerialPortStream.cs`:
```csharp
namespace Sonulab.Core.Transport;

public interface ISerialPortStream : IDisposable
{
    bool IsOpen { get; }
    void Open(string portName, int baudRate);
    void Close();
    void DiscardInBuffer();
    int BytesToRead { get; }
    void Write(byte[] buffer, int offset, int count);
    int Read(byte[] buffer, int offset, int count);
}
```

- [ ] **Step 3: Create the real adapter**

`src/Sonulab.Core/Transport/SystemSerialPort.cs`:
```csharp
using System.IO.Ports;

namespace Sonulab.Core.Transport;

public sealed class SystemSerialPort : ISerialPortStream
{
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen ?? false;

    public void Open(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
        _port.Open();
    }

    public void Close() => _port?.Close();
    public void DiscardInBuffer() { if (_port?.IsOpen == true) _port.DiscardInBuffer(); }
    public int BytesToRead => _port?.IsOpen == true ? _port.BytesToRead : 0;
    public void Write(byte[] buffer, int offset, int count) => _port!.Write(buffer, offset, count);
    public int Read(byte[] buffer, int offset, int count) => _port!.Read(buffer, offset, count);
    public void Dispose() { _port?.Dispose(); _port = null; }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded.` 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ISerialPortStream + SystemSerialPort adapter"
```

---

### Task 2: FakeSerialPort — framing-aware test double

**Files:** Create `src/Sonulab.Core/Transport/FakeSerialPort.cs`; Test `tests/Sonulab.Core.Tests/FakeSerialPortTests.cs`.

It assembles each command from the bytes written up to the `\0` terminator, then enqueues a response produced by a `Responder` delegate — exactly mirroring how the device answers. This makes `SerialSonuLink` tests deterministic.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/FakeSerialPortTests.cs`:
```csharp
using System.Text;
using Sonulab.Core.Transport;
using Xunit;

public class FakeSerialPortTests
{
    [Fact] public void Captures_command_up_to_nul_and_enqueues_response()
    {
        var p = new FakeSerialPort { Responder = cmd => cmd == "read x" ? "x:{\"value\":1}\r\n" : "" };
        p.Open("COM9", 115200);
        var bytes = Encoding.ASCII.GetBytes("read x");
        p.Write(bytes, 0, bytes.Length);
        p.Write(new byte[] { 0 }, 0, 1);                 // terminator triggers the response
        Assert.Equal("read x", p.LastCommand);
        Assert.True(p.BytesToRead > 0);
        var buf = new byte[p.BytesToRead];
        int n = p.Read(buf, 0, buf.Length);
        Assert.Equal("x:{\"value\":1}\r\n", Encoding.ASCII.GetString(buf, 0, n));
    }

    [Fact] public void DiscardInBuffer_clears_pending_input()
    {
        var p = new FakeSerialPort();
        p.Open("COM9", 115200);
        p.EnqueueResponse("junk");
        p.DiscardInBuffer();
        Assert.Equal(0, p.BytesToRead);
    }

    [Fact] public void Open_records_port_and_baud()
    {
        var p = new FakeSerialPort();
        p.Open("COM6", 115200);
        Assert.True(p.IsOpen);
        Assert.Equal("COM6", p.OpenedPort);
        Assert.Equal(115200, p.OpenedBaud);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FakeSerialPortTests`
Expected: FAIL — `FakeSerialPort` does not exist.

- [ ] **Step 3: Implement FakeSerialPort**

`src/Sonulab.Core/Transport/FakeSerialPort.cs`:
```csharp
using System.Text;

namespace Sonulab.Core.Transport;

public sealed class FakeSerialPort : ISerialPortStream
{
    private readonly Queue<byte> _in = new();
    private readonly List<byte> _cmdBuf = new();

    public bool IsOpen { get; private set; }
    public string? OpenedPort { get; private set; }
    public int OpenedBaud { get; private set; }
    public string? LastCommand { get; private set; }

    /// <summary>Maps a fully-received command (NUL stripped) to the response text to enqueue. Return "" for no response.</summary>
    public Func<string, string>? Responder { get; set; }

    public void Open(string portName, int baudRate) { IsOpen = true; OpenedPort = portName; OpenedBaud = baudRate; }
    public void Close() => IsOpen = false;
    public void DiscardInBuffer() => _in.Clear();
    public int BytesToRead => _in.Count;

    public void EnqueueResponse(string text)
    {
        foreach (var b in Encoding.ASCII.GetBytes(text)) _in.Enqueue(b);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = buffer[offset + i];
            if (b == 0)
            {
                LastCommand = Encoding.ASCII.GetString(_cmdBuf.ToArray());
                _cmdBuf.Clear();
                var resp = Responder?.Invoke(LastCommand);
                if (!string.IsNullOrEmpty(resp)) EnqueueResponse(resp);
            }
            else _cmdBuf.Add(b);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int i = 0;
        while (i < count && _in.Count > 0) buffer[offset + i++] = _in.Dequeue();
        return i;
    }

    public void Dispose() { }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FakeSerialPortTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): FakeSerialPort framing-aware test double"
```

---

### Task 3: SerialSonuLink — framing + meter-tolerant read window

**Files:** Create `src/Sonulab.Core/Transport/SerialLinkOptions.cs`, `src/Sonulab.Core/Transport/SerialSonuLink.cs`; Test `tests/Sonulab.Core.Tests/SerialSonuLinkTests.cs`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/SerialSonuLinkTests.cs`:
```csharp
using System.Text;
using Sonulab.Core.Transport;
using Xunit;

public class SerialSonuLinkTests
{
    static SerialLinkOptions Fast => new() { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 500 };

    [Fact] public async Task SendAsync_appends_nul_and_returns_response()
    {
        var port = new FakeSerialPort { Responder = c => c == @"read root\sys\_name" ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "" };
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.Equal(@"read root\sys\_name", port.LastCommand);   // proves NUL framing assembled the command
        Assert.Contains("\"value\":\"AMP Station\"", resp);
    }

    [Fact] public async Task OpenAsync_opens_underlying_port_with_baud()
    {
        var port = new FakeSerialPort();
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        Assert.True(link.IsOpen);
        Assert.Equal("COM6", port.OpenedPort);
        Assert.Equal(115200, port.OpenedBaud);
    }

    [Fact] public async Task SendAsync_returns_empty_when_no_response()
    {
        var port = new FakeSerialPort { Responder = _ => "" };  // device sends nothing (e.g. a write)
        var link = new SerialSonuLink(port, "COM6", 115200, Fast);
        await link.OpenAsync();
        Assert.Equal("", await link.SendAsync(@"write root\app\amp\on_off:{""value"":""OFF""}"));
    }

    [Fact] public async Task SendAsync_throws_if_not_open()
    {
        var link = new SerialSonuLink(new FakeSerialPort(), "COM6", 115200, Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => link.SendAsync("read x"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SerialSonuLinkTests`
Expected: FAIL — `SerialSonuLink`/`SerialLinkOptions` do not exist.

- [ ] **Step 3: Implement SerialLinkOptions**

`src/Sonulab.Core/Transport/SerialLinkOptions.cs`:
```csharp
namespace Sonulab.Core.Transport;

public sealed class SerialLinkOptions
{
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 1500;
}
```

- [ ] **Step 4: Implement SerialSonuLink**

`src/Sonulab.Core/Transport/SerialSonuLink.cs`:
```csharp
using System.Diagnostics;
using System.Text;

namespace Sonulab.Core.Transport;

public sealed class SerialSonuLink : ISonuLink
{
    private static readonly byte[] Nul = { 0 };
    private readonly ISerialPortStream _port;
    private readonly string _portName;
    private readonly int _baud;
    private readonly SerialLinkOptions _options;

    public SerialSonuLink(ISerialPortStream port, string portName, int baudRate, SerialLinkOptions? options = null)
    {
        _port = port; _portName = portName; _baud = baudRate; _options = options ?? new SerialLinkOptions();
    }

    public bool IsOpen => _port.IsOpen;

    public Task OpenAsync(CancellationToken ct = default)
    {
        _port.Open(_portName, _baud);
        return Task.CompletedTask;
    }

    public void Close() => _port.Close();

    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!_port.IsOpen) throw new InvalidOperationException("Serial link is not open.");
        _port.DiscardInBuffer();
        var bytes = Encoding.ASCII.GetBytes(command);
        _port.Write(bytes, 0, bytes.Length);
        _port.Write(Nul, 0, 1);

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        long lastData = 0;
        bool sawData = false;

        while (sw.ElapsedMilliseconds < _options.MaxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            int avail = _port.BytesToRead;
            if (avail > 0)
            {
                var buf = new byte[avail];
                int n = _port.Read(buf, 0, avail);
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                sawData = true;
                lastData = sw.ElapsedMilliseconds;
            }
            else
            {
                if (sawData && sw.ElapsedMilliseconds - lastData >= _options.IdleGapMs) break;
                await Task.Delay(_options.PollMs, ct);
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter SerialSonuLinkTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): SerialSonuLink framing + meter-tolerant read window"
```

---

### Task 4: SonuConnector — port/baud auto-probe

**Files:** Create `src/Sonulab.Core/Connection/SonuConnector.cs`; Test `tests/Sonulab.Core.Tests/SonuConnectorTests.cs`.

Probes each (port, baud) by opening a link and sending `read root\sys\_name`; the first that returns a well-formed `root\sys\_name` record wins. The port factory is injected so tests use `FakeSerialPort`.

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/SonuConnectorTests.cs`:
```csharp
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class SonuConnectorTests
{
    static SerialLinkOptions Fast => new() { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 300 };

    // A fake that only answers the name query when opened at the "correct" baud.
    static FakeSerialPort MakePort(int answersAtBaud)
    {
        var p = new FakeSerialPort();
        p.Responder = cmd =>
            (cmd == @"read root\sys\_name" && p.OpenedBaud == answersAtBaud)
                ? "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n" : "";
        return p;
    }

    [Fact] public async Task Connects_on_matching_baud()
    {
        var connector = new SonuConnector(() => MakePort(115200), Fast);
        var link = await connector.ConnectAsync(new[] { "COM6" }, new[] { 921600, 115200 });
        Assert.NotNull(link);
        Assert.True(link!.IsOpen);
    }

    [Fact] public async Task Returns_null_when_nothing_answers()
    {
        var connector = new SonuConnector(() => MakePort(115200), Fast);
        var link = await connector.ConnectAsync(new[] { "COM4", "COM5" }, new[] { 9600 });
        Assert.Null(link);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SonuConnectorTests`
Expected: FAIL — `SonuConnector` does not exist.

- [ ] **Step 3: Implement SonuConnector**

`src/Sonulab.Core/Connection/SonuConnector.cs`:
```csharp
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

public sealed class SonuConnector
{
    private readonly Func<ISerialPortStream> _portFactory;
    private readonly SerialLinkOptions? _options;

    public SonuConnector(Func<ISerialPortStream> portFactory, SerialLinkOptions? options = null)
    {
        _portFactory = portFactory; _options = options;
    }

    public async Task<SerialSonuLink?> ConnectAsync(
        IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default)
    {
        foreach (var port in ports)
        foreach (var baud in bauds)
        {
            ct.ThrowIfCancellationRequested();
            var link = new SerialSonuLink(_portFactory(), port, baud, _options);
            try
            {
                await link.OpenAsync(ct);
                var resp = await link.SendAsync(@"read root\sys\_name", ct);
                bool ok = ResponseParser.NonMeterRecords(resp)
                    .Any(r => r.StartsWith(@"root\sys\_name:{", StringComparison.Ordinal));
                if (ok) return link;
                link.Close();
            }
            catch
            {
                try { link.Close(); } catch { /* port busy/denied — try next */ }
            }
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SonuConnectorTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): SonuConnector port/baud auto-probe"
```

---

### Task 5: SonuClient.BrowseRecordsAsync (raw records for metadata)

**Files:** Modify `src/Sonulab.Core/SonuClient.cs`; Test `tests/Sonulab.Core.Tests/BrowseRecordsTests.cs`.

`BrowseAsync` (Plan 1) returns `NodeSchema`, which drops list metadata (`count`/`chunk`/`size`/`item_type`) needed by the compatibility preflight. Add `BrowseRecordsAsync` returning the raw `NodeRecord`s, and refactor `BrowseAsync` to build on it (DRY).

- [ ] **Step 1: Write the failing test**

`tests/Sonulab.Core.Tests/BrowseRecordsTests.cs`:
```csharp
using System.Text.Json;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class BrowseRecordsTests
{
    [Fact] public async Task BrowseRecordsAsync_exposes_list_metadata()
    {
        var link = new FakeSonuLink();
        link.SeedBrowse(@"root\presets",
            "root\\presets:{\"value\":[\"A\"],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}");
        await link.OpenAsync();
        var client = new SonuClient(link);

        var recs = await client.BrowseRecordsAsync(@"root\presets");
        var rec = Assert.Single(recs);
        Assert.Equal(@"root\presets", rec.Path);
        Assert.Equal(30, rec.Json.GetProperty("count").GetInt32());
        Assert.Equal(128, rec.Json.GetProperty("chunk").GetInt32());
        Assert.Equal("pst_pst", rec.Json.GetProperty("item_type").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter BrowseRecordsTests`
Expected: FAIL — `FakeSonuLink.SeedBrowse` and `BrowseRecordsAsync` do not exist. (SeedBrowse is added in Task 6; this test also fails to compile until then — implement Task 6 first if doing strict TDD, or add SeedBrowse now. The recommended order is Task 6 then re-run; see note.)

> **Order note:** Tasks 5 and 6 are mutually dependent for their tests. Implement the `BrowseRecordsAsync` production code (Step 3 below) AND `FakeSonuLink.SeedBrowse` (Task 6 Step 3) before running either test. Commit them separately as written.

- [ ] **Step 3: Implement BrowseRecordsAsync and refactor BrowseAsync**

In `src/Sonulab.Core/SonuClient.cs`, replace the existing `BrowseAsync` method with these two methods:
```csharp
    public async Task<IReadOnlyList<NodeRecord>> BrowseRecordsAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Browse(path), ct);
        var list = new List<NodeRecord>();
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r))
                list.Add(r);
        return list;
    }

    public async Task<IReadOnlyList<NodeSchema>> BrowseAsync(string path, CancellationToken ct = default) =>
        (await BrowseRecordsAsync(path, ct)).Select(NodeSchema.FromRecord).ToList();
```

- [ ] **Step 4: Run test to verify it passes** (after Task 6 Step 3 is also in place)

Run: `dotnet test --filter BrowseRecordsTests`
Expected: PASS (1 test). Also run `dotnet test --filter SonuClientTests` to confirm the `BrowseAsync` refactor didn't regress.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Core/SonuClient.cs tests/Sonulab.Core.Tests/BrowseRecordsTests.cs
git commit -m "feat(core): SonuClient.BrowseRecordsAsync exposes raw node records"
```

---

### Task 6: FakeSonuLink.SeedBrowse (browse-with-metadata test double)

**Files:** Modify `src/Sonulab.Core/Transport/FakeSonuLink.cs`; Test `tests/Sonulab.Core.Tests/` (covered by `BrowseRecordsTests` and `CompatibilityCheckerTests`).

- [ ] **Step 1: (test already written in Task 5 / Task 7)** — no new test file; `BrowseRecordsTests` exercises this.

- [ ] **Step 2: Implement SeedBrowse**

In `src/Sonulab.Core/Transport/FakeSonuLink.cs`, add a browse store and handler. Add this field near the other dictionaries:
```csharp
    private readonly Dictionary<string, string> _browse = new(); // path -> full CRLF response text
```
Add this public method:
```csharp
    public void SeedBrowse(string path, params string[] records) =>
        _browse[path] = string.Join("\r\n", records) + "\r\n";
```
In `SendAsync`, add a `browse` branch BEFORE the `Read` branch (so `read` doesn't shadow it). Add this regex field with the others:
```csharp
    private static readonly Regex BrowseRx = new(@"^browse (.+)$");
```
And in the `SendAsync` body, before the `Read.Match` check (now `ReadRx` after Plan 1 review rename), add:
```csharp
        if ((m = BrowseRx.Match(command)).Success)
            return Task.FromResult(_browse.TryGetValue(m.Groups[1].Value, out var b) ? b : "");
```

- [ ] **Step 3: Run the dependent tests to verify they pass**

Run: `dotnet test --filter BrowseRecordsTests`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Sonulab.Core/Transport/FakeSonuLink.cs
git commit -m "test(core): FakeSonuLink.SeedBrowse for browse-with-metadata"
```

---

### Task 7: CompatibilityChecker — version gate + structural preflight

**Files:** Create `src/Sonulab.Core/Connection/CompatibilityChecker.cs` (holds the enums/records too); Test `tests/Sonulab.Core.Tests/CompatibilityCheckerTests.cs`.

Rules:
- Reads `root\sys\_name/_id/_ver/_arch/_license` into `DeviceInfo`.
- Structural preflight: `browse root\presets`, `root\amp`, `root\ir`; each must report `count==30`, `chunk==128`, and the expected `size`+`item_type`. Any miss ⇒ `StructuralMismatch`, `WritesAllowed=false`.
- Version gate (only if structure is OK): if `(license,arch,version)` is in the tested list ⇒ `Tested`, writes allowed. Else ⇒ `Unknown`, writes NOT allowed (user may override in the UI later; the checker just reports).

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/CompatibilityCheckerTests.cs`:
```csharp
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class CompatibilityCheckerTests
{
    static FakeSonuLink Seed(string ver = "2.5.1", int count = 30, int chunk = 128)
    {
        var link = new FakeSonuLink();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        link.SeedScalar(@"root\sys\_id", "\"abc123\"");
        link.SeedScalar(@"root\sys\_ver", $"\"{ver}\"");
        link.SeedScalar(@"root\sys\_arch", "\"ESP32S3\"");
        link.SeedScalar(@"root\sys\_license", "\"stompstation1\"");
        link.SeedBrowse(@"root\presets", $"root\\presets:{{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":{count},\"chunk\":{chunk},\"item_type\":\"pst_pst\"}}");
        link.SeedBrowse(@"root\amp",     "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}");
        link.SeedBrowse(@"root\ir",      "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}");
        return link;
    }

    static CompatibilityChecker Checker() =>
        new(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });

    [Fact] public async Task Tested_firmware_allows_writes()
    {
        var link = Seed(); await link.OpenAsync();
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.Tested, r.Status);
        Assert.True(r.WritesAllowed);
        Assert.Equal("2.5.1", r.Device.Version);
    }

    [Fact] public async Task Unknown_version_blocks_writes()
    {
        var link = Seed(ver: "2.6.0"); await link.OpenAsync();
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.Unknown, r.Status);
        Assert.False(r.WritesAllowed);
    }

    [Fact] public async Task Structural_mismatch_blocks_writes_even_if_version_tested()
    {
        var link = Seed(count: 16); await link.OpenAsync();   // wrong slot count
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.StructuralMismatch, r.Status);
        Assert.False(r.WritesAllowed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CompatibilityCheckerTests`
Expected: FAIL — `CompatibilityChecker` and friends do not exist.

- [ ] **Step 3: Implement CompatibilityChecker (+ enums/records)**

`src/Sonulab.Core/Connection/CompatibilityChecker.cs`:
```csharp
using Sonulab.Core.Model;

namespace Sonulab.Core.Connection;

public enum CompatibilityStatus { Tested, UntestedNewer, Unknown, StructuralMismatch }

public sealed record TestedFirmware(string License, string Arch, string Version);
public sealed record DeviceInfo(string Name, string Id, string Version, string Arch, string License);
public sealed record CompatibilityResult(CompatibilityStatus Status, bool WritesAllowed, string Message, DeviceInfo Device);

public sealed class CompatibilityChecker
{
    // (path, expected size, expected item_type)
    private static readonly (string Path, int Size, string ItemType)[] Lists =
    {
        (@"root\presets", 8192, "pst_pst"),
        (@"root\amp", 12288, "vxamp"),
        (@"root\ir", 4096, "wav_44100"),
    };

    private readonly IReadOnlyList<TestedFirmware> _tested;
    public CompatibilityChecker(IReadOnlyList<TestedFirmware> tested) => _tested = tested;

    public async Task<CompatibilityResult> CheckAsync(SonuClient client, CancellationToken ct = default)
    {
        var device = new DeviceInfo(
            Name: await client.ReadValueAsync(@"root\sys\_name", ct) ?? "",
            Id: await client.ReadValueAsync(@"root\sys\_id", ct) ?? "",
            Version: await client.ReadValueAsync(@"root\sys\_ver", ct) ?? "",
            Arch: await client.ReadValueAsync(@"root\sys\_arch", ct) ?? "",
            License: await client.ReadValueAsync(@"root\sys\_license", ct) ?? "");

        // Structural preflight (version-independent).
        var browse = await client.BrowseRecordsAsync("root", ct);
        var byPath = new Dictionary<string, NodeRecord>();
        foreach (var rec in browse) byPath[rec.Path] = rec;
        // Also accept per-node browse for fakes that seed list nodes directly.
        foreach (var (path, _, _) in Lists)
            if (!byPath.ContainsKey(path))
                foreach (var rec in await client.BrowseRecordsAsync(path, ct))
                    byPath[rec.Path] = rec;

        foreach (var (path, size, itemType) in Lists)
        {
            if (!byPath.TryGetValue(path, out var rec))
                return Mismatch(device, $"List node {path} not found.");
            int? GetInt(string n) => rec.Json.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : null;
            string? GetStr(string n) => rec.Json.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            if (GetInt("count") != 30 || GetInt("chunk") != 128 || GetInt("size") != size || GetStr("item_type") != itemType)
                return Mismatch(device, $"Structural mismatch at {path} (count/chunk/size/item_type).");
        }

        // Version gate.
        bool tested = _tested.Any(t =>
            t.License == device.License && t.Arch == device.Arch && t.Version == device.Version);
        return tested
            ? new CompatibilityResult(CompatibilityStatus.Tested, true,
                $"Firmware {device.Version} is tested.", device)
            : new CompatibilityResult(CompatibilityStatus.Unknown, false,
                $"Firmware {device.Version} has not been tested; writes disabled.", device);
    }

    private static CompatibilityResult Mismatch(DeviceInfo d, string msg) =>
        new(CompatibilityStatus.StructuralMismatch, false, msg, d);
}
```

> Note: `CheckAsync` first tries `browse root` (the real device returns the whole tree, which includes the list nodes), then falls back to per-node `browse root\presets` etc. (which the test fake seeds). Both paths populate `byPath`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CompatibilityCheckerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): CompatibilityChecker (version gate + structural preflight)"
```

---

### Task 8: DeviceSession — connect → identify → compatibility

**Files:** Create `src/Sonulab.Core/Connection/DeviceSession.cs`; Test `tests/Sonulab.Core.Tests/DeviceSessionTests.cs`.

- [ ] **Step 1: Write the failing test**

`tests/Sonulab.Core.Tests/DeviceSessionTests.cs`:
```csharp
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class DeviceSessionTests
{
    static SerialLinkOptions Fast => new() { PollMs = 2, IdleGapMs = 15, MaxWaitMs = 300 };

    // One fake that answers identity + browse on the right baud.
    static FakeSerialPort MakeDevice()
    {
        var p = new FakeSerialPort();
        p.Responder = cmd =>
        {
            if (p.OpenedBaud != 115200) return "";
            return cmd switch
            {
                @"read root\sys\_name"    => "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n",
                @"read root\sys\_id"      => "root\\sys\\_id:{\"value\":\"abc\"}\r\n",
                @"read root\sys\_ver"     => "root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n",
                @"read root\sys\_arch"    => "root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n",
                @"read root\sys\_license" => "root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n",
                @"browse root"            =>
                    "root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n" +
                    "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n" +
                    "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n",
                _ => "",
            };
        };
        return p;
    }

    [Fact] public async Task Connects_identifies_and_reports_tested()
    {
        var connector = new SonuConnector(MakeDevice, Fast);
        var checker = new CompatibilityChecker(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });
        var session = new DeviceSession(connector, checker);

        var state = await session.ConnectAsync(new[] { "COM6" }, new[] { 115200 });

        Assert.True(state.Connected);
        Assert.Equal("AMP Station", state.Device!.Name);
        Assert.Equal(CompatibilityStatus.Tested, state.Compatibility!.Status);
        Assert.True(state.Compatibility.WritesAllowed);
        Assert.NotNull(session.Client);
    }

    [Fact] public async Task Reports_disconnected_when_no_device()
    {
        var connector = new SonuConnector(() => new FakeSerialPort(), Fast); // never answers
        var checker = new CompatibilityChecker(System.Array.Empty<TestedFirmware>());
        var session = new DeviceSession(connector, checker);

        var state = await session.ConnectAsync(new[] { "COM6" }, new[] { 115200 });

        Assert.False(state.Connected);
        Assert.Null(state.Device);
        Assert.Null(session.Client);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DeviceSessionTests`
Expected: FAIL — `DeviceSession`/`SessionState` do not exist.

- [ ] **Step 3: Implement DeviceSession**

`src/Sonulab.Core/Connection/DeviceSession.cs`:
```csharp
namespace Sonulab.Core.Connection;

public sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility);

public sealed class DeviceSession
{
    private readonly SonuConnector _connector;
    private readonly CompatibilityChecker _checker;

    public DeviceSession(SonuConnector connector, CompatibilityChecker checker)
    {
        _connector = connector; _checker = checker;
    }

    public SonuClient? Client { get; private set; }

    public async Task<SessionState> ConnectAsync(
        IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default)
    {
        var link = await _connector.ConnectAsync(ports, bauds, ct);
        if (link is null) { Client = null; return new SessionState(false, null, null); }

        Client = new SonuClient(link);
        var compat = await _checker.CheckAsync(Client, ct);
        return new SessionState(true, compat.Device, compat);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter DeviceSessionTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — all Plan 1 + Plan 2 tests green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): DeviceSession connect/identify/compatibility orchestrator"
```

---

### Task 9: Manual hardware validation (real COM6)

**Files:** Create `docs/HARDWARE-VALIDATION-plan2.md`. No code; this is the integration gate for `SystemSerialPort`, which cannot be unit-tested.

- [ ] **Step 1: Write the checklist document**

Create `docs/HARDWARE-VALIDATION-plan2.md` with:
```markdown
# Plan 2 — Manual Hardware Validation

Run with the pedal on USB and **VoidX-Control CLOSED** (it holds COM6 open).

A tiny console harness (or `dotnet run` from a scratch program / a unit-test marked
`[Trait("Category","Hardware")]` and run explicitly) should, using the REAL `SystemSerialPort`:

1. **Connect:** `SonuConnector(() => new SystemSerialPort())` over ports `["COM6"]` (or all
   `SerialPort.GetPortNames()`), bauds `[115200]`. Expect a non-null link.
2. **Identify:** `DeviceSession.ConnectAsync` returns `Connected=true`, `Device.Name == "AMP Station"`,
   `Device.Version` populated (e.g. "2.5.1").
3. **Compatibility:** with `2.5.1/ESP32S3/stompstation1` in the tested list, `Status == Tested`,
   `WritesAllowed == true`.
4. **Read-only sanity:** `client.ReadListAsync(@"root\presets")` returns 30 names matching what
   `tools/probe.ps1` / `docs/probe-output.txt` showed.
5. **Meter tolerance:** repeat the reads a few times; confirm responses parse correctly despite the
   continuous meter stream (no exceptions, correct values).

Record pass/fail and the observed device version here. NO writes in this task.
```

- [ ] **Step 2: Commit**

```bash
git add docs/HARDWARE-VALIDATION-plan2.md
git commit -m "docs: Plan 2 manual hardware validation checklist"
```

- [ ] **Step 3: (Operator) run the checklist** with VoidX closed and the pedal connected, and record results. If any step fails, capture the exact behavior and report before starting Plan 3.

---

## Self-review notes
- **Spec coverage:** §4 connection → `SonuConnector` + `DeviceSession` (Tasks 4, 8) + baud auto-probe; §8 firmware gate + structural preflight → `CompatibilityChecker` (Task 7). Real transport → `SystemSerialPort` (Task 1) validated by Task 9. Meter-tolerant framing → `SerialSonuLink` (Task 3).
- **Placeholder scan:** none — every code step is complete. The one cross-task dependency (Tasks 5↔6 test compilation) is called out explicitly with an implementation order note.
- **Type consistency:** signatures match the "Public API" block. `BrowseRecordsAsync` (Task 5) is consumed by `CompatibilityChecker` (Task 7). `SerialLinkOptions` flows through `SerialSonuLink`/`SonuConnector`. `FakeSonuLink.SeedBrowse` (Task 6) is used by Tasks 5 and 7 tests. `FakeSonuLink`'s post-Plan-1-review regex field names (`ReadRx`/`WriteRx`/`DReadRx`/`DWriteRx`) are referenced when adding `BrowseRx` in Task 6.
- **Hardware caveat:** `SystemSerialPort` and real read-window timing are intentionally not unit-tested (no hardware in CI); Task 9 is their gate. All logic around them IS unit-tested via `FakeSerialPort`.
```
