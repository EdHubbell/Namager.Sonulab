# Design: StompStationManager → ToneManager rebrand

**Date:** 2026-07-21
**Status:** Approved (design)
**Scope:** Rename/rebrand only. No architecture changes.

## Motivation

The app is being renamed from **StompStationManager** to **ToneManager** to reflect
a future in which it manages hardware devices from multiple vendors, not only the
Sonulab StompStation. This effort is the rename/rebrand groundwork *only* — it
unblocks multi-vendor work but does not implement it. Multi-vendor architecture is a
separate, later effort with its own spec.

## Decisions (locked)

- **Scope:** Rename/rebrand only. No abstraction seams, no second vendor.
- **Namespaces:** Keep all `Sonulab.*` C# projects, namespaces, and the
  `Sonulab.App.exe` assembly name **unchanged**. `Sonulab` is the pedal *vendor*, and
  keeping vendor code namespaced by vendor is the right shape for a multi-vendor
  future. Only product-facing names and app-identity plumbing change.
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

### 1. Product / branding strings
- `src/Sonulab.App/Views/MainWindow.axaml.cs` — window title → `"ToneManager v{AppInfo.Version}"`
- `src/Sonulab.Tone3000/T3kAuth.cs` — OAuth success page text → "…you can close this tab and return to ToneManager."
- `README.md` — title/prose → ToneManager; add one-line "Renamed from StompStationManager on 2026-07-21" note
- `CLAUDE.md` — header line ("StompStation Manager") and any prose references → ToneManager
- `docs/**` prose references (HARDWARE-VALIDATION-*, specs, plans) — updated where they name the product. Historical spec/plan filenames are left as-is; only in-body product-name prose is corrected where it reads as the current product. (Low priority; does not affect build or runtime.)
- `Sonulab.slnx` → **`ToneManager.slnx`** (solution = the app workspace). Projects referenced inside stay `Sonulab.*`.

### 2. GitHub repo references (`EdHubbell/StompStationManager` → `EdHubbell/ToneManager`)
- `src/Sonulab.App/Services/UpdateCheckService.cs`
  - releases URL `.../repos/EdHubbell/StompStationManager/releases/latest` → `ToneManager`
  - `UserAgent.ParseAdd("StompStationManager")` → `"ToneManager"`
- `tests/Sonulab.App.Tests/UpdateCheckServiceTests.cs` — expected `html_url` → new repo
  (this is the **only** test asserting the app repo/name; `FeedbackService` has no test
  asserting its endpoint URL, so changing `EndpointUrl` needs no test change)
- Git remote: `git remote set-url origin git@github.com:EdHubbell/ToneManager.git`, then
  `git push origin --all` (and `--tags` if any).

### 3. Feedback worker (rename worker + retarget repo)
- `infra/feedback-worker/wrangler.toml` — `name = "tonemanager-feedback"`
- `infra/feedback-worker/worker.js`
  - header comment (line 1–3) → ToneManager
  - `const REPO = 'EdHubbell/ToneManager'`
  - `'user-agent': 'tonemanager-feedback-worker'`
- `src/Sonulab.App/Services/FeedbackService.cs` — `EndpointUrl` →
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

### 4. Config path (`%APPDATA%\StompStationManager` → `ToneManager`) — re-authenticate
- `src/Sonulab.Tone3000/T3kConfig.cs` — path segment → `"ToneManager"`
- `src/Sonulab.Tone3000/T3kTokenStore.cs` — path segment → `"ToneManager"`
- `tools/T3kProbe/Program.cs` — comment referencing the path → `ToneManager`
- No migration code. First run after upgrade creates a fresh `ToneManager` folder; the
  user re-logs in to Tone3000 once.

### 5. Installer (`src/Sonulab.Installer/Package.wxs` + `.wixproj`)
- `Package Name` → `"ToneManager"`
- `MajorUpgrade DowngradeErrorMessage` → "A newer version of ToneManager is already installed."
- Shortcut `Name` → `"ToneManager"`
- `INSTALLFOLDER` `Name` → `"ToneManager"` (installs to `%LOCALAPPDATA%\Programs\ToneManager`)
- `RegistryValue` `Key` `Software\StompStationManager` → `Software\ToneManager`
- `ARPURLINFOABOUT` → `https://github.com/EdHubbell/ToneManager`
- Header comment referencing the install path → ToneManager
- **Keep unchanged:** the `UpgradeCode` GUID (`1431D009-…`) — changing it breaks
  in-place upgrades forever. Shortcut `Target` stays `Sonulab.App.exe` (App project /
  assembly name is unchanged).
- **Known wrinkle (accepted):** keeping the `UpgradeCode` while renaming `INSTALLFOLDER`
  means a major-upgrade over an old StompStationManager install may leave the old
  per-user folder behind. Acceptable given the effectively single-user base; a clean
  reinstall avoids it entirely.

### 6. Versioning / release notes
- csproj `<Version>` untouched.
- Announce the rename in the next GitHub Release description, dated 2026-07-21.
- One-line note in README recording the rename and date.

## Out of scope / explicitly not touched
- `Sonulab.*` namespaces, project names, `Sonulab.slnx`-referenced project identities,
  and the `Sonulab.App.exe` assembly/output name.
- The installer `UpgradeCode` GUID.
- Protocol code, transport code, distiller, and all runtime behavior.
- Any multi-vendor abstraction or second-vendor support.
- The device license/model identifiers `stompstation1` and the "StompStation"
  device-model references in code/tests/`FakePresetDevice` — these are vendor/product
  data, not the app name, and stay unchanged.
- `tests/Sonulab.Distill.Tests/goldens-corpus/*.nam.path.txt` — these record a
  historical absolute source path (`…\Buckdrivers\Sonulab\StompStationManager\…`) as
  golden provenance; they are left byte-for-byte unchanged so golden comparisons stay
  stable.

## Testing strategy
- `dotnet build` succeeds against the renamed `ToneManager.slnx`.
- `dotnet test` — all 312 tests green after updating the single test expectation in
  `UpdateCheckServiceTests` (the only test asserting the old repo/name).
- Feedback worker has no automated tests: verification is the manual post-deploy check
  (step 6 above).
- Manual smoke: launch the app, confirm the window title reads `ToneManager v…`, and
  confirm Tone3000 re-auth flows into the new `%APPDATA%\ToneManager` folder.
