# Design: StompStationManager → ToneManager rebrand

**Date:** 2026-07-21
**Status:** Approved (design)
**Scope:** Rename/rebrand + targeted project renames. No functional or architecture
changes (no abstraction seams, no second vendor).

## Motivation

The app is being renamed from **StompStationManager** to **ToneManager** to reflect
a future in which it manages hardware devices from multiple vendors, not only the
Sonulab StompStation. This effort is the rename/rebrand groundwork *only* — it
unblocks multi-vendor work but does not implement it. Multi-vendor architecture is a
separate, later effort with its own spec.

## Decisions (locked)

- **Scope:** Rename/rebrand + the targeted project renames below. No abstraction seams,
  no second vendor, no behavior changes.
- **Project/namespace boundary:** Split the projects by *whose* code they are —
  product/app-level layer becomes `ToneManager.*`; genuinely Sonulab-vendor-specific
  device code stays `Sonulab.*`. This lands the boundary where the multi-vendor future
  wants it: `ToneManager.App` (product shell) consumes vendor libraries, and a second
  vendor later slots in as `OtherVendor.*` beside `Sonulab.*`.

  | Project | Decision | Why |
  |---|---|---|
  | `Sonulab.App` | **→ `ToneManager.App`** | The product shell/UI; not vendor code |
  | `Sonulab.Tone3000` | **→ `ToneManager.Tone3000`** | Tone3000.com is a third-party NAM marketplace, unrelated to Sonulab |
  | `Sonulab.Installer` | **→ `ToneManager.Installer`** | Packages the ToneManager product |
  | `Sonulab.App.Tests` | **→ `ToneManager.App.Tests`** | Follows its subject |
  | `Sonulab.Tone3000.Tests` | **→ `ToneManager.Tone3000.Tests`** | Follows its subject |
  | `Sonulab.Core` | **Keep** | 100% Sonulab wire protocol + device model |
  | `Sonulab.Distill` | **Keep** | Produces Sonulab's `.vxamp` format |
  | `Sonulab.Transport.Wifi` | **Keep** | SonuLink-over-TCP + Sonulab mDNS discovery |
  | `Sonulab.Core.Tests`, `Sonulab.Distill.Tests`, `Sonulab.Transport.Wifi.Tests` | **Keep** | Follow their subjects |

  A future multi-vendor effort may split a vendor-neutral `ToneManager.Core` out of
  `Sonulab.Core`; that is explicitly **out of scope** here.
- **Display / naming convention:** **`ToneManager`** (no space) everywhere — both
  human-facing display text and identifiers/folders/repo/worker. There is no spaced
  variant.
- **Repo move:** Repoint `origin` to `git@github.com:EdHubbell/ToneManager.git` and
  push existing history (all branches/commits). The new repo is expected to exist and
  be empty.
- **Config data:** Re-authenticate. The `%APPDATA%` config folder moves from
  `StompStationManager` to `ToneManager`; existing Tone3000 login/token is orphaned.
  No migration code. Acceptable given the tiny (effectively single-user) install base.
- **Feedback worker:** Rename the Cloudflare worker (new endpoint URL) *and* retarget
  it at the new repo.
- **Versioning:** Keep the existing version-number scheme unchanged. The name change
  is announced in the next GitHub Release description (dated 2026-07-21) plus a
  one-line note in README. The csproj `<Version>` is not touched by this work.

## Change inventory

### 1. Project & namespace renames (do this first — everything below references the new paths)

For each renamed project: rename the folder, rename the `.csproj`/`.wixproj`, change
`namespace` declarations and `using` statements, and update every `ProjectReference`
and `.slnx` project path that points at it.

- `src/Sonulab.App/` → `src/ToneManager.App/` (`Sonulab.App.csproj` → `ToneManager.App.csproj`)
  - `namespace Sonulab.App[.*]` → `ToneManager.App[.*]` across ViewModels, Views,
    Services, Behaviors, Converters, Models, `Program.cs`, `AppInfo.cs`, `App.axaml.cs`
  - **Assembly/output name becomes `ToneManager.App.exe`** (defaults from project name).
    Consequences:
    - Installer shortcut `Target` → `ToneManager.App.exe` (see §6)
    - Root `Sonulab.App.exe - Shortcut.lnk` dev convenience shortcut → regenerate/rename
      (untracked working-tree file; low priority)
  - **Embedded-resource `LogicalName`** `Sonulab.App.labels.en.json` → `ToneManager.App.labels.en.json`
    in the `.csproj`, plus the error-message string in `LabelService` that names
    `Sonulab.App.csproj`. (Note: `LabelService` locates the resource by
    `GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("labels.en.json"))`, i.e.
    a *suffix* match — so label loading cannot silently break; the `LogicalName` edit is
    cosmetic-correctness only.)
  - **XAML:** `x:Class` in `App.axaml` + every `Views/*.axaml`, and any
    `xmlns:...="clr-namespace:Sonulab.App..."` references → `ToneManager.App...`
  - **`ViewLocator`** — confirmed safe: it resolves views via
    `param.GetType().FullName.Replace("ViewModel","View")` + `Type.GetType(name)` with no
    hardcoded `Sonulab.App` literal, so it works unchanged once VM+View namespaces move
    together. No edit needed beyond the namespace change.
