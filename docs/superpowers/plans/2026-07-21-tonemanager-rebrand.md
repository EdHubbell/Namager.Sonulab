# ToneManager Rebrand Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the app from StompStationManager to ToneManager — product/app-level projects, branding, GitHub/feedback/config plumbing — while leaving genuinely Sonulab-vendor-specific code namespaced `Sonulab.*`.

**Architecture:** Purely mechanical rename + string edits; no logic changes. Product/app-level projects (`App`, `Tone3000`, `Installer`, and their test projects) move to `ToneManager.*`; vendor device projects (`Core`, `Distill`, `Transport.Wifi`) stay `Sonulab.*`. The existing build + 312-test suite is the safety net — every structural task ends with `dotnet build` succeeding and `dotnet test` reporting `Failed: 0`.

**Tech Stack:** .NET 10, Avalonia 12, xUnit, WiX Toolset 6, Cloudflare Workers (JS), Git.

**Design spec:** `docs/superpowers/specs/2026-07-21-tonemanager-rebrand-design.md`

## Global Constraints

- **Display/identifier name is `ToneManager`** (no space) — everywhere: UI text, folders, repo, worker, namespaces.
- **Only rename these three project strings** — never a blanket `Sonulab` → `ToneManager`: `Sonulab.App` → `ToneManager.App`, `Sonulab.Tone3000` → `ToneManager.Tone3000`, `Sonulab.Installer` → `ToneManager.Installer`.
- **Keep unchanged** (vendor code): the `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi` projects/namespaces and their three test projects.
- **Do NOT touch:** the installer `UpgradeCode` GUID `1431D009-A559-46D0-9568-BD9675EFC753`; the device identifiers `stompstation1` and "StompStation" device-model references (incl. `FakePresetDevice`); `tests/Sonulab.Distill.Tests/goldens-corpus/*.nam.path.txt`; any csproj `<Version>`; runtime logic.
- **Green gate for every structural task:** `dotnet build` succeeds; `dotnet test` reports `Failed: 0` (expected 312 passed).
- **Feedback worker + GitHub repo/PAT are manual, operator-only steps** (Task 9) — code changes are inert until then.
- **Repo-wide replaces run in Git Bash** with GNU `sed`; always exclude `bin`/`obj`.

---

### Task 1: Rename Sonulab.Tone3000 → ToneManager.Tone3000

Renames the Tone3000 integration project (Tone3000.com is a third-party marketplace, not a Sonulab vendor concern) plus its test project, and renames the solution file. Consumers updated in the same task so the build stays green: `Sonulab.App` (ProjectReference + `using`), `tools/T3kProbe`, and the `.slnx` project path.

**Files:**
- Rename: `src/Sonulab.Tone3000/` → `src/ToneManager.Tone3000/` (+ `.csproj`)
- Rename: `tests/Sonulab.Tone3000.Tests/` → `tests/ToneManager.Tone3000.Tests/` (+ `.csproj`)
- Rename: `Sonulab.slnx` → `ToneManager.slnx`
- Modify (string replace): every `*.cs`/`*.csproj`/`*.slnx` under `src`, `tests`, `tools` containing `Sonulab.Tone3000` (App's `using`/ProjectReference, T3kProbe's `using`/ProjectReference, the test project, the `.slnx` paths)

**Interfaces:**
- Produces: the `ToneManager.Tone3000` namespace and `ToneManager.Tone3000.csproj`; `ToneManager.slnx` as the sole solution file. Later tasks reference files at `src/ToneManager.Tone3000/…`.

- [ ] **Step 1: Move the folders/files and rename the solution**

```bash
git mv Sonulab.slnx ToneManager.slnx
git mv src/Sonulab.Tone3000 src/ToneManager.Tone3000
git mv src/ToneManager.Tone3000/Sonulab.Tone3000.csproj src/ToneManager.Tone3000/ToneManager.Tone3000.csproj
git mv tests/Sonulab.Tone3000.Tests tests/ToneManager.Tone3000.Tests
git mv tests/ToneManager.Tone3000.Tests/Sonulab.Tone3000.Tests.csproj tests/ToneManager.Tone3000.Tests/ToneManager.Tone3000.Tests.csproj
```

