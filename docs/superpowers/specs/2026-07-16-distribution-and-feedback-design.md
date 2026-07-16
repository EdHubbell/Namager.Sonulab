# Distribution & Feedback — Design Spec

**Date:** 2026-07-16
**Status:** Approved for planning

## Goal

Get StompStationManager into real users' hands and collect their feedback:

1. A downloadable **.msi installer** published on GitHub Releases, built automatically by CI on tag push.
2. A proper **application icon** (guitar pedal with a lightning bolt) replacing the stock Avalonia icon.
3. An in-app **Send Feedback** button that creates a GitHub issue on `EdHubbell/StompStationManager` (public repo, issues enabled) without users needing a GitHub account.
4. A lightweight **update check** so testers learn when a new version ships.

Out of scope: code signing (accepted SmartScreen warning, documented), auto-updating/self-patching, arm64 builds, telemetry.

## 1. Release pipeline (.msi on GitHub Releases)

### Versioning

- The version comes from the git tag: pushing `v1.2.3` produces app + installer version `1.2.3`.
- CI passes `-p:Version=<tag>` to build/publish; no `Version` property is hardcoded in csproj (local dev builds stay `1.0.0-dev` via a `Version` default of `1.0.0-dev` in `Sonulab.App.csproj`).
- The main window title shows the version: `StompStation Manager v1.2.3` (from `Assembly` informational version; the `-dev` suffix visible in local builds is intentional).

### Installer project