- `src/Sonulab.Tone3000/` → `src/ToneManager.Tone3000/` (`.csproj` renamed)
  - `namespace Sonulab.Tone3000` → `ToneManager.Tone3000` across `T3k*.cs`
  - `AssemblyInfo.cs`: update any assembly-name / `InternalsVisibleTo` string
- `src/Sonulab.Installer/` → `src/ToneManager.Installer/` (`.wixproj` renamed) — see §6
- `tests/Sonulab.App.Tests/` → `tests/ToneManager.App.Tests/` (`.csproj` renamed;
  `namespace`/`using` and `ProjectReference` → `ToneManager.App`)
- `tests/Sonulab.Tone3000.Tests/` → `tests/ToneManager.Tone3000.Tests/` (same treatment)
- **`InternalsVisibleTo`**: no source-level `InternalsVisibleTo` was found (tests use
  `ProjectReference` + public APIs). If any surfaces during the rename (e.g. in an
  `AssemblyInfo.cs`) pointing at `Sonulab.App.Tests` / `Sonulab.Tone3000.Tests`, update
  it to the new test-assembly name.
- **`.slnx`** (see §2): update the `<Project>` paths for all five renamed projects.
- **Consumers of renamed projects:** `ToneManager.App` referencing
  `ToneManager.Tone3000`; `tools/T3kProbe` referencing `Sonulab.Tone3000` →
  `ToneManager.Tone3000` (`using` + any `ProjectReference`).
- **Unchanged references stay as-is:** `ToneManager.App` still references
  `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi`; `tools/HwCheck` still
  references `Sonulab.Core`. Mixed `ToneManager.*` ↔ `Sonulab.*` references are expected
  and correct.

### 2. Product / branding strings
- `src/ToneManager.App/Views/MainWindow.axaml.cs` — window title → `"ToneManager v{AppInfo.Version}"`
- `src/ToneManager.Tone3000/T3kAuth.cs` — OAuth success page text → "…you can close this tab and return to ToneManager."
- `README.md` — title/prose → ToneManager; add one-line "Renamed from StompStationManager on 2026-07-21" note
- `CLAUDE.md` — header line ("StompStation Manager") and any prose references → ToneManager
- `docs/**` prose references (HARDWARE-VALIDATION-*, specs, plans) — updated where they name the product. Historical spec/plan filenames are left as-is; only in-body product-name prose is corrected where it reads as the current product. (Low priority; does not affect build or runtime.)
- `Sonulab.slnx` → **`ToneManager.slnx`** (solution = the app workspace).

### 3. GitHub repo references (`EdHubbell/StompStationManager` → `EdHubbell/ToneManager`)
- `src/ToneManager.App/Services/UpdateCheckService.cs`
  - releases URL `.../repos/EdHubbell/StompStationManager/releases/latest` → `ToneManager`
  - `UserAgent.ParseAdd("StompStationManager")` → `"ToneManager"`
- `tests/ToneManager.App.Tests/UpdateCheckServiceTests.cs` — expected `html_url` → new repo
  (this is the **only** test asserting the app repo/name; `FeedbackService` has no test
  asserting its endpoint URL, so changing `EndpointUrl` needs no test change)
- Git remote: `git remote set-url origin git@github.com:EdHubbell/ToneManager.git`, then
  `git push origin --all` (and `--tags` if any).

### 4. Feedback worker (rename worker + retarget repo)
- `infra/feedback-worker/wrangler.toml` — `name = "tonemanager-feedback"`
- `infra/feedback-worker/worker.js`
  - header comment (line 1–3) → ToneManager
  - `const REPO = 'EdHubbell/ToneManager'`
  - `'user-agent': 'tonemanager-feedback-worker'`
- `src/ToneManager.App/Services/FeedbackService.cs` — `EndpointUrl` →
  `https://tonemanager-feedback.ed-eed.workers.dev/`
  (confirm the exact subdomain against `wrangler deploy` output; the account subdomain
  `ed-eed` is expected to stay the same — only the worker name changes.)
