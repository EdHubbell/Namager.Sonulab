# Validation — usage telemetry `/ping`

## Run the script

```powershell
pwsh tools/Validate-Telemetry.ps1
```

That covers every automatable case: the twelve HTTP validation rows and the D1 double-count
check. It prints PASS/FAIL per row and exits non-zero if anything failed, so it can gate a
deploy. Needs PowerShell 7+ (`pwsh`, not Windows PowerShell 5).

Useful switches:

| Switch | Effect |
|---|---|
| `-Cleanup` | Deletes the script's test rows from D1 when finished. |
| `-SkipDb` | Skips the D1 queries. Use before `wrangler d1 create` has been run. |
| `-IncludeRateLimit` | Also runs the rate-limit case — **see the warning below**. |
| `-TestGuid <guid>` | Use a different install ID (e.g. to inspect a real one). |
| `-Url <url>` | Point at a different worker. |

⚠️ **`-IncludeRateLimit` is off by default on purpose.** It deliberately trips the 20/hour cap,
and that cap is **per IP, not per install**. If you run it before the real-pedal end-to-end
check, your genuine ping gets a 429 and lands no row — which looks exactly like a broken
feature. Run it last, or wait an hour afterwards. (Nothing is actually lost: the client leaves
the day unrecorded and retries next launch.)

A normal full pass, in order:

```powershell
pwsh tools/Validate-Telemetry.ps1              # rows 1-13
# ... then the two manual checks below ...
pwsh tools/Validate-Telemetry.ps1 -IncludeRateLimit -Cleanup
```

## The two checks the script cannot do

**Feedback-route regression.** Use **Send Feedback** in the app and confirm a `user-feedback`
issue appears on `EdHubbell/Namager.Sonulab`. The `/ping` work put a path router in front of a
live endpoint that shipped copies of the app depend on. Do not skip this one.

**Real end-to-end.** Needs a release-versioned build and a physical pedal:

```powershell
dotnet build src/Namager.App -c Release -p:Version=9.9.9
```

Run the exe from `src/Namager.App/bin/Release/net10.0/` and connect the pedal. A `-dev` version
will **not** ping, by design — so a plain `dotnet run` looks like a failure. Then:

```powershell
npx wrangler d1 execute namager-usage --remote --command "SELECT * FROM installs WHERE app_version='9.9.9'"
```

Expect one row, `active_days = 1`, `last_transport = usb`. Remove it afterwards:

```powershell
npx wrangler d1 execute namager-usage --remote --command "DELETE FROM pings WHERE app_version='9.9.9'; DELETE FROM installs WHERE app_version='9.9.9'"
```

## What the script checks

| # | Case | Expect |
|---|---|---|
| 1 | Happy path | `204` |
| 2 | Duplicate same day | `204` (accepted, but must add no row — see 13) |
| 3 | Not POST | `405` |
| 4 | Wrong content type | `415` |
| 5 | Bad JSON | `400` |
| 6 | Null JSON | `400` |
| 7 | Bad GUID | `400` |
| 8 | Oversized `appVersion` (21 chars) | `400` |
| 9 | Blank `fw` | `400` |
| 10 | Bad `transport` | `400` |
| 11 | Non-string `appVersion` | `400` |
| 12 | Missing `transport` | `400` |
| 13 | Duplicate did not double-count | `active_days = 1` **and** exactly one `pings` row |
| 14 | Rate limit (opt-in) | a `429` within 30 requests |

Row 13 is the one that matters most: `active_days` drives the retention number this whole
feature exists to produce, and it must equal the `pings` row count. Two pings on one day must
leave exactly one of each.

## Running a single case by hand

If you need to poke one case manually, in **Git Bash**:

```bash
URL=https://namager-sonulab-feedback.ed-eed.workers.dev
GUID=8f3c1e64-0000-4000-8000-000000000001
J='content-type: application/json'

curl -sSi -X POST $URL/ping -H "$J" \
  -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"
```

Two traps that cost real time:

- **Use `-sSi`, not `-si`.** Plain `-s` silences *error messages* as well as the progress meter,
  so a failed request prints absolutely nothing and looks like a hang.
- **Run the variable block first, in the same shell.** If `$URL` is empty, curl gets `/ping` as
  the URL and rejects it as a bad hostname — silently, if you used `-si`.

These commands are bash syntax. In PowerShell the `$VAR` expansion and `\"` escaping behave
differently and you will get confusing 400s that are your shell's fault, not the worker's. That
is the main reason the script exists.

## Interpreting a failure

| Response | Meaning |
|---|---|
| `204` | Working. |
| `502` | Worker is deployed but D1 isn't reachable — schema not applied, or `database_id` is still `REPLACE_AFTER_D1_CREATE` in `wrangler.toml`. |
| `400 invalid name` | You're hitting the **old** worker: `/ping` is falling through to the feedback handler because the new code isn't deployed. |
| `curl: (6) Could not resolve host` | Typo in the URL, or no network. |
| Nothing at all | `-s` without `-S`, almost certainly with an empty `$URL`. |

Setup steps (create the database, apply the schema, deploy) are in
`infra/feedback-worker/README.md`.
