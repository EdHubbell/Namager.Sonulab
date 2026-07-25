# Disable WiFi connections in the app — design

Date: 2026-07-25
Status: approved, ready for planning

## Problem

The WiFi/TCP transport is the app's automatic fallback when USB fails. It has been the source of
disproportionate complexity and unreliability, and the evidence has accumulated to the point where
shipping it to users costs more than it returns:

- The pedal **answers mDNS intermittently** — the querier has to re-send every ~2 s
  (`CLAUDE.md`, `UdpMdnsQuerier`).
- The link needed a bespoke **response-debt resync layer** (`_pending`, `_wedgeStrikes`,
  `staleConsumed`, `ResyncQuietMs`) because the pedal returns late responses to *previous* commands
  (`TcpSonuLink.SendCoreAsync`).
- It forced `SonuClient`'s **4-attempt read-retry budget** into existence, for two WiFi-only quirks:
  an empty record (`"\r\n\0"`) and a late previous response arriving in place of the real one
  (`SonuClient.cs:22-27`). Serial answers correctly on attempt 1.
- The 2026-07-24 disconnect work established that **a pedal vanishing over WiFi produces no
  classification at all**: no FIN/RST means the buffered write succeeds and the read simply times
  out, and `TimeoutException` is deliberately not a disconnect. The USB half of that feature is
  sound; the WiFi half is best-effort by construction.
- `README.md:25` has said "WiFi might have been an overreach for compatibility sake. It's still
  buggy" since before this spec.

## The one real capability being given up

**WiFi is currently the only way to use NAMager while VoidX-Control is open.** VoidX holds the COM
port exclusively but not the network, so `CLAUDE.md`'s "VoidX-Control must be CLOSED" gotcha is in
practice "…must be closed *for USB*". After this change it is unconditional: a user running VoidX
must close it before NAMager can connect. This is accepted, and the new disconnected-state message
names it so the user is not left guessing.

## Decisions taken

| Decision | Choice |
|---|---|
| Scope | Unwire from the app; keep the code. The transport survives as an HwCheck-only protocol-diagnostic tool. |
| `Namager.App.csproj` project reference | **Removed.** Keeping it ships a dead assembly and lets a dependency be re-introduced by accident. |
| `SonuClient` read-retry budget | **Untouched** (stays at 4). Inert on serial; changing it is a separate, independently-testable question. |
| WiFi tests | **All 29 kept and still running in CI.** Nothing is deleted. |
| Telemetry `transport` field | Kept, including its now-inert `wifi` branch. |

## Design

### 1. App wiring — the actual change

`src/Namager.App/ViewModels/MainWindowViewModel.cs:101-110` drops the `WifiLinkProvider` entry:

```csharp
var providers = new List<ILinkProvider>
{
    // Fresh port enumeration per connect: a pedal replugged onto a new COM number
    // is found without restarting the app.
    new SerialLinkProvider(() => new SystemSerialPort(), options),
    // USB only. The WiFi/TCP transport still exists (src/Sonulab.Transport.Wifi) and is
    // reachable from HwCheck --wifi, but it is deliberately NOT offered to users — see
    // docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md.
};
```

`DeviceSession` needs **no change**: it iterates whatever providers it is handed, so a single-entry
list simply tries USB and reports failure.

`src/Namager.App/Namager.App.csproj:44` — the
`<ProjectReference Include="..\Sonulab.Transport.Wifi\Sonulab.Transport.Wifi.csproj" />` line is
removed. This makes "the app does not do WiFi" structurally true rather than a convention, and stops
the WiFi assembly shipping in the installer.

**The reversal path is exactly two edits**: restore that `ProjectReference` line, restore the
provider entry. Nothing else in this spec blocks a revert.

### 2. User-visible strings

`src/Namager.App/ViewModels/ConnectionViewModel.cs:66-67` currently reads:

```csharp
Status = "Disconnected (no device found on USB or WiFi)";
_statusService.Failure("No device found on USB or WiFi");
```

Becomes USB-only wording that names the most likely cause, since the VoidX case no longer has a
workaround:

```csharp
Status = "Disconnected (no pedal found on USB — check the cable, and close VoidX-Control if it's running)";
_statusService.Failure("No pedal found on USB");
```

The status bar's `({state.Transport})` suffix (`ConnectionViewModel.cs:72`) is **unchanged**. It will
always read `(USB)`. Mildly redundant, but it is the seam a re-enable needs and removing it is churn.

### 3. What stays untouched

- `src/Sonulab.Transport.Wifi` — all 10 files, still in the solution, still built.
- `tests/Sonulab.Transport.Wifi.Tests` — all 29 tests, still run by `dotnet test`.
- `tools/HwCheck` — `--wifi` and `--ip <addr>` keep working, and HwCheck keeps its project reference.
- `SonuClient` — retry budget, background lane, latch: all unchanged.
- `UsagePingService.NormalizeTransport` — keeps its `wifi` branch. Inert, zero cost, and deleting it
  would make a revert annoying.

