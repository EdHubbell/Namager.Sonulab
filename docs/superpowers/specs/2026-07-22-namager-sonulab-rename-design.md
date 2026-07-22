# Design: ToneManager → Namager for Sonulab rename

**Date:** 2026-07-22
**Status:** Approved (design)
**Scope:** Rename/rebrand + targeted project renames. No functional or architecture
changes (no abstraction seams, no second vendor).

## Motivation

The app is being renamed from **ToneManager** to **Namager** ("NAM" + "manager";
namager.com is obtainable without trademark risk). Namager is a product *line*: one
app per hardware manufacturer, each in its own repo (`Namager.<Vendor>`), installable
side-by-side. Long term, the apps will share data (e.g. upload presets from one
vendor's hardware, download to another's). This effort is the rename/rebrand only —
this repo becomes **Namager.Sonulab**, the Sonulab StompStation app. Multi-vendor
architecture remains a separate, later effort.

## Decisions (locked)

- **Display name:** **"Namager for Sonulab"** — window title, installer, Start-menu
  shortcut. Users may eventually install several Namager apps, so the vendor is
  visible in the product name.
- **App-layer projects/namespaces:** `Namager.App`, `Namager.Tone3000`,
  `Namager.Installer` (+ matching test projects). **Not** `Namager.Sonulab.*`: a
  `Namager.Sonulab` namespace would shadow the `Sonulab.*` vendor namespaces — inside
  `namespace Namager.Sonulab.App`, a qualified `Sonulab.Core.X` reference resolves
  `Sonulab` to `Namager.Sonulab` and fails to compile. The per-vendor identity lives
  in the repo name, display name, and installer instead; every future manufacturer
  app repeats the same clean `Namager.*` project layout in its own repo.
- **Vendor libraries unchanged:** `Sonulab.Core`, `Sonulab.Distill`,
  `Sonulab.Transport.Wifi` (+ their tests) keep their names, per the 2026-07-21
  rebrand decision (project split by *whose* code it is).
- **Repo move:** Repoint `origin` to `git@github.com:EdHubbell/Namager.Sonulab.git`
  and push existing history (all branches + tags). The local working folder is
  already `C:\Development\Namager\Namager.Sonulab`.
