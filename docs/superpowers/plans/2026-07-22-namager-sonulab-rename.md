# NAMager for Sonulab Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the app from ToneManager to NAMager for Sonulab (repo `EdHubbell/Namager.Sonulab`) — app-layer projects to `Namager.*`, all branding/GitHub/feedback/config/installer plumbing — leaving Sonulab-vendor code namespaced `Sonulab.*`.

**Architecture:** Purely mechanical rename + string edits; no logic changes. App-layer projects (`App`, `Tone3000`, `Installer` + their tests) move `ToneManager.*` → `Namager.*` (NOT `Namager.Sonulab.*` — that namespace would shadow `Sonulab.*`). Human-readable text says **NAMager for Sonulab**; identifiers use **Namager**. The existing build + 471-test suite is the safety net — every structural task ends with `dotnet build` succeeding and `dotnet test` reporting `Failed: 0`.

**Tech Stack:** .NET 10, Avalonia 12, xUnit, WiX Toolset 6, Cloudflare Workers (JS), Git.

**Design spec:** `docs/superpowers/specs/2026-07-22-namager-sonulab-rename-design.md`

## Global Constraints

- **Casing:** `NAMager` in human-readable text (window title, installer UI, shortcut, OAuth page, log banner, README/docs prose, release notes). `Namager` in identifiers (repo, namespaces, projects, exe, `%APPDATA%\Namager`, registry key, worker name, User-Agent).
- **Display name is "NAMager for Sonulab"** wherever users see a product name.
- **Only replace these three project strings mechanically** — never a blanket `ToneManager` → `Namager` (it would clobber historical docs and human-facing text that needs `NAMager`): `ToneManager.App` → `Namager.App`, `ToneManager.Tone3000` → `Namager.Tone3000`, `ToneManager.Installer` → `Namager.Installer`. Every *bare* `ToneManager` occurrence is edited individually in Tasks 4–8.
- **Keep unchanged** (vendor code): `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi` projects/namespaces and their three test projects.
- **Do NOT touch:** the device identifiers `stompstation1` / "StompStation" device-model references (incl. `FakePresetDevice`); `tests/Sonulab.Distill.Tests/goldens-corpus/*.nam.path.txt`; any csproj `<Version>`; runtime logic.
- **Installer `UpgradeCode` CHANGES** (deliberate clean break, per spec): old `1431D009-A559-46D0-9568-BD9675EFC753` → new **`47160B31-CC00-4023-887A-13478CB150D9`**. The new GUID is then forever for this product.
- **Green gate for every structural task:** `dotnet build` succeeds; `dotnet test` reports `Failed: 0` (expected 471 passed).
- **Feedback worker + GitHub repo/PAT are manual, operator-only steps** (Task 9) — code changes are inert until then.
- **Repo-wide replaces run in Git Bash** with GNU `sed`; always exclude `bin`/`obj`.

---

### Task 1: Rename ToneManager.Tone3000 → Namager.Tone3000 (+ solution file)

Renames the Tone3000 integration project plus its test project, and renames the solution file to match the repo identity. Consumers updated in the same task so the build stays green: `ToneManager.App` (ProjectReference + `using`), `tools/T3kProbe`, and the `.slnx` project paths.

**Files:**
- Rename: `src/ToneManager.Tone3000/` → `src/Namager.Tone3000/` (+ `.csproj`)
- Rename: `tests/ToneManager.Tone3000.Tests/` → `tests/Namager.Tone3000.Tests/` (+ `.csproj`)
- Rename: `ToneManager.slnx` → `Namager.Sonulab.slnx`
- Modify (string replace): every `*.cs`/`*.csproj`/`*.slnx` under `src`, `tests`, `tools` containing `ToneManager.Tone3000`

**Interfaces:**
- Produces: the `Namager.Tone3000` namespace and `Namager.Tone3000.csproj`; `Namager.Sonulab.slnx` as the sole solution file. Later tasks reference files at `src/Namager.Tone3000/…`.

