# Disable WiFi Connections in the App — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make NAMager USB-only — the app no longer offers the WiFi/TCP transport — while keeping that transport, its 29 tests, and `HwCheck --wifi` alive as a protocol-diagnostic path.

**Architecture:** Three surgical changes, no new subsystems. `MainWindowViewModel`'s inline provider list is extracted to a testable `BuildProviders` and loses its `WifiLinkProvider` entry; the app project drops its `Sonulab.Transport.Wifi` reference so the assembly stops shipping; the disconnected-state message stops promising a WiFi fallback that no longer exists. Docs follow.

**Tech Stack:** .NET 10, C#, Avalonia 12 (MVVM via CommunityToolkit.Mvvm), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md`

## Global Constraints

- **Do NOT delete** `src/Sonulab.Transport.Wifi` (10 files), `tests/Sonulab.Transport.Wifi.Tests` (29 tests), or `HwCheck --wifi` / `--ip`. This plan unwires; it does not remove.
- **Do NOT change** `SonuClient`'s `readRetryAttempts` default (stays at 4). Out of scope by explicit decision.
- **Do NOT remove** `UsagePingService.NormalizeTransport`'s `wifi` branch. Inert, kept deliberately so a re-enable is cheap.
- **Do NOT add FluentAvalonia** (Avalonia 12 + built-in `FluentTheme` only).
- No hex color literals in `.axaml` — colors come from `Styles/SonulabTheme.axaml` tokens. (No view changes expected, but the rule stands.)
- `HwCheck` keeps its `Sonulab.Transport.Wifi` project reference. Only the **app's** reference is removed.
- Exact new disconnected status string, em dash (—, U+2014) not hyphen:
  `Disconnected (no pedal found on USB — check the cable, and close VoidX-Control if it's running)`
- Exact new status-bar failure string: `No pedal found on USB`
- Baseline: **760 tests passing, 0 failing** (Core 268, App 299, Distill 86, Tone3000 78, Wifi 29). Final target: **761 passing, 0 failing** — one added test, none deleted, all 29 WiFi tests still green.
- The reversal path must stay two edits: restore the `ProjectReference` line, restore the provider entry. Do not couple anything else to the removal.

## File Structure

**Modify:**
- `src/Namager.App/ViewModels/MainWindowViewModel.cs:99-111` — extract `BuildProviders`, drop the WiFi entry.
- `src/Namager.App/Namager.App.csproj:44` — remove the WiFi `ProjectReference`; `:40-45` — add `InternalsVisibleTo`.
- `src/Namager.App/ViewModels/ConnectionViewModel.cs:66-67` — the two disconnected strings.
- `tests/Namager.App.Tests/ConnectionViewModelTests.cs:60-71` — the assertion on the old string.
- `CLAUDE.md` — architecture bullet (`:21-25`), VoidX gotcha (`:50`), USB→WiFi fallback gotcha (`:51-54`), WiFi-detection caveat (`:65-67`).
- `README.md:23-25` (fallback paragraph), `:103` (protocol line), `:124` (privacy line).
- `PRIVACY.md:20` — the `transport` row's justification.
- `docs/HARDWARE-VALIDATION-wifi.md` — header note.
- `docs/HARDWARE-VALIDATION-disconnect.md:40` — check 4 header note.

**Create:**
- `tests/Namager.App.Tests/LinkProviderWiringTests.cs` — the regression guard.

**Untouched (verify at the end):** everything under `src/Sonulab.Transport.Wifi/`, `tests/Sonulab.Transport.Wifi.Tests/`, `tools/HwCheck/`, `src/Sonulab.Core/SonuClient.cs`, `src/Namager.App/Services/UsagePingService.cs`.

---

### Task 1: USB-only provider wiring