- [ ] **Step 2: Replace the `Sonulab.Tone3000` string across sources**

```bash
grep -rlZ 'Sonulab\.Tone3000' --include='*.cs' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools ToneManager.slnx \
  | xargs -0 sed -i 's/Sonulab\.Tone3000/ToneManager.Tone3000/g'
```

- [ ] **Step 3: Verify no stragglers remain**

Run:
```bash
grep -rn 'Sonulab\.Tone3000' --include='*.cs' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools ToneManager.slnx
```
Expected: (no output — nothing matches)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Test**

Run: `dotnet test`
Expected: `Failed: 0` (312 passed).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename Sonulab.Tone3000 -> ToneManager.Tone3000

Tone3000.com integration is product-level, not Sonulab-vendor code.
Renames the project, its tests, and the solution file; updates all
consumers (App, T3kProbe, .slnx). No behavior change."
```

---

### Task 2: Rename Sonulab.App → ToneManager.App

Renames the Avalonia app shell (the product itself) and its test project. The assembly/output becomes `ToneManager.App.exe`. Because the `Sonulab.App` string is unique (it is not a prefix of any kept `Sonulab.Core/Distill/Transport.Wifi` name), a single scoped replace safely updates namespaces, `using`s, XAML `x:Class`/`xmlns`, the embedded-resource `LogicalName`, and the `LabelService` error-message string. The installer is **not** in the solution, so it is untouched here (handled in Task 3).

**Files:**
- Rename: `src/Sonulab.App/` → `src/ToneManager.App/` (+ `.csproj`)
- Rename: `tests/Sonulab.App.Tests/` → `tests/ToneManager.App.Tests/` (+ `.csproj`)
- Modify (string replace): every `*.cs`/`*.axaml`/`*.csproj`/`*.slnx` under `src`, `tests`, `tools` containing `Sonulab.App`

**Interfaces:**
- Consumes: `ToneManager.slnx` (from Task 1).
- Produces: the `ToneManager.App` namespace, `ToneManager.App.csproj`, and the `ToneManager.App.exe` assembly name (used by Task 3's installer).

- [ ] **Step 1: Move the folders/files**

```bash
git mv src/Sonulab.App src/ToneManager.App
git mv src/ToneManager.App/Sonulab.App.csproj src/ToneManager.App/ToneManager.App.csproj
git mv tests/Sonulab.App.Tests tests/ToneManager.App.Tests
git mv tests/ToneManager.App.Tests/Sonulab.App.Tests.csproj tests/ToneManager.App.Tests/ToneManager.App.Tests.csproj
```

- [ ] **Step 2: Replace the `Sonulab.App` string across sources**

```bash
grep -rlZ 'Sonulab\.App' --include='*.cs' --include='*.axaml' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools ToneManager.slnx \
  | xargs -0 sed -i 's/Sonulab\.App/ToneManager.App/g'