### 4. Telemetry and PRIVACY.md

`PRIVACY.md:20` currently justifies the `transport` field as: *"Whether anyone uses the WiFi
connection, which is buggy and expensive to maintain."*

That justification expires with this change — the field becomes a constant `usb` and answers nothing.
The field still ships (it costs nothing and a re-enable would want it), but the stated reason must be
rewritten honestly. Use exactly:

> Which transport the app connected over. The app is USB-only, so this is always `usb` today; the
> field stays so a future transport can be measured.

**Open item for the human, not a blocker:** the `transport` field was built specifically to answer
the question this spec decides. If the collected pings have not been looked at, that is a one-query
check worth doing before implementing. The design does not depend on the answer.

### 5. Documentation

| File | Change |
|---|---|
| `CLAUDE.md:21-25` | The `Sonulab.Transport.Wifi` architecture bullet keeps its description but replaces "USB stays #1, WiFi is the auto fallback via `DeviceSession`" with: not wired into the app; reachable via HwCheck only. |
| `CLAUDE.md:51-54` | The "USB→WiFi fallback" gotcha becomes a "USB only" gotcha: the transport exists and works, but the app does not offer it. Keep the mDNS/TCP-8080 detail — it is still true of HwCheck. |
| `CLAUDE.md:50` | "VoidX-Control must be CLOSED" — drop the implicit WiFi escape hatch; it is now unconditional. |
| `CLAUDE.md:65-67` | The "On WiFi detection is best-effort" caveat stays but is scoped to HwCheck. |
| `README.md:23-25` | Delete the "falls back to WiFi automatically" paragraph. Replace with one sentence: USB only. Line 25's "still buggy" editorial goes with it — it is now the reason, not a caveat. |
| `README.md:103`, `:124` | Protocol/telemetry mentions of WiFi corrected to match. |
| `PRIVACY.md:20` | Rewritten per §4. |
| `docs/HARDWARE-VALIDATION-wifi.md` | Header note: exercises a path the app no longer takes; HwCheck only. Bench results retained as a protocol record. |
| `docs/HARDWARE-VALIDATION-disconnect.md` check 4 | Same header note. Its "known limitation" paragraph stays accurate and is now the *reason* WiFi was unwired, not merely a caveat. |

`README.md:56` ("Bluetooth, USB, WiFi across a plethora of platforms") describes VoidX-Control's
scope, not NAMager's — **leave it alone**.

### 6. Tests

- **Update:** `tests/Namager.App.Tests/ConnectionViewModelTests.cs:70` asserts the old
  `"Disconnected (no device found on USB or WiFi)"` string verbatim. Move it to the new wording.
- **Add:** one test that the app's provider list contains exactly one provider, named `USB`, so a
  future edit cannot silently re-add the fallback. A test asserting only the disconnected-string
  would not catch a re-added provider, so this needs a seam: `MainWindowViewModel` currently builds
  the list inline in its constructor with no way to observe it. Extract it to

  ```csharp
  internal static IReadOnlyList<ILinkProvider> BuildProviders(SerialLinkOptions options)
  ```

  called from the constructor, and assert on that. The extraction is a pure move of
  `MainWindowViewModel.cs:101-110` — no behavior change.

  `Namager.App` has **no** `InternalsVisibleTo` today, so this also needs
  `<InternalsVisibleTo Include="Namager.App.Tests" />` added to `src/Namager.App/Namager.App.csproj`.
  That follows the pattern already used by `src/Sonulab.Core/Sonulab.Core.csproj:18`
  (`Namager.Tone3000` uses the equivalent `AssemblyInfo.cs` attribute form — either is idiomatic here;
  match `Sonulab.Core`'s csproj form since the app project has no `AssemblyInfo.cs`).
- **Delete:** nothing.

## Out of scope

- Deleting `src/Sonulab.Transport.Wifi` or its tests.
- Removing `HwCheck --wifi`.
- Changing `SonuClient`'s `readRetryAttempts` default (a separate question; the budget is inert on
  serial, and touching it changes behavior on the transport being kept).
- Removing the `transport` telemetry field or its `wifi` branch.
- Any BLE work.

## Verification

`dotnet build` and `dotnet test` — all **760** existing tests must stay green, plus the one new
provider-list test, for a final **761 passing, 0 failing**. That includes all 29 WiFi tests, which
are unaffected by unwiring: the transport is still built and still tested, it is merely unreachable
from the app.

No new hardware validation is required: the change removes a code path rather than adding one. The
existing `docs/HARDWARE-VALIDATION-disconnect.md` checks 1–3 (USB) remain the relevant on-device
checks and are still pending from the previous cycle.