**Files:**
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:99-111`
- Modify: `src/Namager.App/Namager.App.csproj:40-45`
- Test: `tests/Namager.App.Tests/LinkProviderWiringTests.cs` (create)

**Interfaces:**
- Consumes: `Sonulab.Core.Connection.ILinkProvider` (has `string Name { get; }`), `Sonulab.Core.Connection.SerialLinkProvider`, `Sonulab.Core.Transport.SerialLinkOptions`, `Sonulab.Core.Transport.SystemSerialPort`. All already imported by `MainWindowViewModel.cs:1-6`.
- Produces: `internal static IReadOnlyList<ILinkProvider> MainWindowViewModel.BuildProviders(SerialLinkOptions options)`. No later task depends on it; the test in this task is its only consumer.

**Verified before this plan was written:** `tests/Namager.App.Tests` does **not** reference the
`Sonulab.Transport.Wifi` project and uses no type from it — the single grep hit is a code comment in
`CrashGuardTests.cs:17` mentioning `TcpSonuLink` by name. Removing the app's `ProjectReference`
therefore cannot break the test project's compilation. If you nonetheless hit a compile error naming
a WiFi type, stop and report it: something changed since this plan was written.

- [ ] **Step 1: Add `InternalsVisibleTo` so the test can see the extracted method**

`Namager.App` has no `InternalsVisibleTo` today (only `Sonulab.Core` and `Namager.Tone3000` do). Follow `src/Sonulab.Core/Sonulab.Core.csproj:18`'s csproj-item form — the app project has no `AssemblyInfo.cs`.

In `src/Namager.App/Namager.App.csproj`, change the final `ItemGroup` (lines 40-45) from:

```xml
  <ItemGroup>
    <ProjectReference Include="..\Sonulab.Core\Sonulab.Core.csproj" />
    <ProjectReference Include="..\Sonulab.Distill\Sonulab.Distill.csproj" />
    <ProjectReference Include="..\Namager.Tone3000\Namager.Tone3000.csproj" />
    <ProjectReference Include="..\Sonulab.Transport.Wifi\Sonulab.Transport.Wifi.csproj" />
  </ItemGroup>
```

to:

```xml
  <ItemGroup>
    <ProjectReference Include="..\Sonulab.Core\Sonulab.Core.csproj" />
    <ProjectReference Include="..\Sonulab.Distill\Sonulab.Distill.csproj" />
    <ProjectReference Include="..\Namager.Tone3000\Namager.Tone3000.csproj" />
    <!-- Sonulab.Transport.Wifi is deliberately NOT referenced: the app is USB-only.
         The transport still exists and is exercised by HwCheck --wifi and its own 29 tests.
         Re-enabling WiFi = restore this reference + the provider entry in MainWindowViewModel. -->
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Namager.App.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `tests/Namager.App.Tests/LinkProviderWiringTests.cs`:

```csharp
using Namager.App.ViewModels;
using Sonulab.Core.Transport;
using Xunit;

public class LinkProviderWiringTests
{
    // The app is USB-only by decision (2026-07-25 spec). This is a regression guard: the WiFi
    // transport still exists and still compiles, so re-adding it to the provider list is a
    // two-line accident. Asserting only on the disconnected-status string would NOT catch that.
    [Fact] public void App_offers_exactly_one_transport_and_it_is_USB()
    {
        var providers = MainWindowViewModel.BuildProviders(new SerialLinkOptions());

        Assert.Single(providers);
        Assert.Equal("USB", providers[0].Name);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~LinkProviderWiringTests"`

Expected: build FAILS — `'MainWindowViewModel' does not contain a definition for 'BuildProviders'`.

- [ ] **Step 4: Extract `BuildProviders` and drop the WiFi entry**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, the constructor currently contains (lines 99-111):

```csharp
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
        var providers = new List<ILinkProvider>
        {
            // Fresh port enumeration per connect: a pedal replugged onto a new COM number
            // is found without restarting the app.
            new SerialLinkProvider(() => new SystemSerialPort(), options),
            // WiFi fallback: ~3s mDNS browse (query re-sent every 2s); returns null silently
            // when no network / multicast blocked / no pedal on the LAN.
            new Sonulab.Transport.Wifi.WifiLinkProvider(
                new Sonulab.Transport.Wifi.UdpMdnsQuerier(), TimeSpan.FromSeconds(3)),
        };
        var session = new DeviceSession(providers, new CompatibilityChecker(FirmwareCatalog.Default));
```