- [ ] **Step 1: Move the folders/files and rename the solution**

```bash
git mv ToneManager.slnx Namager.Sonulab.slnx
git mv src/ToneManager.Tone3000 src/Namager.Tone3000
git mv src/Namager.Tone3000/ToneManager.Tone3000.csproj src/Namager.Tone3000/Namager.Tone3000.csproj
git mv tests/ToneManager.Tone3000.Tests tests/Namager.Tone3000.Tests
git mv tests/Namager.Tone3000.Tests/ToneManager.Tone3000.Tests.csproj tests/Namager.Tone3000.Tests/Namager.Tone3000.Tests.csproj
```

- [ ] **Step 2: Replace the `ToneManager.Tone3000` string across sources**

```bash
grep -rlZ 'ToneManager\.Tone3000' --include='*.cs' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools Namager.Sonulab.slnx \
  | xargs -0 sed -i 's/ToneManager\.Tone3000/Namager.Tone3000/g'
```

This covers: `namespace ToneManager.Tone3000` in `T3k*.cs`; the App's `using` + `ProjectReference`; T3kProbe's `using`/`ProjectReference` + the `AssemblyInfo.cs` comment; the test project's namespace/references; the `.slnx` paths.

- [ ] **Step 3: Verify no stragglers remain**

Run:
```bash
grep -rn 'ToneManager\.Tone3000' --include='*.cs' --include='*.csproj' --include='*.slnx' \
  --exclude-dir=bin --exclude-dir=obj src tests tools Namager.Sonulab.slnx
```
Expected: (no output — nothing matches)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Test**

Run: `dotnet test`
Expected: `Failed: 0` (471 passed).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename ToneManager.Tone3000 -> Namager.Tone3000

Renames the project, its tests, and the solution file
(ToneManager.slnx -> Namager.Sonulab.slnx); updates all consumers
(App, T3kProbe, .slnx). No behavior change."
```

---

### Task 2: Rename ToneManager.App → Namager.App

Renames the Avalonia app shell (the product itself) and its test project. The assembly/output becomes `Namager.App.exe`. The `ToneManager.App` string is unique and safe for a single scoped replace: namespaces, `using`s, XAML `x:Class`/`xmlns`/`avares://` URLs, the embedded-resource `LogicalName`, the `LabelService`/`ParameterExposure` error strings, `app.manifest` assemblyIdentity, the release workflow's publish path, and the icon/art script paths. The sweep deliberately includes `*.wixproj`/`*.wxs` so the installer's `..\ToneManager.App\…` paths and `ToneManager.App.exe` targets update here too (the installer's own project rename is Task 3).

**Files:**
- Rename: `src/ToneManager.App/` → `src/Namager.App/` (+ `.csproj`)
- Rename: `tests/ToneManager.App.Tests/` → `tests/Namager.App.Tests/` (+ `.csproj`)
- Modify (string replace): every `*.cs`/`*.axaml`/`*.csproj`/`*.slnx`/`*.manifest`/`*.ps1`/`*.yml`/`*.wixproj`/`*.wxs` under `src`, `tests`, `tools`, `.github` containing `ToneManager.App`

