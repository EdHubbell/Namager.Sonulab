# Usage Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send one anonymous ping per install per UTC day when a pedal successfully connects, record it in Cloudflare D1, so we can tell whether NAMager has returning users before building more features.

**Architecture:** A local state file (`%APPDATA%\Namager\usage.json`) holds a random install GUID and the last ping date. `UsagePingService` POSTs four fields to a new `/ping` route on the existing Cloudflare Worker, which upserts into D1. The ping fires from `ConnectionViewModel` on the first successful connect of an app run, is gated to once per UTC day, and never throws or blocks the UI.

**Tech Stack:** C# / .NET 10, Avalonia MVVM, xUnit, `System.Text.Json`, `HttpClient`; Cloudflare Workers (plain JS, no framework) + D1 (SQLite), deployed with `wrangler`.

**Spec:** `docs/superpowers/specs/2026-07-23-usage-telemetry-design.md`

## Global Constraints

- **The payload is exactly four fields** — `installId`, `appVersion`, `fw`, `transport`. Adding any field is a spec change, not an implementation detail.
- **Never throws, never blocks.** Every failure mode in the client path (offline, DNS, 4xx, 5xx, malformed, cancelled, unwritable disk) is a silent no-op. Match `UpdateCheckService`'s existing contract exactly.
- **Dev builds never ping.** `AppInfo.Version` containing `-` (local builds are `1.0.0-dev`) skips the ping entirely.
- **`/` on the worker stays byte-for-byte as it is.** Installed copies POST feedback to the bare URL; changing that route breaks them.
- **No IP is ever written to D1.** `cf-connecting-ip` is used only for the in-memory rate limit.
- Existing tests must keep passing: `dotnet test` is 490 tests today and every number in this plan assumes that baseline.
- Follow existing file conventions: services in `src/Namager.App/Services/`, tests in `tests/Namager.App.Tests/<Name>Tests.cs`, XML doc comments on public types explaining *why*, not *what*.

**Task order vs spec §9.** The spec requires the worker to be live *before the app change ships*. Tasks 1–3 build the client first because it is pure TDD with no external dependency, and Task 4 deploys the worker before Task 5's end-to-end check — so the constraint holds: nothing reaches a user until a release is tagged, which happens after all five tasks. Do not tag a release between Task 3 and Task 4.

## File Structure

| File | Responsibility |
|---|---|
| `src/Namager.App/Services/UsageState.cs` (create) | The `usage.json` record: load/save, mint install GUID, pure day-gate logic. No HTTP. |
| `src/Namager.App/Services/UsagePingService.cs` (create) | `IUsagePingService` + implementation: build the payload, POST it, update the state on success. No UI. |
| `src/Namager.App/ViewModels/ConnectionViewModel.cs` (modify) | Fires the ping once per run after a successful connect. |
| `src/Namager.App/ViewModels/MainWindowViewModel.cs` (modify) | Constructs the real `UsagePingService` and hands it to `ConnectionViewModel`. |
| `tests/Namager.App.Tests/UsageStateTests.cs` (create) | State file round-trip, first run, corruption, day gate. |
| `tests/Namager.App.Tests/UsagePingServiceTests.cs` (create) | Payload shape, never-throws, dev-build skip, transport normalization, state updates. |
| `tests/Namager.App.Tests/ConnectionViewModelTests.cs` (modify) | Ping fires once on success, never on failure. |
| `infra/feedback-worker/worker.js` (modify) | Add a path router + `/ping` handler. `/` handler untouched. |
| `infra/feedback-worker/schema.sql` (create) | D1 tables. |
| `infra/feedback-worker/wrangler.toml` (modify) | D1 binding. |
| `infra/feedback-worker/README.md` (modify) | D1 setup steps + the reporting queries. |
| `docs/VALIDATION-usage-telemetry.md` (create) | Manual `curl` checklist for the worker (no JS test toolchain exists). |
| `PRIVACY.md` (create) | The four fields, verbatim. |
| `README.md` (modify) | Short paragraph linking to `PRIVACY.md`. |

---

### Task 1: `UsageState` — install ID and day gate

The file-backed state, with all logic pure enough to test without a socket. Nothing here knows about HTTP.

**Files:**
- Create: `src/Namager.App/Services/UsageState.cs`
- Test: `tests/Namager.App.Tests/UsageStateTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `sealed record UsageState(string InstallId, string? LastPingUtc)`
  - `static UsageState Load(string? path = null)` — never throws; mints a new GUID when the file is missing/corrupt.
  - `static string DefaultPath { get; }` — `%APPDATA%\Namager\usage.json`
  - `bool ShouldPing(DateOnly todayUtc)` — pure.
  - `void Save(string? path = null)` — never throws.

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.App.Tests/UsageStateTests.cs`:

```csharp
using Namager.App.Services;
using Xunit;

public class UsageStateTests
{
    // Each test gets its own throwaway file path; nothing touches the real %APPDATA%.
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"usage-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_on_first_run_mints_a_guid()
    {
        var state = UsageState.Load(TempPath());
        Assert.True(Guid.TryParse(state.InstallId, out _));
        Assert.Null(state.LastPingUtc);
    }

    [Fact]
    public void InstallId_is_stable_across_save_and_reload()
    {
        var path = TempPath();
        var first = UsageState.Load(path);
        first.Save(path);
        var second = UsageState.Load(path);
        Assert.Equal(first.InstallId, second.InstallId);
    }

    [Fact]
    public void LastPingUtc_round_trips()
    {
        var path = TempPath();
        var state = UsageState.Load(path) with { LastPingUtc = "2026-07-23" };
        state.Save(path);
        Assert.Equal("2026-07-23", UsageState.Load(path).LastPingUtc);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"installId\":\"\"}")]
    [InlineData("{\"installId\":\"not-a-guid\"}")]
    public void Load_treats_corrupt_file_as_first_run(string contents)
    {
        var path = TempPath();
        File.WriteAllText(path, contents);
        var state = UsageState.Load(path);
        Assert.True(Guid.TryParse(state.InstallId, out _));   // must not throw
    }

    [Fact]
    public void ShouldPing_is_false_on_the_same_day_and_true_on_a_new_one()
    {
        var state = new UsageState(Guid.NewGuid().ToString(), "2026-07-23");
        Assert.False(state.ShouldPing(new DateOnly(2026, 7, 23)));
        Assert.True(state.ShouldPing(new DateOnly(2026, 7, 24)));
    }

    [Fact]
    public void ShouldPing_is_true_when_never_pinged()
        => Assert.True(new UsageState(Guid.NewGuid().ToString(), null)
                       .ShouldPing(new DateOnly(2026, 7, 23)));

    [Fact]
    public void Save_to_an_unwritable_path_does_not_throw()
    {
        // A path whose parent is a file, not a directory - guaranteed to fail.
        var file = TempPath();
        File.WriteAllText(file, "x");
        var state = UsageState.Load(TempPath());
        state.Save(Path.Combine(file, "usage.json"));   // must not throw
    }

    [Fact]
    public void DefaultPath_sits_next_to_the_tone3000_config()
    {
        Assert.EndsWith(Path.Combine("Namager", "usage.json"), UsageState.DefaultPath);
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~UsageStateTests`
Expected: FAIL — compile error, `The type or namespace name 'UsageState' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Namager.App/Services/UsageState.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namager.App.Services;

/// <summary>Local state behind the anonymous usage ping: a random install ID and the last day
/// we pinged. Lives next to tone3000.json. Deleting the file resets the install identity —
/// that is the user's documented escape hatch (PRIVACY.md).
/// Every failure mode (missing, corrupt, unreadable, unwritable) degrades to "first run"
/// rather than throwing: telemetry must never be able to break the app.</summary>
public sealed record UsageState(
    [property: JsonPropertyName("installId")] string InstallId,
    [property: JsonPropertyName("lastPingUtc")] string? LastPingUtc)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Namager", "usage.json");

    /// <summary>Never throws. A missing or unusable file yields a fresh install ID, which is
    /// only persisted once <see cref="Save"/> is called after a successful ping.</summary>
    public static UsageState Load(string? path = null)
    {
        try
        {
            var state = JsonSerializer.Deserialize<UsageState>(File.ReadAllText(path ?? DefaultPath));
            if (state is not null && Guid.TryParse(state.InstallId, out _)) return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or NotSupportedException)
        { /* fall through to a fresh identity */ }

        return new UsageState(Guid.NewGuid().ToString(), null);
    }

    /// <summary>True when we have not yet pinged on this UTC day. The date is passed in so the
    /// gate is testable without a clock.</summary>
    public bool ShouldPing(DateOnly todayUtc)
        => LastPingUtc != todayUtc.ToString("yyyy-MM-dd");

    /// <summary>Never throws. A failed write just means the day gate doesn't stick — the app
    /// pings again next launch, which is harmless (the worker de-duplicates by day anyway).</summary>
    public void Save(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        { /* best effort */ }
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~UsageStateTests`
Expected: PASS — 12 tests (the corrupt-file `[Theory]` contributes 5).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/UsageState.cs tests/Namager.App.Tests/UsageStateTests.cs
git commit -m "feat: UsageState - install ID and once-per-day gate for the usage ping"
```

---

### Task 2: `UsagePingService` — build and send the payload

**Files:**
- Create: `src/Namager.App/Services/UsagePingService.cs`
- Test: `tests/Namager.App.Tests/UsagePingServiceTests.cs`

**Interfaces:**
- Consumes: `UsageState.Load/Save/ShouldPing` from Task 1.
- Produces:
  - `interface IUsagePingService { Task PingAsync(string firmware, string? transport, CancellationToken ct = default); }`
  - `sealed class UsagePingService : IUsagePingService` with ctor `(HttpMessageHandler? handler = null, string? endpoint = null, string? appVersion = null, string? statePath = null)`
  - `const string EndpointUrl`
  - `static string NormalizeTransport(string? transport)` — public for direct testing.

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.App.Tests/UsagePingServiceTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Namager.App.Services;
using Xunit;

public class UsagePingServiceTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"usage-ping-test-{Guid.NewGuid():N}.json");

    /// Records every request and replays a scripted outcome.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _throw;
        public List<string> Bodies { get; } = new();
        public HttpRequestMessage? LastRequest { get; private set; }
        public int Calls => Bodies.Count;

        public FakeHandler(HttpStatusCode status = HttpStatusCode.NoContent, Exception? toThrow = null)
        { _status = status; _throw = toThrow; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            if (_throw is not null) throw _throw;
            return new HttpResponseMessage(_status);
        }
    }

    // ---------- transport normalization ----------
    [Theory]
    [InlineData("USB", "usb")]
    [InlineData("WiFi", "wifi")]
    [InlineData("wifi", "wifi")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("Bluetooth", "unknown")]
    public void NormalizeTransport_maps_provider_names_to_wire_values(string? input, string expected)
        => Assert.Equal(expected, UsagePingService.NormalizeTransport(input));

    // ---------- payload ----------
    [Fact]
    public async Task PingAsync_posts_exactly_the_four_documented_fields()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(1, handler.Calls);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.test/ping", handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "appVersion", "fw", "installId", "transport" }, keys);
        Assert.Equal("1.2.0", doc.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("2.5.1", doc.RootElement.GetProperty("fw").GetString());
        Assert.Equal("usb", doc.RootElement.GetProperty("transport").GetString());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("installId").GetString(), out _));
    }

    // ---------- day gate ----------
    [Fact]
    public async Task PingAsync_sends_once_per_day()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");
        await svc.PingAsync("2.5.1", "USB");   // same day, fresh read of the saved state

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PingAsync_reuses_the_same_install_id_across_days()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");
        // Rewind the gate to simulate "yesterday", keeping the minted install ID.
        (UsageState.Load(path) with { LastPingUtc = "2000-01-01" }).Save(path);
        await svc.PingAsync("2.5.1", "WiFi");

        Assert.Equal(2, handler.Calls);
        static string Id(string body) =>
            JsonDocument.Parse(body).RootElement.GetProperty("installId").GetString()!;
        Assert.Equal(Id(handler.Bodies[0]), Id(handler.Bodies[1]));
    }

    // ---------- failure handling ----------
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task PingAsync_does_not_record_the_day_when_the_server_rejects_it(HttpStatusCode status)
    {
        var path = TempPath();
        var svc = new UsagePingService(new FakeHandler(status), "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);   // offline/rejected days are not burned
    }

    [Fact]
    public async Task PingAsync_swallows_transport_exceptions()
    {
        var path = TempPath();
        var svc = new UsagePingService(
            new FakeHandler(toThrow: new HttpRequestException("offline")),
            "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");   // must not throw

        Assert.Null(UsageState.Load(path).LastPingUtc);
    }

    [Fact]
    public async Task PingAsync_records_the_day_on_success()
    {
        var path = TempPath();
        var svc = new UsagePingService(new FakeHandler(), "https://example.test/ping", "1.2.0", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), UsageState.Load(path).LastPingUtc);
    }

    // ---------- dev builds ----------
    [Fact]
    public async Task PingAsync_is_a_no_op_for_dev_builds()
    {
        var handler = new FakeHandler();
        var path = TempPath();
        var svc = new UsagePingService(handler, "https://example.test/ping", "1.0.0-dev", path);

        await svc.PingAsync("2.5.1", "USB");

        Assert.Equal(0, handler.Calls);
        Assert.False(File.Exists(path));   // dev runs leave no trace at all
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~UsagePingServiceTests`
Expected: FAIL — compile error, `The type or namespace name 'UsagePingService' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Namager.App/Services/UsagePingService.cs`:

```csharp
using System.Net.Http;
using System.Net.Http.Json;

namespace Namager.App.Services;

public interface IUsagePingService
{
    /// <summary>Best-effort anonymous ping. Never throws.</summary>
    Task PingAsync(string firmware, string? transport, CancellationToken ct = default);
}

/// <summary>Sends one anonymous ping per install per UTC day when a pedal connects, so we can
/// tell whether the app has returning users (spec: docs/superpowers/specs/2026-07-23-usage-telemetry-design.md).
/// The entire payload is installId / appVersion / fw / transport — see PRIVACY.md, which must
/// stay in sync with this file.
/// Same contract as UpdateCheckService: never throws, never blocks, silent on every failure.</summary>
public sealed class UsagePingService : IUsagePingService
{
    /// <summary>The /ping route on the same worker that serves feedback at "/".</summary>
    public const string EndpointUrl = "https://namager-sonulab-feedback.ed-eed.workers.dev/ping";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _appVersion;
    private readonly string? _statePath;

    public UsagePingService(HttpMessageHandler? handler = null, string? endpoint = null,
                            string? appVersion = null, string? statePath = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _endpoint = endpoint ?? EndpointUrl;
        _appVersion = appVersion ?? AppInfo.Version;
        _statePath = statePath;
    }

    /// <summary>SessionState.Transport carries ILinkProvider.Name ("USB" / "WiFi"). Lowercasing
    /// here keeps the wire format stable if a provider's display name is ever reworded, and
    /// anything unrecognised still pings (as "unknown") rather than going silent.</summary>
    public static string NormalizeTransport(string? transport) => transport?.ToLowerInvariant() switch
    {
        "usb" => "usb",
        "wifi" => "wifi",
        _ => "unknown",
    };

    public async Task PingAsync(string firmware, string? transport, CancellationToken ct = default)
    {
        // Dev builds never ping: the author connects the pedal many times a day and would
        // otherwise dominate the active-user count. Same rule as UpdateCheckService.IsNewer.
        if (_appVersion.Contains('-')) return;

        try
        {
            var state = UsageState.Load(_statePath);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!state.ShouldPing(today)) return;

            var resp = await _http.PostAsJsonAsync(_endpoint, new
            {
                installId = state.InstallId,
                appVersion = _appVersion,
                fw = firmware,
                transport = NormalizeTransport(transport),
            }, ct);

            // Only a confirmed success burns the day, so an offline week doesn't silently
            // consume its days. The cost is one wasted request per launch while offline.
            if (resp.IsSuccessStatusCode)
                (state with { LastPingUtc = today.ToString("yyyy-MM-dd") }).Save(_statePath);
        }
        catch
        {
            // Offline, DNS failure, timeout, cancellation, malformed response: all no-ops.
            // Telemetry must never be able to surface an error to the user.
        }
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~UsagePingServiceTests`
Expected: PASS — 15 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/UsagePingService.cs tests/Namager.App.Tests/UsagePingServiceTests.cs
git commit -m "feat: UsagePingService - anonymous once-daily usage ping"
```

---

### Task 3: Fire the ping on the first successful connect

**Files:**
- Modify: `src/Namager.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs` (the `new ConnectionViewModel(session)` call)
- Test: `tests/Namager.App.Tests/ConnectionViewModelTests.cs`

**Interfaces:**
- Consumes: `IUsagePingService.PingAsync(string firmware, string? transport, CancellationToken)` from Task 2.
- Produces: `ConnectionViewModel(DeviceSession session, IUsagePingService? usage = null)` — the optional second parameter keeps every existing call site and test compiling; `null` means no ping.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Namager.App.Tests/ConnectionViewModelTests.cs`, inside the existing class:

```csharp
    private sealed class SpyUsagePing : Namager.App.Services.IUsagePingService
    {
        public List<(string Firmware, string? Transport)> Pings { get; } = new();
        public Task PingAsync(string firmware, string? transport, CancellationToken ct = default)
        {
            Pings.Add((firmware, transport));
            return Task.CompletedTask;
        }
    }

    [Fact] public async Task Connect_pings_usage_once_with_firmware_and_transport()
    {
        var spy = new SpyUsagePing();
        var vm = new ConnectionViewModel(Session(), spy);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Single(spy.Pings);
        Assert.Equal("2.5.1", spy.Pings[0].Firmware);
        Assert.Equal("USB", spy.Pings[0].Transport);
    }

    [Fact] public async Task Reconnecting_in_the_same_run_does_not_ping_again()
    {
        var spy = new SpyUsagePing();
        var vm = new ConnectionViewModel(Session(), spy);

        await vm.ConnectCommand.ExecuteAsync(null);
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Single(spy.Pings);
    }

    [Fact] public async Task Failed_connect_does_not_ping()
    {
        var spy = new SpyUsagePing();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, spy);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Empty(spy.Pings);
    }

    [Fact] public async Task Connect_without_a_usage_service_still_works()
    {
        var vm = new ConnectionViewModel(Session());   // null usage service
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ConnectionViewModelTests`
Expected: FAIL — compile error, `ConnectionViewModel` does not take 2 arguments.