Replace with:

```csharp
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
        var session = new DeviceSession(BuildProviders(options), new CompatibilityChecker(FirmwareCatalog.Default));
```

Leave the `// Adaptive settle (perf spec §4)` comment block above `options` (lines 92-98) exactly where it is — it explains the `SerialLinkOptions` values, not the provider list.

Then add the extracted method to the class. Put it immediately after the constructor's closing brace, before `NavigateToUpload`:

```csharp
    /// <summary>The transports the app will try, in order. USB ONLY — the WiFi/TCP transport
    /// (src/Sonulab.Transport.Wifi) still exists and is exercised by HwCheck --wifi and its own
    /// tests, but it is deliberately not offered to users: the pedal answers mDNS intermittently,
    /// the link needs a bespoke response-debt resync layer, and a pedal that vanishes without a
    /// FIN/RST produces no disconnect signal at all. See
    /// docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md.
    ///
    /// Extracted from the constructor so LinkProviderWiringTests can assert the list stays
    /// single-entry — re-adding a fallback should fail a test, not slip through review.</summary>
    internal static IReadOnlyList<ILinkProvider> BuildProviders(SerialLinkOptions options) => new List<ILinkProvider>
    {
        // Fresh port enumeration per connect: a pedal replugged onto a new COM number
        // is found without restarting the app.
        new SerialLinkProvider(() => new SystemSerialPort(), options),
    };
```

`ILinkProvider`, `SerialLinkProvider`, `SerialLinkOptions` and `SystemSerialPort` are already in scope via the existing usings at `MainWindowViewModel.cs:1-6` — do not add any.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~LinkProviderWiringTests"`

Expected: PASS (1 test).

- [ ] **Step 6: Verify the WiFi assembly is genuinely gone from the app's output**

Run: `dotnet build src/Namager.App`

Then check the build output no longer contains the WiFi assembly:

```bash
ls src/Namager.App/bin/Debug/net10.0/Sonulab.Transport.Wifi.dll
```

Expected: `No such file or directory`.

If it IS still there, it is a stale artifact from before the reference was removed — run `dotnet clean src/Namager.App` and rebuild, then re-check. A file surviving a clean rebuild means the reference was not actually removed; fix that before continuing.

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`

Expected: **761 passing, 0 failing**. Confirm `Sonulab.Transport.Wifi.Tests` still reports **29 passing** — the transport is unwired from the app, not broken.

- [ ] **Step 8: Commit**

```bash
git add src/Namager.App/ViewModels/MainWindowViewModel.cs src/Namager.App/Namager.App.csproj tests/Namager.App.Tests/LinkProviderWiringTests.cs
git commit -m "feat(app): USB-only transport wiring; drop the WiFi project reference"
```

---

### Task 2: Tell the truth when no pedal is found

**Files:**
- Modify: `src/Namager.App/ViewModels/ConnectionViewModel.cs:66-67`
- Modify: `tests/Namager.App.Tests/ConnectionViewModelTests.cs:60-71`

**Interfaces:**
- Consumes: nothing from Task 1 (independent — this task would be correct even if Task 1 had not run).
- Produces: nothing later tasks depend on.

**Why this is its own task:** Task 1 makes the app USB-only structurally; this makes the *message* honest. A reviewer could reasonably approve one and reject the other.

- [ ] **Step 1: Update the failing test first**

In `tests/Namager.App.Tests/ConnectionViewModelTests.cs`, the existing test at lines 60-71 reads:

```csharp
    [Fact] public async Task Connect_when_no_device_found_sets_status()
    {
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Equal("Disconnected (no device found on USB or WiFi)", vm.Status);
    }
```

Replace it with:

```csharp
    [Fact] public async Task Connect_when_no_device_found_sets_status()
    {
        // USB-only since the 2026-07-25 spec: one provider, and the message names the VoidX-Control
        // case explicitly because WiFi is no longer the workaround for a held COM port.
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.Equal(
            "Disconnected (no pedal found on USB — check the cable, and close VoidX-Control if it's running)",
            vm.Status);
    }