**Interfaces:**
- Consumes: `Namager.Sonulab.slnx` (from Task 1).
- Produces: the `Namager.App` namespace, `Namager.App.csproj`, and the `Namager.App.exe` assembly name (used by Task 3's installer).

- [ ] **Step 1: Move the folders/files**

```bash
git mv src/ToneManager.App src/Namager.App
git mv src/Namager.App/ToneManager.App.csproj src/Namager.App/Namager.App.csproj
git mv tests/ToneManager.App.Tests tests/Namager.App.Tests
git mv tests/Namager.App.Tests/ToneManager.App.Tests.csproj tests/Namager.App.Tests/Namager.App.Tests.csproj
```

- [ ] **Step 2: Replace the `ToneManager.App` string across sources**

```bash
grep -rlZ 'ToneManager\.App' --include='*.cs' --include='*.axaml' --include='*.csproj' \
  --include='*.slnx' --include='*.manifest' --include='*.ps1' --include='*.yml' \
  --include='*.wixproj' --include='*.wxs' \
  --exclude-dir=bin --exclude-dir=obj src tests tools .github Namager.Sonulab.slnx \
  | xargs -0 sed -i 's/ToneManager\.App/Namager.App/g'
```

This covers: all `namespace`/`using ToneManager.App[.*]`; `x:Class="ToneManager.App…"`, `xmlns:…="using:ToneManager.App"` and `avares://ToneManager.App/…` in `App.axaml` + `Views/*.axaml`; the `<LogicalName>ToneManager.App.labels.en.json</LogicalName>`; the `"check ToneManager.App.csproj"` error strings in `Services/LabelService.cs` + `Services/ParameterExposure.cs`; `app.manifest` `assemblyIdentity name="ToneManager.App.Desktop"`; the `.slnx` paths; the test project's `ProjectReference`; `.github/workflows/release.yml` (`dotnet publish src/ToneManager.App`); `tools/icon/make-installer-art.ps1` + `make-ico.ps1` asset paths; and the installer's `PublishDir`/`Icon SourceFile`/shortcut `Target`/`ExeCommand` references to `ToneManager.App`.

- [ ] **Step 3: Verify no stragglers remain**

Run:
```bash
grep -rn 'ToneManager\.App' --include='*.cs' --include='*.axaml' --include='*.csproj' \
  --include='*.slnx' --include='*.manifest' --include='*.ps1' --include='*.yml' \
  --include='*.wixproj' --include='*.wxs' \
  --exclude-dir=bin --exclude-dir=obj src tests tools .github Namager.Sonulab.slnx
```
Expected: (no output)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors. (A stale-generated-XAML error means old `obj/*.g.cs` survived — run `dotnet clean` then rebuild.)

- [ ] **Step 5: Test**

Run: `dotnet test`
Expected: `Failed: 0` (471 passed). `LabelService` tests passing confirms the embedded labels resource still resolves under the new assembly name.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: rename ToneManager.App -> Namager.App

Renames the Avalonia app shell + its test project; assembly is now
Namager.App.exe. Updates namespaces, XAML x:Class/xmlns/avares URLs,
embedded labels LogicalName, app.manifest, release workflow publish
path, icon-script paths, and installer references. No behavior change."
```

---

### Task 3: Rename & rebrand the installer (new product identity)

Renames `ToneManager.Installer` → `Namager.Installer` and rebrands the WiX package as **"NAMager for Sonulab"** with a **NEW `UpgradeCode`** — a deliberate clean break so the new app installs beside old ToneManager (the operator uninstalls old ToneManager manually, Task 9). Also retargets the release workflow's installer paths. Not in the solution, so verification is a WiX build, not `dotnet test`. The `..\Namager.App\…` path references inside were already fixed by Task 2's sweep.

**Files:**
- Rename: `src/ToneManager.Installer/` → `src/Namager.Installer/` (+ `.wixproj`)
- Modify: `src/Namager.Installer/Namager.Installer.wixproj` — header comment, `OutputName`
- Modify: `src/Namager.Installer/Package.wxs` — all product-identity values (details below)
- Modify: `.github/workflows/release.yml` — installer build path + MSI glob
- Modify: `tools/icon/make-installer-art.ps1` — `src/ToneManager.Installer` output paths (art contains no text — icon + palette only — so the BMPs themselves need no regeneration)

- [ ] **Step 1: Move the folder and project file, then sweep the project-name string**

```bash
git mv src/ToneManager.Installer src/Namager.Installer
git mv src/Namager.Installer/ToneManager.Installer.wixproj src/Namager.Installer/Namager.Installer.wixproj
grep -rlZ 'ToneManager\.Installer' --include='*.ps1' --include='*.yml' \
  --include='*.wixproj' --include='*.wxs' \
  --exclude-dir=bin --exclude-dir=obj src tools .github \
  | xargs -0 sed -i 's/ToneManager\.Installer/Namager.Installer/g'
```

This fixes `.github/workflows/release.yml` (`dotnet build src/ToneManager.Installer`, `files: src/ToneManager.Installer/bin/**/*.msi`) and the `make-installer-art.ps1` output paths.

- [ ] **Step 2: Update the `.wixproj`**

In `src/Namager.Installer/Namager.Installer.wixproj`:
- Line 1 comment: `Deliberately NOT in ToneManager.slnx` → `Deliberately NOT in Namager.Sonulab.slnx`
- `<OutputName>ToneManager-$(ProductVersion)-x64</OutputName>` → `<OutputName>Namager.Sonulab-$(ProductVersion)-x64</OutputName>`
- Line ~26 comment: `end-of-install "Launch ToneManager" checkbox` → `end-of-install "Launch NAMager for Sonulab" checkbox`

- [ ] **Step 3: Update `Package.wxs`**

Apply these exact edits in `src/Namager.Installer/Package.wxs`:
- Header comment: `lands in %LOCALAPPDATA%\Programs\ToneManager.` → `lands in %LOCALAPPDATA%\Programs\Namager.Sonulab.`; and `UpgradeCode is FOREVER - changing it breaks in-place upgrades for every user.` → `UpgradeCode is FOREVER - changing it breaks in-place upgrades for every user. (Changed once, deliberately, at the 2026-07-22 NAMager rename: new product identity, clean break from ToneManager.)`
- `Package Name="ToneManager"` → `Package Name="NAMager for Sonulab"`
- `UpgradeCode="1431D009-A559-46D0-9568-BD9675EFC753"` → `UpgradeCode="47160B31-CC00-4023-887A-13478CB150D9"`
- `DowngradeErrorMessage="A newer version of ToneManager is already installed."` → `"A newer version of NAMager for Sonulab is already installed."`
- `Property Id="ARPURLINFOABOUT" Value="https://github.com/EdHubbell/ToneManager"` → `Value="https://github.com/EdHubbell/Namager.Sonulab"`
- `<Directory Id="INSTALLFOLDER" Name="ToneManager">` → `Name="Namager.Sonulab"`
- `Shortcut … Name="ToneManager"` → `Name="NAMager for Sonulab"`
- `RegistryValue Root="HKCU" Key="Software\ToneManager"` → `Key="Software\Namager.Sonulab"`
- UI comment: `Finish with a "Launch ToneManager"` → `Finish with a "Launch NAMager for Sonulab"`
- Art comment: `ToneManager wizard art` → `NAMager wizard art`
- `Property Id="WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT" Value="Launch ToneManager"` → `Value="Launch NAMager for Sonulab"`
- (`Target`/`ExeCommand` already say `Namager.App.exe` from Task 2's sweep — verify, don't re-edit.)

- [ ] **Step 4: Verify no old names remain in the installer**

Run:
```bash
grep -rn 'ToneManager' src/Namager.Installer
```
Expected: (no output)

- [ ] **Step 5: Build the installer (verifies the WiX project + paths)**

```bash
dotnet publish src/Namager.App -c Release -r win-x64
dotnet build src/Namager.Installer/Namager.Installer.wixproj -c Release
```
Expected: `Build succeeded`; an MSI named `Namager.Sonulab-1.0.0-x64.msi` under the installer's `bin`. (The only goal of the publish is that `PublishDir` exists so WiX harvests files.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor!: rebrand installer as NAMager for Sonulab, new UpgradeCode

Renames ToneManager.Installer -> Namager.Installer. NEW UpgradeCode
(deliberate clean break): installs beside old ToneManager, which is
uninstalled manually once. Product/shortcut/checkbox read 'NAMager for
Sonulab'; installs to Programs\\Namager.Sonulab; HKCU key + ARP URL +
release.yml paths updated."
```

---

### Task 4: Rebrand in-app user-facing text

Three runtime strings a user (or log reader) sees. No automated test asserts them; verification is build + the Task 9 smoke.

**Files:**
- Modify: `src/Namager.App/Views/MainWindow.axaml.cs:14`
- Modify: `src/Namager.App/Program.cs:16`
- Modify: `src/Namager.Tone3000/T3kAuth.cs:95`

- [ ] **Step 1: Update the window title**

In `src/Namager.App/Views/MainWindow.axaml.cs`, change:
```csharp
Title = $"ToneManager v{AppInfo.Version}";
```
to:
```csharp
Title = $"NAMager for Sonulab v{AppInfo.Version}";
```

- [ ] **Step 2: Update the startup log banner**

In `src/Namager.App/Program.cs`, change:
```csharp
log.Info("===== ToneManager started; logging to {0} =====", logPath);
```
to:
```csharp
log.Info("===== NAMager for Sonulab started; logging to {0} =====", logPath);
```

- [ ] **Step 3: Update the OAuth success page**

In `src/Namager.Tone3000/T3kAuth.cs`, change:
```csharp
var page = ok ? "<html><body>Signed in — you can close this tab and return to ToneManager.</body></html>"
```
to:
```csharp
var page = ok ? "<html><body>Signed in — you can close this tab and return to NAMager.</body></html>"
```
(Just "NAMager", not "NAMager for Sonulab" — Tone3000 sign-in is shared across the product line, per spec.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: rebrand in-app text to NAMager for Sonulab

Window title, startup log banner, and Tone3000 OAuth success page."
```

---

### Task 5: Point the update-check at the new repo (TDD)

`UpdateCheckService` polls the GitHub releases API. Retarget it at `EdHubbell/Namager.Sonulab` with User-Agent `Namager.Sonulab` (identifier — not the display name). This is the one change with a real test to flip first.

**Files:**
- Test: `tests/Namager.App.Tests/UpdateCheckServiceTests.cs:34,46,48`
- Modify: `src/Namager.App/Services/UpdateCheckService.cs:19,28`

**Interfaces:**
- Consumes: `UpdateCheckService` (unchanged signature).

- [ ] **Step 1: Update the test expectations to the new repo (RED)**

In `tests/Namager.App.Tests/UpdateCheckServiceTests.cs`:
- Line 34 fixture: `"""{"tag_name":"v2.5.0","html_url":"https://github.com/EdHubbell/ToneManager/releases/tag/v2.5.0"}"""` → `…github.com/EdHubbell/Namager.Sonulab/releases/tag/v2.5.0…`
- Line 46 request-URL assert: `"https://api.github.com/repos/EdHubbell/ToneManager/releases/latest"` → `…/repos/EdHubbell/Namager.Sonulab/releases/latest`
- Line 48 user-agent assert: `Assert.Equal("ToneManager", handler.LastRequest?.Headers.UserAgent?.ToString());` → `Assert.Equal("Namager.Sonulab", …)`

- [ ] **Step 2: Run the test — expect failure**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckServiceTests`
Expected: FAIL — the service still requests the `ToneManager` URL with the old User-Agent.

- [ ] **Step 3: Update the service (GREEN)**

In `src/Namager.App/Services/UpdateCheckService.cs`:
- Releases URL constant: `"https://api.github.com/repos/EdHubbell/ToneManager/releases/latest"` → `"https://api.github.com/repos/EdHubbell/Namager.Sonulab/releases/latest"`
- User agent: `_http.DefaultRequestHeaders.UserAgent.ParseAdd("ToneManager");` → `ParseAdd("Namager.Sonulab");`

- [ ] **Step 4: Run the test — expect pass**

Run: `dotnet test --filter FullyQualifiedName~UpdateCheckServiceTests`
Expected: `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test`
Expected: `Failed: 0` (471 passed).
```bash
git add -A
git commit -m "feat: point update-check at EdHubbell/Namager.Sonulab"
```

---

### Task 6: Rename the feedback worker and endpoint

Renames the Cloudflare worker (`tonemanager-feedback` → `namager-sonulab-feedback`, new endpoint URL) and retargets it at the new repo. Worker deployment itself is manual (Task 9); this task only edits code/config. No automated test asserts the endpoint URL, so verification is build + a `grep` sweep.

**Files:**
- Modify: `infra/feedback-worker/wrangler.toml:1`
- Modify: `infra/feedback-worker/worker.js:1-5,60`
- Modify: `src/Namager.App/Services/FeedbackService.cs:25`
- Modify: `infra/feedback-worker/README.md` (repo name, PAT scoping, `gh label` command, example URLs, FeedbackService path)

- [ ] **Step 1: Update `wrangler.toml`**

`name = "tonemanager-feedback"` → `name = "namager-sonulab-feedback"`

- [ ] **Step 2: Update `worker.js`**

- Line 1: `// ToneManager feedback endpoint: turns an app POST into a GitHub issue.` → `// NAMager for Sonulab feedback endpoint: turns an app POST into a GitHub issue.`
- Line 3: `// scoped to EdHubbell/ToneManager with Issues read/write ONLY.` → `// scoped to EdHubbell/Namager.Sonulab with Issues read/write ONLY.`
- `const REPO = 'EdHubbell/ToneManager';` → `const REPO = 'EdHubbell/Namager.Sonulab';`
- `'user-agent': 'tonemanager-feedback-worker',` → `'user-agent': 'namager-sonulab-feedback-worker',`

- [ ] **Step 3: Update the app endpoint**

In `src/Namager.App/Services/FeedbackService.cs`:
```csharp
public const string EndpointUrl = "https://tonemanager-feedback.ed-eed.workers.dev/";
```
→
```csharp
public const string EndpointUrl = "https://namager-sonulab-feedback.ed-eed.workers.dev/";
```
(The account subdomain `ed-eed` is assumed unchanged; Task 9 reconciles against the actual `wrangler deploy` output.)

- [ ] **Step 4: Update the worker README**

In `infra/feedback-worker/README.md`: `EdHubbell/ToneManager` → `EdHubbell/Namager.Sonulab` (issue-target line + `gh label create` command); PAT repository-access `ToneManager` → `Namager.Sonulab`; `tonemanager-feedback` → `namager-sonulab-feedback` (deploy URL example + rate-limit section); `src/ToneManager.App/Services/FeedbackService.cs` → `src/Namager.App/Services/FeedbackService.cs`.

- [ ] **Step 5: Verify + build**

Run:
```bash
grep -rni 'tonemanager' infra/feedback-worker src/Namager.App/Services/FeedbackService.cs
dotnet build
```
Expected: grep prints nothing; `Build succeeded`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: rename feedback worker to namager-sonulab-feedback, retarget repo

worker.js REPO + user-agent, wrangler name, app EndpointUrl, and README
all point at Namager.Sonulab. Deploy is a manual step."
```

---

### Task 7: Move the config path to %APPDATA%\Namager (shared product-line root)

The Tone3000 config + token live under `%APPDATA%\ToneManager`. Point them at the **shared** `%APPDATA%\Namager\` root — Tone3000 is vendor-neutral, so future sibling apps (e.g. a second manufacturer's NAMager) inherit the login. `%APPDATA%\Namager\Sonulab\` is reserved for vendor-specific data; nothing writes there yet. No migration (users re-authenticate once — a design decision). No automated test asserts the path segment.

**Files:**
- Modify: `src/Namager.Tone3000/T3kConfig.cs:14`
- Modify: `src/Namager.Tone3000/T3kTokenStore.cs:14`
- Modify: `tools/T3kProbe/Program.cs:2` (comment)

- [ ] **Step 1: Update the path segments**

In `src/Namager.Tone3000/T3kConfig.cs` line 14: `"ToneManager", "tone3000.json");` → `"Namager", "tone3000.json");`
In `src/Namager.Tone3000/T3kTokenStore.cs` line 14: `"ToneManager", "tone3000.token");` → `"Namager", "tone3000.token");`

- [ ] **Step 2: Update the T3kProbe comment**

In `tools/T3kProbe/Program.cs` line 2, change `Reads %APPDATA%\ToneManager\tone3000.json directly` to `Reads %APPDATA%\Namager\tone3000.json directly`.

- [ ] **Step 3: Verify + build + test**

Run:
```bash
grep -rn 'ToneManager' src tools --include='*.cs' --exclude-dir=bin --exclude-dir=obj
dotnet build && dotnet test
```
Expected: grep prints nothing (Tasks 4–6 removed the other in-code occurrences); `Build succeeded`; `Failed: 0` (471 passed).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: move config dir to %APPDATA%\\Namager (shared product-line root)

Tone3000 config + token live at the shared Namager root so future
sibling apps inherit the login; Namager\\Sonulab is reserved for
vendor-specific data. No migration; users re-authenticate once."
```