- [ ] **Step 3: Write the implementation**

In `src/Namager.App/ViewModels/ConnectionViewModel.cs`, replace the field and constructor:

```csharp
public partial class ConnectionViewModel : ObservableObject
{
    private readonly DeviceSession _session;
    private readonly Namager.App.Services.IUsagePingService? _usage;
    private bool _usagePinged;   // first successful connect of this app run only

    public ConnectionViewModel(DeviceSession session,
                               Namager.App.Services.IUsagePingService? usage = null)
    { _session = session; _usage = usage; }
```

Then, in `ConnectAsync`, immediately after `Connected?.Invoke(this, EventArgs.Empty);`:

```csharp
            // Anonymous usage ping: first successful connect per run, at most once per UTC day.
            // Awaited so tests are deterministic; PingAsync itself never throws and returns
            // immediately when the day is already recorded or this is a dev build.
            if (_usage is not null && !_usagePinged)
            {
                _usagePinged = true;
                await _usage.PingAsync(state.Device!.Version, state.Transport);
            }
```

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, change the construction:

```csharp
        _connection = new ConnectionViewModel(session, new UsagePingService());
```

`UsagePingService` is in `Namager.App.Services`, which `MainWindowViewModel.cs` already imports — if it does not, add `using Namager.App.Services;`.

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ConnectionViewModelTests`
Expected: PASS — 6 tests.

- [ ] **Step 5: Run the whole suite — nothing else may regress**

Run: `dotnet test`
Expected: PASS — 490 existing + 31 new (12 + 15 + 4) = 521 tests, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ConnectionViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/ConnectionViewModelTests.cs
git commit -m "feat: ping usage on the first successful pedal connect of a run"
```

---

### Task 4: Worker `/ping` route and D1 schema

No JavaScript test toolchain exists in this repo and the feedback worker was validated by hand, so this task ships with a `curl` checklist rather than introducing a Node test runner.

**Files:**
- Modify: `infra/feedback-worker/worker.js`
- Create: `infra/feedback-worker/schema.sql`
- Modify: `infra/feedback-worker/wrangler.toml`
- Create: `docs/VALIDATION-usage-telemetry.md`

**Interfaces:**
- Consumes: the four-field payload from Task 2.
- Produces: `POST /ping` → 204 on success; the D1 tables `pings` and `installs`.

- [ ] **Step 1: Write the D1 schema**

Create `infra/feedback-worker/schema.sql`:

```sql
-- Append-only ping log. The composite primary key is the server-side backstop for the
-- client's once-per-day gate: a reinstall, a clock change, or a replayed request cannot
-- double-count a day. Keeping raw rows means new metrics are a query, not a redeploy.
CREATE TABLE IF NOT EXISTS pings (
  install_id  TEXT NOT NULL,
  day         TEXT NOT NULL,          -- UTC date, YYYY-MM-DD
  app_version TEXT NOT NULL,
  fw_version  TEXT NOT NULL,
  transport   TEXT NOT NULL,
  PRIMARY KEY (install_id, day)
);

-- Rolled-up per-install state, upserted on each accepted ping.
CREATE TABLE IF NOT EXISTS installs (
  install_id     TEXT PRIMARY KEY,
  first_seen     TEXT NOT NULL,
  last_seen      TEXT NOT NULL,
  active_days    INTEGER NOT NULL DEFAULT 0,
  app_version    TEXT NOT NULL,       -- most recent seen
  fw_version     TEXT NOT NULL,       -- most recent seen
  last_transport TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_installs_last_seen  ON installs(last_seen);
CREATE INDEX IF NOT EXISTS idx_installs_first_seen ON installs(first_seen);
CREATE INDEX IF NOT EXISTS idx_pings_day           ON pings(day);
```

- [ ] **Step 2: Add the D1 binding**

Replace `infra/feedback-worker/wrangler.toml` with:

```toml
name = "namager-sonulab-feedback"
main = "worker.js"
compatibility_date = "2026-07-01"

# Usage telemetry store. database_id is filled in by `wrangler d1 create` (Step 6).
[[d1_databases]]
binding = "USAGE_DB"
database_name = "namager-usage"
database_id = "REPLACE_AFTER_D1_CREATE"
```

- [ ] **Step 3: Add the router and `/ping` handler**