- `infra/feedback-worker/README.md` — repo name, PAT repository-access scoping, the
  `gh label create` command, and example URLs → ToneManager / tonemanager-feedback

**Manual steps (only the user can perform these — the app/worker changes are inert until done):**
1. Create the empty `EdHubbell/ToneManager` GitHub repo.
2. Create a new fine-grained PAT scoped to `ToneManager`, Issues **read/write only**.
3. `cd infra/feedback-worker && wrangler secret put GITHUB_TOKEN` (paste new PAT).
4. `wrangler deploy` — note the printed URL; reconcile with `FeedbackService.EndpointUrl`.
5. `gh label create user-feedback --repo EdHubbell/ToneManager --color F9D0C4 --description "Submitted from the app's Send Feedback dialog"`
6. Post-deploy manual verification: submit a test feedback from the app → confirm an
   issue appears in the ToneManager repo.

### 5. Config path (`%APPDATA%\StompStationManager` → `ToneManager`) — re-authenticate
- `src/ToneManager.Tone3000/T3kConfig.cs` — path segment → `"ToneManager"`
- `src/ToneManager.Tone3000/T3kTokenStore.cs` — path segment → `"ToneManager"`
- `tools/T3kProbe/Program.cs` — comment referencing the path → `ToneManager`
- No migration code. First run after upgrade creates a fresh `ToneManager` folder; the
  user re-logs in to Tone3000 once.

### 6. Installer (`src/ToneManager.Installer/Package.wxs` + `.wixproj`)
- Project renamed to `ToneManager.Installer` (see §1).
- `Package Name` → `"ToneManager"`
- `MajorUpgrade DowngradeErrorMessage` → "A newer version of ToneManager is already installed."
- Shortcut `Name` → `"ToneManager"`
- **Shortcut `Target`** → `[INSTALLFOLDER]ToneManager.App.exe` (assembly renamed in §1)
- `INSTALLFOLDER` `Name` → `"ToneManager"` (installs to `%LOCALAPPDATA%\Programs\ToneManager`)
- `RegistryValue` `Key` `Software\StompStationManager` → `Software\ToneManager`
- `ARPURLINFOABOUT` → `https://github.com/EdHubbell/ToneManager`
- `Icon SourceFile` path `..\Sonulab.App\Assets\app-icon.ico` → `..\ToneManager.App\Assets\app-icon.ico`
- Header comment referencing the install path → ToneManager
- **Keep unchanged:** the `UpgradeCode` GUID (`1431D009-…`) — changing it breaks
  in-place upgrades forever.
- **Known wrinkle (accepted):** keeping the `UpgradeCode` while renaming `INSTALLFOLDER`
  means a major-upgrade over an old StompStationManager install may leave the old
  per-user folder behind. Acceptable given the effectively single-user base; a clean
  reinstall avoids it entirely.

### 7. Versioning / release notes
- csproj `<Version>` untouched.
- Announce the rename in the next GitHub Release description, dated 2026-07-21.
- One-line note in README recording the rename and date.

## Out of scope / explicitly not touched
- The `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi` projects/namespaces
  and their test projects — these are genuinely vendor-specific and keep the `Sonulab.*`
  name.
- Splitting a vendor-neutral `ToneManager.Core` out of `Sonulab.Core` (future
  multi-vendor work).
- The installer `UpgradeCode` GUID.
- Protocol code, transport behavior, distiller logic, and all runtime behavior — the
  renames are mechanical; no logic changes.
- Any multi-vendor abstraction or second-vendor support.
- The device license/model identifiers `stompstation1` and the "StompStation"
  device-model references in code/tests/`FakePresetDevice` — these are vendor/product
  data, not the app name, and stay unchanged.
- `tests/Sonulab.Distill.Tests/goldens-corpus/*.nam.path.txt` — these record a
  historical absolute source path (`…\Buckdrivers\Sonulab\StompStationManager\…`) as
  golden provenance; they are left byte-for-byte unchanged so golden comparisons stay
  stable.

## Testing strategy
- `dotnet build` succeeds against the renamed `ToneManager.slnx` (surfaces any missed
  `using`/`ProjectReference`/`x:Class`/`LogicalName` after the project renames).
- `dotnet test` — all 312 tests green (renames don't change the count) after updating
  the single test expectation in `UpdateCheckServiceTests` (the only test asserting the
  old repo/name).
- Feedback worker has no automated tests: verification is the manual post-deploy check
  (§4 step 6).
- Manual smoke: launch the app, confirm the window title reads `ToneManager v…`, that
  the embedded labels resource still loads (proves the `LogicalName` rename is correct),
  and that Tone3000 re-auth flows into the new `%APPDATA%\ToneManager` folder.