---

### Task 8: Rebrand docs & project metadata

Pure documentation. Updates `README.md`, `CLAUDE.md` (renamed projects + new names; keeping `Sonulab.Core/Distill/Transport.Wifi`), and product-name prose in `docs/`. No build impact.

**Files:**
- Modify: `README.md` (title/prose, rename note, release/issue URLs)
- Modify: `CLAUDE.md` (header, project paths, repo refs, config path)
- Modify: `docs/**` product-name prose where it reads as the current product
- Check: `tone3000.json.example` (update any `%APPDATA%\ToneManager` mention)

- [ ] **Step 1: Update README**

- Title: `# ToneManager` → `# NAMager for Sonulab`
- Below the existing `> Renamed from StompStationManager on 2026-07-21.` note, add: `> Renamed from ToneManager on 2026-07-22. NAMager = NAM + manager; one NAMager app per hardware manufacturer.`
- Releases link: `https://github.com/EdHubbell/ToneManager/releases/latest` → `https://github.com/EdHubbell/Namager.Sonulab/releases/latest`
- `Launch **ToneManager** from the Start Menu` → `Launch **NAMager for Sonulab** from the Start Menu`
- `ToneManager connects over USB first` → `NAMager connects over USB first`
- Feedback issues link: `https://github.com/EdHubbell/ToneManager/issues` → `https://github.com/EdHubbell/Namager.Sonulab/issues`