In `infra/feedback-worker/worker.js`, add these constants below the existing `MAX_PER_HOUR`:

```js
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TRANSPORTS = new Set(['usb', 'wifi', 'unknown']);
const pingHits = new Map();
const PING_MAX_PER_HOUR = 20;
```

Rename the existing exported `fetch` body into a `handleFeedback(request, env)` function (its
contents are unchanged, byte for byte), and replace the export with:

```js
export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    // "/" must keep behaving exactly as before: installed copies of the app POST feedback there.
    return pathname === '/ping' ? handlePing(request, env) : handleFeedback(request, env);
  },
};

async function handlePing(request, env) {
  if (request.method !== 'POST')
    return new Response('method not allowed', { status: 405 });
  if (!(request.headers.get('content-type') || '').includes('application/json'))
    return new Response('unsupported content type', { status: 415 });

  let p;
  try { p = await request.json(); } catch { return new Response('bad json', { status: 400 }); }
  if (!p || typeof p !== 'object') return new Response('bad json', { status: 400 });

  // A strict GUID check keeps the table hard to pollute with invented or enumerated IDs.
  if (typeof p.installId !== 'string' || !GUID_RE.test(p.installId))
    return new Response('invalid installId', { status: 400 });
  for (const field of ['appVersion', 'fw']) {
    if (typeof p[field] !== 'string' || !p[field].trim() || p[field].length > 20)
      return new Response(`invalid ${field}`, { status: 400 });
  }
  if (typeof p.transport !== 'string' || !TRANSPORTS.has(p.transport))
    return new Response('invalid transport', { status: 400 });

  // IP is used for rate limiting ONLY and is never written to D1 (PRIVACY.md).
  const ip = request.headers.get('cf-connecting-ip') || 'unknown';
  const now = Date.now();
  const recent = (pingHits.get(ip) || []).filter(t => now - t < 3600_000);
  if (recent.length >= PING_MAX_PER_HOUR)
    return new Response('rate limited', { status: 429 });
  pingHits.set(ip, [...recent, now]);

  const day = new Date(now).toISOString().slice(0, 10);
  const id = p.installId.toLowerCase();

  try {
    // INSERT OR IGNORE: a duplicate (install_id, day) is accepted but changes nothing.
    const ins = await env.USAGE_DB.prepare(
      `INSERT OR IGNORE INTO pings (install_id, day, app_version, fw_version, transport)
       VALUES (?, ?, ?, ?, ?)`
    ).bind(id, day, p.appVersion, p.fw, p.transport).run();

    // active_days increments ONLY when the pings insert actually added a row, so a replayed
    // request cannot inflate retention numbers without a matching pings row.
    const isNewDay = ins.meta.changes > 0;

    await env.USAGE_DB.prepare(
      `INSERT INTO installs
         (install_id, first_seen, last_seen, active_days, app_version, fw_version, last_transport)
       VALUES (?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(install_id) DO UPDATE SET
         last_seen      = excluded.last_seen,
         active_days    = active_days + ?,
         app_version    = excluded.app_version,
         fw_version     = excluded.fw_version,
         last_transport = excluded.last_transport`
    ).bind(id, day, day, isNewDay ? 1 : 0, p.appVersion, p.fw, p.transport, isNewDay ? 1 : 0).run();
  } catch {
    // Never leak database errors to the client; the ping is disposable.
    return new Response(null, { status: 502 });
  }

  return new Response(null, { status: 204 });
}
```

- [ ] **Step 4: Write the manual validation checklist**

Create `docs/VALIDATION-usage-telemetry.md`:

```markdown
# Validation — usage telemetry `/ping`

Run against the deployed worker. Set `URL` first:

```bash
URL=https://namager-sonulab-feedback.ed-eed.workers.dev
GUID=8f3c1e64-0000-4000-8000-000000000001
J='content-type: application/json'
```

Each row states the exact command and the exact expected status.

| # | Case | Command | Expect |
|---|---|---|---|
| 1 | Happy path | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `204` |
| 2 | Duplicate same day | repeat command 1 | `204` (no new row — verify in #12) |
| 3 | Not POST | `curl -si $URL/ping` | `405` |
| 4 | Wrong content type | `curl -si -X POST $URL/ping -H 'content-type: text/plain' -d 'x'` | `415` |
| 5 | Bad JSON | `curl -si -X POST $URL/ping -H "$J" -d 'not json'` | `400` |
| 6 | Null JSON | `curl -si -X POST $URL/ping -H "$J" -d 'null'` | `400` |
| 7 | Bad GUID | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"nope\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `400` |
| 8 | Oversized appVersion | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"123456789012345678901\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `400` |
| 9 | Missing fw | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"transport\":\"usb\"}"` | `400` |
| 10 | Bad transport | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"ble\"}"` | `400` |
| 11 | Rate limit | run command 1 twenty-one times in a minute | final one `429` |

- [ ] **12. Duplicate did not double-count.** After commands 1 and 2:

```bash
npx wrangler d1 execute namager-usage --remote \
  --command "SELECT active_days, COUNT(*) OVER () FROM installs WHERE install_id='$GUID'"
```

Expect `active_days = 1`, and exactly one row in `pings` for that install/day.

- [ ] **13. Feedback route regression.** Submit feedback from the app's Send Feedback dialog
  (or POST to the bare `$URL`) and confirm a `user-feedback` issue appears on
  `EdHubbell/Namager.Sonulab`. `/` must be unaffected by the router change.

- [ ] **14. Real end-to-end.** Run a **release-versioned** build (a `-dev` build will not ping
  by design), connect the pedal, then confirm a row landed:

```bash
npx wrangler d1 execute namager-usage --remote --command "SELECT * FROM installs"
```
```

- [ ] **Step 5: Verify the worker parses**

Run: `node --check infra/feedback-worker/worker.js`
Expected: no output (syntax OK).

- [ ] **Step 6: Create the database and deploy**

```bash
cd infra/feedback-worker
npx wrangler d1 create namager-usage
```

Copy the printed `database_id` into `wrangler.toml`, replacing `REPLACE_AFTER_D1_CREATE`. Then:

```bash
npx wrangler d1 execute namager-usage --remote --file schema.sql
npx wrangler deploy
```

- [ ] **Step 7: Work through `docs/VALIDATION-usage-telemetry.md`**

Complete rows 1–13. (Row 14 needs a release build and is repeated in Task 5.) Every row must
match its expected status before continuing.

- [ ] **Step 8: Commit**

```bash
git add infra/feedback-worker/worker.js infra/feedback-worker/schema.sql infra/feedback-worker/wrangler.toml docs/VALIDATION-usage-telemetry.md
git commit -m "feat(worker): /ping route writing usage pings to D1"
```

---

### Task 5: Disclosure docs and the reporting queries

**Files:**
- Create: `PRIVACY.md`
- Modify: `README.md`
- Modify: `infra/feedback-worker/README.md`

**Interfaces:**
- Consumes: the payload from Task 2 and the schema from Task 4.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write `PRIVACY.md`**

Create `PRIVACY.md` at the repo root:

```markdown
# Privacy

NAMager talks to the internet in three places. This page lists all of them.

## 1. Anonymous usage ping

So I can tell whether anyone actually uses NAMager — and therefore whether it's worth building
more of it — the app sends one small message when you connect your pedal.

**When:** the first time a pedal successfully connects in an app session, and at most once per
day. If you never connect a pedal, nothing is ever sent. Development builds never send anything.

**What, in full:**

| Field | Example | Why |
|---|---|---|
| `installId` | `8f3c1e64-…` | A random ID made on first run. Lets me count people instead of launches, and see whether they come back. Not derived from anything about you or your machine. |
| `appVersion` | `1.2.0` | Tells me how quickly people move to new releases. |
| `fw` | `2.5.1` | Which pedal firmware versions are in use, so I know what to keep supporting. |
| `transport` | `usb` | Whether anyone uses the WiFi connection, which is buggy and expensive to maintain. |

**What is never sent:** your name, email, IP address, preset/amp/IR names, file paths, device
serial numbers, or anything about what you do inside the app. Your IP is used only to rate-limit
abuse at the server and is never stored.

There is no opt-out toggle. If you'd rather not send it, don't use the app — or block
`namager-sonulab-feedback.ed-eed.workers.dev` at your firewall, which the app handles silently.
Deleting `%APPDATA%\Namager\usage.json` resets your install ID.

## 2. Update check

On launch the app asks GitHub's public API for the latest release version. This is a normal
unauthenticated web request; GitHub sees it, I don't.

## 3. Send Feedback (only when you use it)

The Send Feedback dialog posts the name, email, and message **you type**, plus your app version
and OS, and creates a **public** GitHub issue. Don't put anything in it you wouldn't post
publicly.

## Tone3000

If you sign in to Tone3000, that's between you and Tone3000 under their privacy policy. Your
token is stored locally, encrypted with Windows DPAPI.
```

- [ ] **Step 2: Link it from the README**

In `README.md`, add this section immediately before the license/footer section (or at the end if
there is none):

```markdown
## Privacy

NAMager sends one anonymous ping when you connect your pedal (a random install ID, the app
version, your pedal's firmware version, and whether you connected over USB or WiFi) so I can
tell whether the app has actual users. No personal data, no tracking of what you do in the app,
no opt-out toggle. Full details, including what is never sent: [PRIVACY.md](PRIVACY.md).
```

- [ ] **Step 3: Document setup and queries in the worker README**

In `infra/feedback-worker/README.md`, change the opening line to note the worker now serves two
routes:

```markdown
# NAMager worker

Two routes on one worker:

- **`/`** — the app's Send Feedback dialog. Receives `{ name, email, message, appVersion, os,
  website }` and creates a GitHub issue labeled `user-feedback`. `website` is a honeypot.
- **`/ping`** — the anonymous usage ping. Receives `{ installId, appVersion, fw, transport }`
  and records it in the `namager-usage` D1 database. See `PRIVACY.md`.

**This worker is deployed manually, never by CI.**
```

Then append a new section at the end:

```markdown
## Usage telemetry (D1)

One-time setup:

```
npx wrangler d1 create namager-usage         # paste database_id into wrangler.toml
npx wrangler d1 execute namager-usage --remote --file schema.sql
npx wrangler deploy
```

### Reporting queries

Run with `npx wrangler d1 execute namager-usage --remote --command "<sql>"`.

```sql
-- Monthly actives: installs that connected a pedal in the last 30 days
SELECT COUNT(*) FROM installs WHERE last_seen >= date('now','-30 days');

-- Retention: of installs first seen 30-60 days ago, how many are still connecting?
SELECT COUNT(*) FILTER (WHERE last_seen >= date('now','-14 days')) * 1.0 / COUNT(*)
FROM installs WHERE first_seen BETWEEN date('now','-60 days') AND date('now','-30 days');

-- New installs per day
SELECT first_seen, COUNT(*) FROM installs GROUP BY first_seen ORDER BY first_seen DESC;

-- Does anyone use WiFi, and regularly?
SELECT transport, COUNT(DISTINCT install_id) AS installs, COUNT(*) AS days
FROM pings WHERE day >= date('now','-60 days') GROUP BY transport;

-- App version and firmware spread
SELECT app_version, COUNT(*) FROM installs GROUP BY app_version ORDER BY 2 DESC;
SELECT fw_version,  COUNT(*) FROM installs GROUP BY fw_version  ORDER BY 2 DESC;

-- How engaged are the returning users? (active days per install)
SELECT active_days, COUNT(*) FROM installs GROUP BY active_days ORDER BY active_days;
```

**Reading these honestly:** these counts include only people who *connected a pedal*. Anyone who
downloaded the app and never plugged in is invisible here — that top of the funnel is the release
asset download count on GitHub Releases, which is a separate number.
```

- [ ] **Step 4: Verify PRIVACY.md matches the code**

Read `src/Namager.App/Services/UsagePingService.cs` and confirm the anonymous object in
`PingAsync` has exactly the four fields listed in the PRIVACY.md table, with the same names.
This check is the whole point of the document — if they disagree, the code wins and the doc is
wrong.

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test`
Expected: PASS — 521 tests, 0 failures.

```bash
git add PRIVACY.md README.md infra/feedback-worker/README.md
git commit -m "docs: PRIVACY.md, README privacy section, and telemetry reporting queries"
```

- [ ] **Step 6: End-to-end check on a release build**

This is row 14 of `docs/VALIDATION-usage-telemetry.md` and cannot be done from a `-dev` build.
Build with an explicit version, run it, connect the pedal:

```bash
dotnet run --project src/Namager.App -p:Version=1.0.0-validation-check
```

Note: that version string contains `-`, so it will *not* ping — that is the dev guard working.
To genuinely exercise the path, build with a clean version:

```bash
dotnet build src/Namager.App -c Release -p:Version=9.9.9
```

Run the built exe from `src/Namager.App/bin/Release/net10.0/`, connect the pedal, then:

```bash
npx wrangler d1 execute namager-usage --remote --command "SELECT * FROM installs WHERE app_version='9.9.9'"
```

Expect exactly one row, `active_days = 1`, `last_transport = usb`. Then delete the test row:

```bash
npx wrangler d1 execute namager-usage --remote --command "DELETE FROM pings WHERE app_version='9.9.9'; DELETE FROM installs WHERE app_version='9.9.9'"
```

---

## Done when

- `dotnet test` is green at 521 tests.
- `docs/VALIDATION-usage-telemetry.md` rows 1–14 all pass.
- A release build connecting to a real pedal produces exactly one `installs` row and one `pings`
  row, and connecting a second time the same day adds neither.
- The Send Feedback dialog still files a GitHub issue.
- `PRIVACY.md`'s field table and `UsagePingService.PingAsync`'s payload list the same four names.