```

This covers: all `namespace`/`using Sonulab.App[.*]`; `x:Class="Sonulab.App…"` and `xmlns:…="clr-namespace:Sonulab.App…"` in `App.axaml` + `Views/*.axaml`; the `<LogicalName>Sonulab.App.labels.en.json</LogicalName>` in the csproj; the `"check Sonulab.App.csproj"` error string in `Services/LabelService.cs`; the `.slnx` App paths; and the `ProjectReference` in `ToneManager.App.Tests.csproj`.

- [ ] **Step 3: Verify no stragglers remain**

Run:
```bash
grep -rn 'Sonulab\.App' --include='*.cs' --include='*.axaml' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools ToneManager.slnx
```
Expected: (no output)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors. (A stale-generated-XAML error means the earlier `bin`/`obj` weren't cleaned — run `dotnet clean` then rebuild.)

- [ ] **Step 5: Test**

Run: `dotnet test`
Expected: `Failed: 0` (312 passed). The `LabelService` tests passing here confirm the embedded labels resource still resolves under the new assembly name.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename Sonulab.App -> ToneManager.App

Renames the Avalonia app shell + its test project; assembly is now
ToneManager.App.exe. Updates namespaces, XAML x:Class/xmlns, the
embedded labels LogicalName, and .slnx paths. No behavior change."
```

---

### Task 3: Rename & rebrand the installer

Renames `Sonulab.Installer` → `ToneManager.Installer` and updates every StompStation/Sonulab reference inside the WiX project so packaging produces a "ToneManager" MSI. The installer is not in the solution, so it has its own verification (a WiX build), not `dotnet test`. **Keep the `UpgradeCode` GUID unchanged.**

**Files:**
- Rename: `src/Sonulab.Installer/` → `src/ToneManager.Installer/` (+ `.wixproj`)
- Modify: `src/ToneManager.Installer/Sonulab.Installer.wixproj` → `ToneManager.Installer.wixproj` — `PublishDir` (`..\Sonulab.App\…` → `..\ToneManager.App\…`) and `OutputName` (`StompStationManager-…` → `ToneManager-…`)
- Modify: `src/ToneManager.Installer/Package.wxs` — `Package Name`, `MajorUpgrade DowngradeErrorMessage`, shortcut `Name`, shortcut `Target` (`Sonulab.App.exe` → `ToneManager.App.exe`), `INSTALLFOLDER` `Name`, `RegistryValue Key` (`Software\StompStationManager` → `Software\ToneManager`), `ARPURLINFOABOUT`, `Icon SourceFile` (`..\Sonulab.App\…` → `..\ToneManager.App\…`), and the two header comments

- [ ] **Step 1: Move the folder and project file**

```bash
git mv src/Sonulab.Installer src/ToneManager.Installer
git mv src/ToneManager.Installer/Sonulab.Installer.wixproj src/ToneManager.Installer/ToneManager.Installer.wixproj
```

- [ ] **Step 2: Update the `.wixproj`**

In `src/ToneManager.Installer/ToneManager.Installer.wixproj`:
- Line 1 comment: `Deliberately NOT in Sonulab.slnx` → `Deliberately NOT in ToneManager.slnx`
- `PublishDir` default: `$(MSBuildProjectDirectory)\..\Sonulab.App\bin\Release\net10.0\win-x64\publish` → `…\..\ToneManager.App\bin\Release\net10.0\win-x64\publish`
- `<OutputName>StompStationManager-$(ProductVersion)-x64</OutputName>` → `<OutputName>ToneManager-$(ProductVersion)-x64</OutputName>`

- [ ] **Step 3: Update `Package.wxs`**

Apply these exact edits in `src/ToneManager.Installer/Package.wxs`:
- Header comment: `%LOCALAPPDATA%\Programs\StompStationManager` → `…\ToneManager`
- `Package Name="StompStation Manager"` → `Package Name="ToneManager"`
- `DowngradeErrorMessage="A newer version of StompStation Manager is already installed."` → `"A newer version of ToneManager is already installed."`
- `<Icon Id="AppIcon" SourceFile="..\Sonulab.App\Assets\app-icon.ico" />` → `SourceFile="..\ToneManager.App\Assets\app-icon.ico"`
- `Property Id="ARPURLINFOABOUT" Value="https://github.com/EdHubbell/StompStationManager"` → `…/EdHubbell/ToneManager`
- `<Directory Id="INSTALLFOLDER" Name="StompStationManager">` → `Name="ToneManager"`
- `Shortcut … Name="StompStation Manager"` → `Name="ToneManager"`
- `Target="[INSTALLFOLDER]Sonulab.App.exe"` → `Target="[INSTALLFOLDER]ToneManager.App.exe"`
- `RegistryValue Root="HKCU" Key="Software\StompStationManager"` → `Key="Software\ToneManager"`
- **Leave `UpgradeCode="1431D009-A559-46D0-9568-BD9675EFC753"` exactly as-is.**

- [ ] **Step 4: Verify no old names remain in the installer**

Run:
```bash
grep -rn 'Sonulab\.App\|StompStation' src/ToneManager.Installer
```
Expected: (no output)

- [ ] **Step 5: Build the installer (verifies the WiX project + paths)**

First publish the app the installer harvests, then build the package:
```bash
dotnet publish src/ToneManager.App -c Release -r win-x64
dotnet build src/ToneManager.Installer/ToneManager.Installer.wixproj -c Release
```
Expected: `Build succeeded`; an MSI named `ToneManager-1.0.0-x64.msi` is produced under the installer's `bin`. (If `dotnet publish` flags are unfamiliar, the only goal is that `PublishDir` exists so WiX harvests files; a prior Release publish is fine.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename & rebrand installer to ToneManager

Renames Sonulab.Installer -> ToneManager.Installer; updates PublishDir,
OutputName, product/shortcut names, install folder, HKCU key, and ARP
URL. UpgradeCode GUID unchanged (in-place upgrades preserved)."
```

---

### Task 4: Rebrand in-app user-facing text

Two runtime strings a user sees. No automated test asserts them; verification is build + a visual check.

**Files:**
- Modify: `src/ToneManager.App/Views/MainWindow.axaml.cs:14`
- Modify: `src/ToneManager.Tone3000/T3kAuth.cs:95`

- [ ] **Step 1: Update the window title**

In `src/ToneManager.App/Views/MainWindow.axaml.cs`, change:
```csharp
Title = $"StompStation Manager v{AppInfo.Version}";
```
to:
```csharp
Title = $"ToneManager v{AppInfo.Version}";
```

- [ ] **Step 2: Update the OAuth success page**

In `src/ToneManager.Tone3000/T3kAuth.cs`, in the success-page HTML string, change `return to StompStation Manager.` to `return to ToneManager.`

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: rebrand in-app text to ToneManager

Window title and Tone3000 OAuth success page now read ToneManager."
```

---

### Task 5: Point the update-check at the new repo (TDD)

`UpdateCheckService` polls the GitHub releases API. Retarget it at `EdHubbell/ToneManager`. This is the one change with a real test to flip first.

**Files:**
- Test: `tests/ToneManager.App.Tests/UpdateCheckServiceTests.cs:29`
- Modify: `src/ToneManager.App/Services/UpdateCheckService.cs:19,28`

**Interfaces:**
- Consumes: `UpdateCheckService` (unchanged signature).

- [ ] **Step 1: Update the test expectation to the new repo (RED)**

In `tests/ToneManager.App.Tests/UpdateCheckServiceTests.cs`, change the fixture/assert URL:
```csharp
"""{"tag_name":"v2.5.0","html_url":"https://github.com/EdHubbell/ToneManager/releases/tag/v2.5.0"}""";
```
(If the same test also asserts the request URL/host, update that expectation to `EdHubbell/ToneManager` too.)

- [ ] **Step 2: Run the test — expect failure**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckServiceTests`
Expected: FAIL — the service still requests/returns the `StompStationManager` URL.

- [ ] **Step 3: Update the service (GREEN)**

In `src/ToneManager.App/Services/UpdateCheckService.cs`:
- Releases URL constant: `https://api.github.com/repos/EdHubbell/StompStationManager/releases/latest` → `…/EdHubbell/ToneManager/releases/latest`
- User agent: `_http.DefaultRequestHeaders.UserAgent.ParseAdd("StompStationManager");` → `ParseAdd("ToneManager");`

- [ ] **Step 4: Run the test — expect pass**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckServiceTests`
Expected: `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test`
Expected: `Failed: 0` (312 passed).
```bash
git add -A
git commit -m "feat: point update-check at EdHubbell/ToneManager"
```

---

### Task 6: Rename the feedback worker and endpoint

Renames the Cloudflare worker (new endpoint URL) and retargets it at the new repo. Worker deployment itself is a manual step (Task 9); this task only edits code/config. No automated test asserts the endpoint URL, so verification is build + a `grep` sweep.

**Files:**
- Modify: `infra/feedback-worker/wrangler.toml:1`
- Modify: `infra/feedback-worker/worker.js:1-3,5,60`
- Modify: `src/ToneManager.App/Services/FeedbackService.cs:25`
- Modify: `infra/feedback-worker/README.md` (repo name, PAT scoping, `gh label` command, example URLs)

- [ ] **Step 1: Update `wrangler.toml`**

`name = "stompstation-feedback"` → `name = "tonemanager-feedback"`

- [ ] **Step 2: Update `worker.js`**

- Header comment lines 1–3: `StompStationManager` → `ToneManager` (both occurrences, incl. the PAT-scoping note)
- `const REPO = 'EdHubbell/StompStationManager';` → `const REPO = 'EdHubbell/ToneManager';`
- `'user-agent': 'stompstation-feedback-worker',` → `'user-agent': 'tonemanager-feedback-worker',`

- [ ] **Step 3: Update the app endpoint**

In `src/ToneManager.App/Services/FeedbackService.cs`:
```csharp
public const string EndpointUrl = "https://stompstation-feedback.ed-eed.workers.dev/";
```
→
```csharp
public const string EndpointUrl = "https://tonemanager-feedback.ed-eed.workers.dev/";
```
(The account subdomain `ed-eed` is assumed unchanged; Task 9 reconciles this against the actual `wrangler deploy` output.)

- [ ] **Step 4: Update the worker README**

In `infra/feedback-worker/README.md`, replace `EdHubbell/StompStationManager` → `EdHubbell/ToneManager`, `StompStationManager` (PAT repo-access) → `ToneManager`, and `stompstation-feedback` → `tonemanager-feedback` in the deploy/rate-limit/URL examples.

- [ ] **Step 5: Verify + build**

Run:
```bash
grep -rn 'stompstation\|StompStation' infra/feedback-worker
dotnet build
```
Expected: grep prints nothing; `Build succeeded`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: rename feedback worker to tonemanager-feedback, retarget repo

worker.js REPO + user-agent, wrangler name, app EndpointUrl, and README
all point at ToneManager / tonemanager-feedback. Deploy is a manual step."
```

---

### Task 7: Move the config path to %APPDATA%\ToneManager

The Tone3000 token and keys live under `%APPDATA%\StompStationManager`. Point them at `ToneManager`. No migration (users re-authenticate once — a design decision). No automated test asserts the path segment.

**Files:**
- Modify: `src/ToneManager.Tone3000/T3kConfig.cs:14`
- Modify: `src/ToneManager.Tone3000/T3kTokenStore.cs:14`
- Modify: `tools/T3kProbe/Program.cs:2` (comment)

- [ ] **Step 1: Update the path segments**

In `src/ToneManager.Tone3000/T3kConfig.cs` and `src/ToneManager.Tone3000/T3kTokenStore.cs`, change the `"StompStationManager"` path segment to `"ToneManager"` (one occurrence each).

- [ ] **Step 2: Update the T3kProbe comment**

In `tools/T3kProbe/Program.cs`, change the comment mentioning `%APPDATA%\StompStationManager\tone3000.json` to `%APPDATA%\ToneManager\tone3000.json`.

- [ ] **Step 3: Verify + build + test**

Run:
```bash
grep -rn 'StompStationManager' src tools --include='*.cs'
dotnet build && dotnet test
```
Expected: grep prints nothing; `Build succeeded`; `Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: move config dir to %APPDATA%\\ToneManager

Tone3000 token + keys now live under ToneManager. No migration; users
re-authenticate once (per design)."
```

---

### Task 8: Rebrand docs & project metadata

Pure documentation. Updates `README`, the `CLAUDE.md` architecture description (renaming the moved projects while keeping `Sonulab.Core/Distill/Transport.Wifi`), and product-name prose in `docs/`. Also removes the stale dev shortcut. No build impact.

**Files:**
- Modify: `README.md` (title/prose + rename note)
- Modify: `CLAUDE.md` (header line + `src/Sonulab.App` → `src/ToneManager.App`, `Sonulab.Tone3000` → `ToneManager.Tone3000`; leave `Sonulab.Core/Distill/Transport.Wifi`)
- Modify: `docs/**` product-name prose (where it reads as the current product)
- Delete: `Sonulab.App.exe - Shortcut.lnk` (untracked stale dev shortcut in repo root)

- [ ] **Step 1: Update README**

Change the product title/prose to `ToneManager`, and add a one-line note: `> Renamed from StompStationManager on 2026-07-21.`

- [ ] **Step 2: Update CLAUDE.md**

- Header: `# CLAUDE.md — StompStation Manager` → `# CLAUDE.md — ToneManager`
- Intro sentence: replace the "Desktop app … to manage a **Sonulab StompStation**" product-name references with ToneManager (keep the *device* name "Sonulab StompStation" where it names the hardware).
- Architecture bullets: `src/Sonulab.App` → `src/ToneManager.App`, `Sonulab.Tone3000` → `ToneManager.Tone3000`. **Leave `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi`** (still their real names). Update the `%APPDATA%\StompStationManager` mention to `ToneManager`.

- [ ] **Step 3: Sweep docs prose**

Run:
```bash
grep -rln 'StompStation Manager\|StompStationManager' docs README.md
```
For each hit that names the *product* (not the device model / not a historical spec filename), update to ToneManager. Historical plan/spec bodies describing past work may be left; prioritize files a reader uses as current reference (`README.md`, `CLAUDE.md`, `PROTOCOL.md`, `HARDWARE-VALIDATION-*`).

- [ ] **Step 4: Remove the stale shortcut**

```bash
rm -f "Sonulab.App.exe - Shortcut.lnk"
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: rebrand README/CLAUDE/docs to ToneManager

Product-name prose + moved-project references updated; vendor projects
(Sonulab.Core/Distill/Transport.Wifi) kept. Records the 2026-07-21 rename."
```

---

### Task 9: Repo migration & manual deploy (operator gates)

Operational steps that finish the transition. The `git` steps can run once the empty `EdHubbell/ToneManager` repo exists; the Cloudflare/GitHub steps are manual and require your credentials. **Do the GitHub repo creation and PAT steps yourself — they are not automatable here.**

**Prerequisites (manual, you):**
- Create the empty `EdHubbell/ToneManager` repo on GitHub.
- Create a fine-grained PAT scoped to `ToneManager`, **Issues: Read/Write only**.

- [ ] **Step 1: Repoint origin and push history**

```bash
git remote set-url origin git@github.com:EdHubbell/ToneManager.git
git push origin --all
git push origin --tags
```
Expected: all branches/tags land in the new repo; `git remote -v` shows `ToneManager.git`.

- [ ] **Step 2: Create the feedback label (manual)**

```bash
gh label create user-feedback --repo EdHubbell/ToneManager --color F9D0C4 \
  --description "Submitted from the app's Send Feedback dialog"
```

- [ ] **Step 3: Deploy the worker (manual)**

```bash
cd infra/feedback-worker
wrangler secret put GITHUB_TOKEN   # paste the new ToneManager-scoped PAT
wrangler deploy
```
Note the printed URL. **If it is not `https://tonemanager-feedback.ed-eed.workers.dev/`,** update `FeedbackService.EndpointUrl` (Task 6, Step 3) to match, rebuild, and commit the fix.

- [ ] **Step 4: End-to-end feedback verification (manual)**

Run the app (`dotnet run --project src/ToneManager.App`), open Send Feedback, submit a test message, and confirm a new issue appears in `EdHubbell/ToneManager` with the `user-feedback` label.

- [ ] **Step 5: Post-rename smoke (manual)**

Launch the app; confirm the title reads `ToneManager v…`, the preset/amp/IR tabs load (labels resource resolves), and a Tone3000 sign-in writes to `%APPDATA%\ToneManager`.

- [ ] **Step 6: Release note**

In the next GitHub Release description, add: "Renamed StompStationManager → ToneManager on 2026-07-21; version numbering continues unchanged."

---

## Notes for the executor

- Run everything from the repo root in Git Bash unless a step says otherwise.
- If a `dotnet build` fails right after a rename with a XAML/`x:Class` mismatch, run `dotnet clean` (stale `obj/*.g.cs` from the old namespace) and rebuild before investigating further.
- Tasks 1–3 must run in order (each depends on the previous project/namespace state). Tasks 4–8 are independent of each other and can run in any order after Task 2 (and Task 6/7 after Task 1). Task 9 is last.