- [ ] **Step 2: Update CLAUDE.md**

- Header: `# CLAUDE.md — ToneManager` → `# CLAUDE.md — NAMager for Sonulab`
- Project paths in build/run/architecture sections: `src/ToneManager.App` → `src/Namager.App`, `ToneManager.Tone3000` → `Namager.Tone3000`, `ToneManager.App.Tests` → `Namager.App.Tests`, `%APPDATA%\ToneManager\tone3000.json` → `%APPDATA%\Namager\tone3000.json`. **Leave `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi` unchanged.**
- Any remaining "ToneManager" prose (e.g. "ToneManager-branded installer") → NAMager phrasing.

- [ ] **Step 3: Sweep docs prose**

Run:
```bash
grep -rln 'ToneManager' docs README.md CLAUDE.md tone3000.json.example PROTOCOL.md
```
For each hit that names the *current product* (not a historical spec/plan body), update to `NAMager for Sonulab` (prose) or `Namager.*` (paths/identifiers). Prioritize current-reference files: `README.md`, `CLAUDE.md`, `PROTOCOL.md`, `docs/HARDWARE-VALIDATION-*`, `infra` docs. Historical specs/plans (incl. the 2026-07-21 rebrand spec/plan and this rename's own spec, which legitimately mention ToneManager) stay as-is.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: rebrand README/CLAUDE/docs to NAMager for Sonulab

Product-name prose + moved-project references updated; vendor projects
(Sonulab.Core/Distill/Transport.Wifi) kept. Records the 2026-07-22 rename."
```

---

### Task 9: Repo migration & manual deploy (operator gates)

Operational steps that finish the transition. The `git` steps can run once the `EdHubbell/Namager.Sonulab` repo exists; the Cloudflare/GitHub steps are manual and require your credentials. **Do the GitHub repo creation/confirmation and PAT steps yourself — they are not automatable here.**

**Prerequisites (manual, you):**
- Confirm the empty `EdHubbell/Namager.Sonulab` repo exists on GitHub (`gh repo view EdHubbell/Namager.Sonulab`); create it if not.
- Create a fine-grained PAT scoped to `Namager.Sonulab`, **Issues: Read/Write only**.

- [ ] **Step 1: Repoint origin and push history**

```bash
git remote set-url origin git@github.com:EdHubbell/Namager.Sonulab.git
git push origin --all
git push origin --tags
```
Expected: all branches/tags land in the new repo; `git remote -v` shows `Namager.Sonulab.git`.

- [ ] **Step 2: Create the feedback label (manual)**

```bash
gh label create user-feedback --repo EdHubbell/Namager.Sonulab --color F9D0C4 \
  --description "Submitted from the app's Send Feedback dialog"
```

- [ ] **Step 3: Deploy the worker (manual)**

```bash
cd infra/feedback-worker
wrangler secret put GITHUB_TOKEN   # paste the new Namager.Sonulab-scoped PAT
wrangler deploy
```
Note the printed URL. **If it is not `https://namager-sonulab-feedback.ed-eed.workers.dev/`,** update `FeedbackService.EndpointUrl` (Task 6, Step 3) to match, rebuild, and commit the fix. Optionally delete the old `tonemanager-feedback` worker afterward (`wrangler delete --name tonemanager-feedback`).

- [ ] **Step 4: End-to-end feedback verification (manual)**

Run the app (`dotnet run --project src/Namager.App`), open Send Feedback, submit a test message, and confirm a new issue appears in `EdHubbell/Namager.Sonulab` with the `user-feedback` label.

- [ ] **Step 5: Uninstall old ToneManager + post-rename smoke (manual)**

- Uninstall **ToneManager** from Windows Settings → Installed apps (the new UpgradeCode means the new MSI will NOT remove it automatically).
- Install the new MSI (or `dotnet run --project src/Namager.App`); confirm: title reads `NAMager for Sonulab v…`; preset/amp/IR tabs load (labels resource resolves); Tone3000 sign-in writes to `%APPDATA%\Namager\`; Start-menu shortcut says "NAMager for Sonulab"; installer wizard shows the NAMager name over the existing art.
- Optionally delete the leftover `%APPDATA%\ToneManager` and `%LOCALAPPDATA%\Programs\ToneManager` folders.

- [ ] **Step 6: Release note**

In the next GitHub Release description, add: "Renamed ToneManager → NAMager for Sonulab on 2026-07-22; version numbering continues unchanged. NAMager is one app per hardware manufacturer — this one drives the Sonulab StompStation."

---

## Notes for the executor

- Run everything from the repo root in Git Bash unless a step says otherwise.
- If a `dotnet build` fails right after a rename with a XAML/`x:Class` mismatch, run `dotnet clean` (stale `obj/*.g.cs` from the old namespace) and rebuild before investigating further.
- Tasks 1–3 must run in order (each depends on the previous project/namespace state). Tasks 4–8 are independent of each other and can run in any order after Task 3. Task 9 is last.
- Never run a blanket `ToneManager` → `Namager` replace: historical docs keep "ToneManager", and human-facing strings need "NAMager"/"NAMager for Sonulab", not "Namager".