- **Config data:** `%APPDATA%\Namager\` is the shared root for the product line;
  `%APPDATA%\Namager\Sonulab\` holds vendor-specific data. The Tone3000 config +
  token (vendor-neutral NAM marketplace) go at the **shared root** so future sibling
  apps inherit the login. No migration code from `%APPDATA%\ToneManager`; the user
  re-authenticates once.
- **Installer identity:** **New UpgradeCode GUID** — clean break. "Namager for
  Sonulab" installs beside an existing ToneManager install; the user manually
  uninstalls old ToneManager once. Future manufacturer apps each get their own GUID.
- **Feedback worker:** Rename the Cloudflare worker (`namager-sonulab-feedback`, new
  endpoint URL) and retarget it at the new repo.
- **Versioning:** Version-number scheme unchanged; csproj `<Version>` untouched.
  Rename announced in the next GitHub Release description (dated 2026-07-22) plus a
  one-line note in README.

## Change inventory

### 1. Project & namespace renames (first — everything below references the new paths)

For each renamed project: rename the folder, rename the `.csproj`/`.wixproj`, change
`namespace` declarations and `using` statements, and update every `ProjectReference`
and `.slnx` project path that points at it.

| Current | New |
|---|---|
| `src/ToneManager.App/` | `src/Namager.App/` |
| `src/ToneManager.Tone3000/` | `src/Namager.Tone3000/` |
| `src/ToneManager.Installer/` | `src/Namager.Installer/` |
| `tests/ToneManager.App.Tests/` | `tests/Namager.App.Tests/` |
| `tests/ToneManager.Tone3000.Tests/` | `tests/Namager.Tone3000.Tests/` |
| `ToneManager.slnx` | `Namager.Sonulab.slnx` |

Mechanics (proven by the 2026-07-21 rename):

- `namespace ToneManager.App[.*]` → `Namager.App[.*]` across ViewModels, Views,
  Services, Behaviors, Converters, `Program.cs`, `AppInfo.cs`, `App.axaml.cs`.
- **Assembly/output name becomes `Namager.App.exe`** (defaults from project name) —
  installer shortcut/launch `Target` updated to match (§6).
- **Embedded-resource `LogicalName`** → `Namager.App.labels.en.json`, plus the
  error-message strings in `LabelService` / `ParameterExposure` that name
  `ToneManager.App.csproj`. (`LabelService` locates the resource by suffix match, so
  label loading cannot silently break; the `LogicalName` edit is cosmetic-correctness.)
- **XAML:** `x:Class` in `App.axaml` + every `Views/*.axaml`, and any
  `xmlns:...="clr-namespace:ToneManager.App..."` → `Namager.App...`.
- **`ViewLocator`** resolves views by reflection (`FullName.Replace("ViewModel","View")`)
  with no hardcoded namespace literal — no edit beyond the namespace change.
- `namespace ToneManager.Tone3000` → `Namager.Tone3000` across `T3k*.cs`;
  `tools/T3kProbe` `using`/`ProjectReference` follows.
- **`InternalsVisibleTo`:** none exists today; if one surfaces, update the assembly name.
- **Consumers:** `Namager.App` references `Namager.Tone3000` plus the unchanged
  `Sonulab.Core` / `Sonulab.Distill` / `Sonulab.Transport.Wifi`; `tools/HwCheck`
  still references `Sonulab.Core`. Mixed `Namager.*` ↔ `Sonulab.*` references are
  expected and correct.

### 2. Product / branding strings

- `Views/MainWindow.axaml.cs` — window title → `"Namager for Sonulab v{AppInfo.Version}"`
- `Namager.Tone3000/T3kAuth.cs` — OAuth success page → "…you can close this tab and
  return to Namager."
- `README.md` — title/prose → Namager for Sonulab; one-line "Renamed from ToneManager
  on 2026-07-22" note (keep the earlier StompStationManager note for the trail).
- `CLAUDE.md` — header + prose → Namager for Sonulab / Namager.Sonulab.
- `docs/**` prose where it names the current product (low priority; historical
  spec/plan filenames stay as-is).
- **Installer wizard art** — regenerate `banner.bmp`/`dialog.bmp` via
  `tools/icon/make-installer-art.ps1` with the new product name (update the name
  string inside the script; don't hand-edit the BMPs). `make-ico.ps1` comment refs too.

### 3. GitHub repo references (`EdHubbell/ToneManager` → `EdHubbell/Namager.Sonulab`)

- `Services/UpdateCheckService.cs` — releases URL →
  `.../repos/EdHubbell/Namager.Sonulab/releases/latest`; `UserAgent` → `"Namager.Sonulab"`.
- `tests/Namager.App.Tests/UpdateCheckServiceTests.cs` — expected `html_url` → new repo
  (the only test asserting the app repo/name).
- `.github/workflows/release.yml` — `dotnet publish src/Namager.App`,
  `dotnet build src/Namager.Installer`, MSI glob path.
- Git remote: `git remote set-url origin git@github.com:EdHubbell/Namager.Sonulab.git`,
  then `git push origin --all` and `git push origin --tags`.

### 4. Feedback worker (rename worker + retarget repo)

- `infra/feedback-worker/wrangler.toml` — `name = "namager-sonulab-feedback"`
- `infra/feedback-worker/worker.js` — header comment → Namager;
  `const REPO = 'EdHubbell/Namager.Sonulab'`;
  `'user-agent': 'namager-sonulab-feedback-worker'`
- `Services/FeedbackService.cs` — `EndpointUrl` →
  `https://namager-sonulab-feedback.ed-eed.workers.dev/` (reconcile the exact
  subdomain against `wrangler deploy` output; only the worker name changes).
- `infra/feedback-worker/README.md` — repo name, PAT scoping, `gh label create`
  command, example URLs.

**Manual steps (only the user can perform these — app/worker changes are inert until done):**

1. Create the empty `EdHubbell/Namager.Sonulab` GitHub repo (or confirm it exists).
2. New fine-grained PAT scoped to `Namager.Sonulab`, Issues **read/write only**.
3. `cd infra/feedback-worker && wrangler secret put GITHUB_TOKEN` (paste new PAT).
4. `wrangler deploy` — note the printed URL; reconcile with `FeedbackService.EndpointUrl`.
5. `gh label create user-feedback --repo EdHubbell/Namager.Sonulab --color F9D0C4 --description "Submitted from the app's Send Feedback dialog"`
6. Uninstall the old ToneManager app once (new UpgradeCode means the new installer
   won't remove it).
7. Post-deploy verification: submit a test feedback from the app → confirm an issue
   appears in the Namager.Sonulab repo.

### 5. Config path (`%APPDATA%\ToneManager` → `%APPDATA%\Namager[\Sonulab]`) — re-authenticate

- `Namager.Tone3000/T3kConfig.cs` — path → `%APPDATA%\Namager\tone3000.json` (shared root).
- `Namager.Tone3000/T3kTokenStore.cs` — path → `%APPDATA%\Namager\` (shared root).
- `tools/T3kProbe/Program.cs` — comment referencing the path.
- `%APPDATA%\Namager\Sonulab\` is reserved for vendor-specific data; nothing writes
  there yet (device backups currently land in the repo's gitignored `docs/backups/`).
- No migration code. First run creates the fresh folder; the user re-logs-in to
  Tone3000 once.

### 6. Installer (`src/Namager.Installer/Package.wxs` + `.wixproj`)

- Project renamed to `Namager.Installer` (§1).
- `Package Name` → `"Namager for Sonulab"`; **`UpgradeCode` → a freshly generated GUID**
  (clean break from ToneManager; this new GUID is then forever for this product).
- `MajorUpgrade DowngradeErrorMessage` → "A newer version of Namager for Sonulab is
  already installed."
- Shortcut `Name` → `"Namager for Sonulab"`; `Target` → `[INSTALLFOLDER]Namager.App.exe`.
- `INSTALLFOLDER` `Name` → `"Namager.Sonulab"` (installs to
  `%LOCALAPPDATA%\Programs\Namager.Sonulab`).
- `RegistryValue` `Key` → `Software\Namager.Sonulab`.
- `ARPURLINFOABOUT` → `https://github.com/EdHubbell/Namager.Sonulab`.
- Launch checkbox text → `"Launch Namager for Sonulab"`; `LaunchApplication`
  `ExeCommand` → `Namager.App.exe`.
- `Icon SourceFile` path → `..\Namager.App\Assets\app-icon.ico`; header comment updated.
- Wizard art regenerated with the new name (§2).

### 7. Versioning / release notes

- csproj `<Version>` untouched.
- Announce the rename in the next GitHub Release description, dated 2026-07-22.
- One-line note in README recording the rename and date.

## Out of scope / explicitly not touched

- `Sonulab.Core`, `Sonulab.Distill`, `Sonulab.Transport.Wifi` and their test
  projects — vendor-specific, keep their names.
- Multi-vendor abstraction, cross-app preset sharing, or a second vendor app — later
  efforts with their own specs. This rename only positions the naming for them.
- The device identifiers `stompstation1` / "StompStation" in code, tests, and
  `FakePresetDevice` — vendor/product data, not the app name.
- `tests/Sonulab.Distill.Tests/goldens-corpus/*.nam.path.txt` — historical golden
  provenance paths, left byte-for-byte unchanged.
- Old `%APPDATA%\ToneManager` folder cleanup — harmless residue; user may delete it.
- Protocol code, transports, distiller logic, all runtime behavior.

## Testing strategy

- `dotnet build` against the renamed `Namager.Sonulab.slnx` (surfaces any missed
  `using`/`ProjectReference`/`x:Class`/`LogicalName`).
- `dotnet test` — all 471 tests green (renames don't change the count) after updating
  the single expectation in `UpdateCheckServiceTests`.
- Feedback worker: manual post-deploy check (§4 step 7).
- Manual smoke: launch the app → title reads `Namager for Sonulab v…`; labels resource
  loads; Tone3000 re-auth lands in `%APPDATA%\Namager\`; installer builds, shows the
  new name + regenerated art, installs to `Programs\Namager.Sonulab`, and coexists
  with (until manually removed) the old ToneManager install.