- New `src/Sonulab.Installer/` — a **WiX v6** project (`WixToolset.Sdk` MSBuild SDK) so it builds with `dotnet build` both locally and in CI. It is **not** added to `Sonulab.slnx` default build (packaging shouldn't run on every `dotnet build` of the solution); CI and a doc'd command build it explicitly.
- Input: the output of `dotnet publish src/Sonulab.App -c Release -r win-x64 --self-contained true` (framework bundled — users do not need .NET 10).
- Uses WiX `Files Include="...\publish\**"` wildcard harvesting so new app files never require installer edits.
- **Per-user install** (`Scope="perUser"`): no admin/UAC prompt, installs to `%LOCALAPPDATA%\Programs\StompStationManager`.
- Start Menu shortcut "StompStation Manager"; app icon on shortcut, exe, and Add/Remove Programs entry.
- Stable `UpgradeCode` GUID + `MajorUpgrade` element: installing a newer .msi over an older one upgrades in place; downgrades blocked with a friendly message.
- Output name: `StompStationManager-<version>-x64.msi`.

### CI workflow

- New `.github/workflows/release.yml`, triggered by tag push matching `v*`.
- Runner: `windows-latest`. Steps:
  1. Checkout; setup .NET 10.
  2. `dotnet test` (full suite) — release gate; any failure aborts the release.
  3. `dotnet publish` Sonulab.App (Release, win-x64, self-contained, `-p:Version=<tag without v>`).
  4. `dotnet build src/Sonulab.Installer -c Release` (same version property) → .msi.
  5. Create the GitHub Release for the tag (auto-generated notes) and attach the .msi.
- Also a new `.github/workflows/ci.yml` (build + test on push/PR to main) if one doesn't exist — the release gate shouldn't be the first time CI runs the suite. *(Verified: no workflows exist today.)*

### Known limitation (accepted)

The .msi is unsigned → SmartScreen shows "Windows protected your PC" on first run. README and release notes include the "More info → Run anyway" instruction. Code signing is a possible later purchase, not part of this work.

## 2. Application icon

- Motif: simple guitar pedal (rounded-rect body, two knobs, footswitch) with a lightning bolt through it. Style matches the app's Studio-warm palette; must read clearly at 16×16.
- Deliverables committed to the repo:
  - `src/Sonulab.App/Assets/app-icon.svg` — master.
  - `src/Sonulab.App/Assets/app-icon.ico` — multi-resolution (16, 24, 32, 48, 64, 128, 256), generated from the SVG by a one-time script (`tools/icon/make-ico.ps1`, committed for regeneration).
- Wired in three places:
  - `ApplicationIcon` in `Sonulab.App.csproj` (exe icon).
  - `Icon` on `MainWindow` (window/taskbar icon), replacing `avalonia-logo.ico` (which is deleted).
  - Installer: shortcut + ARP (`Add/Remove Programs`) icon.
- **Approval gate:** 2–3 rendered variants are shown to Ed before wiring in; the chosen one becomes the master. Icon look is Ed-approved, not review-agent-approved.

## 3. Feedback → GitHub issue

### UX

- A **Send Feedback** item docked at the bottom of the left nav pane (below the nav `ListBox` in `MainWindow.axaml`), with a PathIcon (new `Icon.Feedback` geometry in `Icons.axaml`) — matches the Fluent dashboard style.
- Clicking opens a modal dialog (`FeedbackDialog`):
  - Fields: **Name** (required), **Email** (required, format-validated), **Message** (required, multiline). Caps: name 100, email 200, message 4000 chars.
  - Disclosure text in the dialog: *"Your feedback — including your name and email — will be posted as a public GitHub issue."* (Ed accepted including the email publicly so he can reply directly.)
  - Send button disabled until valid; while sending shows progress; on success shows "Thanks — feedback sent!" and closes; on failure shows an inline error ("Couldn't send feedback — check your connection and try again") **without losing the typed text**.
  - App version and OS description are appended automatically (shown in the dialog as fine print so the user knows).

### App plumbing

- `FeedbackViewModel` (CommunityToolkit.Mvvm) — validation, state machine (Editing → Sending → Sent/Failed), unit-tested against a fake service.
- `IFeedbackService` / `FeedbackService` in `src/Sonulab.App/Services`: POSTs JSON `{ name, email, message, appVersion, os, website }` to the worker endpoint over HTTPS. 15-second timeout. `website` is the honeypot field — always sent empty by the real app.
- The worker URL is a plain compile-time constant (it is not a secret).

### Cloudflare Worker (`infra/feedback-worker/` in the repo)

- ~50-line JS worker + `wrangler.toml`. Behavior:
  - Accepts only `POST` with `Content-Type: application/json`.
  - Rejects: missing/oversized fields (same caps as the app), non-empty honeypot `website` field, malformed email.
  - Creates the issue via `POST /repos/EdHubbell/StompStationManager/issues` using a **fine-grained PAT** stored as a worker secret (`GITHUB_TOKEN`), scoped to this single repo with only Issues read/write.
  - Issue format — title: `Feedback: <first 60 chars of message>`; body: message, then a metadata block (name, email, app version, OS); label: `user-feedback`.
  - Returns 201 on success, 4xx with a terse reason otherwise; never echoes the token.
- **Rate limiting:** worker enforces a best-effort per-IP throttle, plus a documented Cloudflare dashboard rate-limiting rule (free tier) on the route as the real backstop.
- **One-time setup by Ed** (documented in `infra/feedback-worker/README.md`): create the fine-grained PAT, `wrangler deploy`, `wrangler secret put GITHUB_TOKEN`, create the `user-feedback` label, add the dashboard rate rule. The worker is *not* deployed by CI.

## 4. In-app update check

- `IUpdateCheckService` / `UpdateCheckService` in `src/Sonulab.App/Services`:
  - On startup (fire-and-forget, after the window shows), GET `https://api.github.com/repos/EdHubbell/StompStationManager/releases/latest` (unauthenticated; 60 req/hr/IP is ample).
  - Parses `tag_name`, compares to the running assembly version with `System.Version` semantics (a `-dev` local build never prompts).
  - Any error (offline, rate-limited, malformed) is swallowed silently — the feature must never bother the user or block startup.
- If newer: a dismissible banner under the top connection bar: *"Version X.Y.Z is available — Download"*; the link opens the release page in the default browser. Dismiss lasts for the session (no persisted state).
- Unit tests: version comparison table (older/equal/newer/prerelease/dev/malformed), service behavior against a fake HTTP handler, banner visibility on `MainWindowViewModel`.

## Component summary

| Unit | Purpose | Depends on |
|---|---|---|
| `.github/workflows/ci.yml` | build+test on push/PR | — |
| `.github/workflows/release.yml` | tag → test → publish → .msi → Release | Installer project |
| `src/Sonulab.Installer` | WiX v6 .msi from publish output | publish output |
| `Assets/app-icon.{svg,ico}` + `tools/icon/make-ico.ps1` | app icon | — |
| `FeedbackViewModel` + `FeedbackDialog` | feedback UX | `IFeedbackService` |
| `FeedbackService` | POST to worker | worker URL |
| `infra/feedback-worker` | create GitHub issue | `GITHUB_TOKEN` secret (Ed-deployed) |
| `UpdateCheckService` + banner | new-version notice | GitHub Releases API |

## Testing strategy

- ViewModels and services: xUnit against fakes (existing pattern) — no network in tests.
- Worker: exercised manually with `curl` against a `wrangler dev` instance (happy path, honeypot, oversize, bad email); it's ~50 lines of validation glue, not unit-test infrastructure.
- Installer/CI: validated end-to-end by tagging a prerelease (e.g. `v0.9.0`) and checking the Release, install, upgrade-over-install, and uninstall on Ed's machine. Checklist: `docs/HARDWARE-VALIDATION-installer.md` (install/upgrade/uninstall/shortcut/icon/SmartScreen wording).
- Feedback + update check live checks ride the same checklist (send real feedback → issue appears; fake old version → banner appears).

## Error handling summary

- Feedback send failure → inline retryable error, text preserved.
- Update check failure → silent no-op.
- Release workflow test failure → no release created.
- Worker validation failure → 4xx, app shows the generic failure message (no worker internals surfaced to users).

## README additions

Download/install section (link to latest Release, SmartScreen "More info → Run anyway" note, .NET not required), plus a note that the Feedback button posts a public GitHub issue.