```

Note the em dash (—, U+2014) in the expected string, not a hyphen.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~Connect_when_no_device_found_sets_status"`

Expected: FAIL with an assertion showing the actual value is still `Disconnected (no device found on USB or WiFi)`.

- [ ] **Step 3: Update the strings**

In `src/Namager.App/ViewModels/ConnectionViewModel.cs`, lines 66-67 currently read:

```csharp
                Status = "Disconnected (no device found on USB or WiFi)";
                _statusService.Failure("No device found on USB or WiFi");
```

Replace with:

```csharp
                Status = "Disconnected (no pedal found on USB — check the cable, and close VoidX-Control if it's running)";
                _statusService.Failure("No pedal found on USB");
```

The longer text goes in `Status` (the connection panel, which has room); the short form goes to the status bar, which is a single line.

Leave the `({state.Transport})` suffix at line 72 and the `SetIdleSummary` at line 78 alone — they will always render `(USB)` now, which is redundant but harmless, and they are the seam a WiFi re-enable would need.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~Connect_when_no_device_found_sets_status"`

Expected: PASS.

- [ ] **Step 5: Run the app test project**

Run: `dotnet test tests/Namager.App.Tests`

Expected: PASS. Watch for any other test asserting the old string — `grep -rn "USB or WiFi" tests/` should return nothing after this change.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ConnectionViewModel.cs tests/Namager.App.Tests/ConnectionViewModelTests.cs
git commit -m "feat(app): USB-only wording when no pedal is found"
```

---

### Task 3: Documentation

**Files:**
- Modify: `CLAUDE.md` (four places), `README.md` (three places), `PRIVACY.md:20`, `docs/HARDWARE-VALIDATION-wifi.md`, `docs/HARDWARE-VALIDATION-disconnect.md:40`

**Interfaces:**
- Consumes: the behavior established by Tasks 1 and 2.
- Produces: nothing code-facing.

**Read the current text of each file before editing** — line numbers below are from plan-writing time and shift as you edit. Match each file's existing prose style; `CLAUDE.md` in particular is dense and specific, with file references and rationale.

- [ ] **Step 1: `CLAUDE.md` — architecture bullet**

The `src/Sonulab.Transport.Wifi` bullet (around line 21) ends with:

```
  and `WifiLinkProvider` (an `ILinkProvider` — USB stays #1, WiFi is the auto fallback via `DeviceSession`).
```

Change that closing clause to record that it is no longer wired in:

```
  and `WifiLinkProvider` (an `ILinkProvider`). **NOT wired into the app** — the app is USB-only
  (2026-07-25); this transport is reachable only via `HwCheck --wifi`. Re-enabling = restore the
  `ProjectReference` in `Namager.App.csproj` + the provider entry in `MainWindowViewModel.BuildProviders`.
```

- [ ] **Step 2: `CLAUDE.md` — the two gotcha bullets**

Around lines 50-54, these two bullets currently read:

```
- **VoidX-Control must be CLOSED** to use the pedal — it holds COM6 exclusively.
- **USB→WiFi fallback:** connect tries USB first, then auto-discovers the pedal over WiFi (mDNS PTR
  `_http._tcp.local` filtered by TXT `id=voidx`; TCP 8080, identical wire protocol) — see
  `src/Sonulab.Transport.Wifi` and `docs/HARDWARE-VALIDATION-wifi.md`. VoidX holds only the COM port, so
  WiFi coexists with it; the pedal answers mDNS intermittently (the querier re-sends every ~2 s).
```

Replace both with:

```
- **VoidX-Control must be CLOSED** to use the pedal — it holds COM6 exclusively. This is now
  unconditional: WiFi used to coexist with VoidX (it holds only the COM port), and that was the one
  real capability given up when the app went USB-only.
- **The app is USB-only (2026-07-25).** The WiFi/TCP transport still exists and still passes its 29
  tests, but `MainWindowViewModel.BuildProviders` offers `SerialLinkProvider` alone. WiFi is reachable
  only via `HwCheck --wifi` (mDNS PTR `_http._tcp.local` filtered by TXT `id=voidx`; TCP 8080,
  identical wire protocol) — see `src/Sonulab.Transport.Wifi`, `docs/HARDWARE-VALIDATION-wifi.md`, and
  `docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md` for why: intermittent mDNS, a
  bespoke response-debt resync layer, and no disconnect signal on a silent vanish.
```

- [ ] **Step 3: `CLAUDE.md` — scope the WiFi-detection caveat to HwCheck**

The "Link death is typed" bullet ends (around lines 65-67) with:

```
  Reconnect-in-place is deliberately NOT supported — re-opening a live session resets the ESP32 and
  wedges the pedal. **On WiFi detection is best-effort:** only a real socket fault
```

Change the WiFi sentence's opening so it is clearly about HwCheck, not the app:

```
  Reconnect-in-place is deliberately NOT supported — re-opening a live session resets the ESP32 and
  wedges the pedal. **On WiFi (HwCheck only — the app is USB-only) detection is best-effort:** only a real socket fault
```

Leave the rest of that sentence, and the `TimeoutException` explanation that follows it, unchanged.

- [ ] **Step 4: `README.md` — the fallback paragraph**

Lines 23-25 currently read:

```
NAMager connects over USB first and falls back to WiFi automatically when the pedal is on
your network (same protocol, auto-discovered via mDNS) - handy when a cable or USB port lets you down.
But really, best to stick with a USB connection. WiFi might have been an overreach for compatibility sake. It's still buggy.
```

Replace with:

```
NAMager connects over USB. It used to fall back to WiFi automatically, but that turned out to be an
overreach - the pedal's WiFi stack was unreliable enough that the fallback caused more confusion than
it solved, so it's no longer offered.
```

This is user-facing copy: keep the file's existing casual tone and its `-` hyphens (this file does not use em dashes).

- [ ] **Step 5: `README.md` — protocol and privacy lines**

Line 103 currently reads:

```
Plaintext over USB serial (CH340, `COM6`, 115200 8N1), BLE, or WiFi. Commands are NUL-terminated
```

Change to:

```
Plaintext over USB serial (CH340, `COM6`, 115200 8N1). The same protocol also runs over BLE and WiFi
on the pedal; NAMager speaks only the serial transport. Commands are NUL-terminated
```

Line 124 currently reads:

```
version, your pedal's firmware version, and whether you connected over USB or WiFi) so I can
```

Change to:

```
version, your pedal's firmware version, and which transport you connected over) so I can
```

Leave line 56 alone — "(Bluetooth, USB, WiFi) across a plethora of platforms" describes VoidX-Control's scope, not NAMager's.

- [ ] **Step 6: `PRIVACY.md` — the `transport` row**

Line 20 currently reads:

```
| `transport` | `usb` | Whether anyone uses the WiFi connection, which is buggy and expensive to maintain. |
```

Replace with:

```
| `transport` | `usb` | Which transport the app connected over. The app is USB-only, so this is always `usb` today; the field stays so a future transport can be measured. |
```

- [ ] **Step 7: `docs/HARDWARE-VALIDATION-wifi.md` — header note**

Insert immediately after the `# Manual validation — WiFi transport (SP1)` heading, before the existing "Live bench results…" line:

```
> **The app no longer uses this transport.** NAMager went USB-only on 2026-07-25
> (`docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md`). The WiFi/TCP transport still
> exists and still passes its 29 unit tests, and everything below is still reachable via
> `HwCheck --wifi`. The "App (Ed)" GUI checks in this file are obsolete — there is no WiFi path in
> the app to check. The bench results are retained as a protocol record.
```

- [ ] **Step 8: `docs/HARDWARE-VALIDATION-disconnect.md` — check 4 header note**

Insert immediately after the `## 4. WiFi equivalent` heading (line 40), before its numbered steps:

```
> **HwCheck only — the app is USB-only as of 2026-07-25.** This check exercises a transport the app
> does not offer. Run it if you are working on the WiFi transport itself; skip it otherwise.
```

The "Known limitation" paragraph at the end of that check stays exactly as written — it is now part of the *reason* WiFi was unwired, not merely a caveat.

- [ ] **Step 9: Verify no stale claims survive**

Run:

```bash
grep -rn -i "falls back to wifi\|USB or WiFi\|auto fallback" README.md CLAUDE.md PRIVACY.md docs/*.md src/ tests/ --include=*.md --include=*.cs | grep -v "/bin/\|/obj/"
```

Expected: no hits. If a `docs/superpowers/plans/` or `docs/superpowers/specs/` file matches, that is fine and must be left alone — historical plans and specs are a record of what was true when written, not live documentation. Only flag hits in the files this task edits, plus `src/` and `tests/`.

- [ ] **Step 10: Final build and full suite**

Run: `dotnet build` then `dotnet test`

Expected: build succeeds; **761 passing, 0 failing**. Report the actual counts.

- [ ] **Step 11: Confirm nothing out of scope was touched**

The spec and this plan were committed to `main` before implementation began, so a `main..HEAD` diff
would not list them — do not expect to see them. What matters is that no **forbidden** path was
touched. Check that directly:

```bash
BASE=$(git merge-base main HEAD)
echo "--- files changed since $BASE ---"
git diff --name-only "$BASE"..HEAD
echo "--- forbidden paths (must print nothing) ---"
git diff --name-only "$BASE"..HEAD | grep -E \
  'src/Sonulab\.Transport\.Wifi/|tests/Sonulab\.Transport\.Wifi\.Tests/|tools/HwCheck/|src/Sonulab\.Core/|src/Namager\.App/Services/UsagePingService\.cs'
```

Expected: the second command prints nothing (grep exits 1, which is the pass condition here).

The first command should list only these ten:

```
CLAUDE.md
PRIVACY.md
README.md
docs/HARDWARE-VALIDATION-disconnect.md
docs/HARDWARE-VALIDATION-wifi.md
src/Namager.App/Namager.App.csproj
src/Namager.App/ViewModels/ConnectionViewModel.cs
src/Namager.App/ViewModels/MainWindowViewModel.cs
tests/Namager.App.Tests/ConnectionViewModelTests.cs
tests/Namager.App.Tests/LinkProviderWiringTests.cs
```

- [ ] **Step 12: Commit**

```bash
git add CLAUDE.md README.md PRIVACY.md docs/HARDWARE-VALIDATION-wifi.md docs/HARDWARE-VALIDATION-disconnect.md
git commit -m "docs: record that the app is USB-only"
```

---

## Verification summary

| Spec section | Task |
|---|---|
| §1 App wiring (provider list) | 1 |
| §1 `ProjectReference` removal | 1 |
| §2 User-visible strings | 2 |
| §3 What stays untouched | 1 (Step 6 assembly check), 3 (Step 11 file-list check) |
| §4 Telemetry + PRIVACY.md | 3 (Step 6); the `wifi` branch in `NormalizeTransport` is left alone by omission — no task touches it |
| §5 Documentation | 3 |
| §6 Tests (update / add / delete nothing) | 2 (update), 1 (add) |
| Verification (761 passing) | 1 (Step 7), 3 (Step 10) |

## Notes for the executor

- **This plan removes a code path; it adds no runtime behavior.** No new hardware validation is required. The pending USB checks 1–3 in `docs/HARDWARE-VALIDATION-disconnect.md` remain outstanding from the previous cycle and are unaffected.
- **If any WiFi test fails at any point, stop.** Unwiring the app must not break the transport. A failing `Sonulab.Transport.Wifi.Tests` means something was deleted that should not have been.
- **Open question, deliberately not resolved by this plan:** the `transport` telemetry field was built to answer the question this change decides (`PRIVACY.md` said so before Task 3 rewrites it). Whether to review the collected pings before shipping is the human's call and does not block implementation.
