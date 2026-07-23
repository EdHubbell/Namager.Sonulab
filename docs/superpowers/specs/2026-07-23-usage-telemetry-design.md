# Usage Telemetry — Design Spec

**Date:** 2026-07-23
**Status:** Approved for planning

## Goal

Answer one question with data: **is anyone actually using NAMager, and do they come back?**
Without that, there is no basis for deciding which features are worth building.

The app sends one anonymous ping per install per UTC day, fired only when a pedal successfully
connects. A Cloudflare Worker records it in D1. Three numbers come out:

1. **Monthly actives** — how many distinct installs connected a pedal in the last 30 days.
2. **Retention** — of the installs first seen 30–60 days ago, how many are still connecting.
3. **Version & transport spread** — which app versions and device firmwares are in the wild,
   and whether anyone uses the WiFi transport (which is buggy and expensive to maintain).

This supersedes the "Out of scope: … telemetry" line in
`2026-07-16-distribution-and-feedback-design.md`.

Out of scope: feature-level event tracking, crash reporting, per-user identity or registration,
a settings UI, and any dashboard beyond SQL queries run by hand.

## Decisions taken (and why)

| Decision | Choice | Rationale |
|---|---|---|
| Signal | Active usage over time | Download counts can't distinguish a real user from someone who installed once and deleted it. |
| Identity | Persistent random install GUID | The only option that yields honest retention. Reset by deleting one file. |
| Trigger | First **successful** pedal connect per app run | Users who never connect a pedal are not of interest. Also removes the need for a fallback timer and its race. |
| Consent | On by default, no opt-out toggle | The app is a network client already (update check), and the roadmap includes a NAMager backend for sharing tones across hardware. Disclosed in `PRIVACY.md` and the README rather than gated behind a checkbox. |
| Backend | Cloudflare Worker + D1 | Reuses the deploy path, secrets pattern, and rate-limit backstop already proven by the feedback worker. At this scale D1 gives exact, unbounded retention; Analytics Engine is built for volume this project doesn't have and caps retention at ~90 days. |

## 1. The privacy contract

Exactly one POST per install per UTC day. The entire payload:

```json
{ "installId": "8f3c1e64-…", "appVersion": "1.2.0", "fw": "2.5.1", "transport": "usb" }
```

Nothing else is sent — no name, email, IP, preset or amp names, file paths, or device serials.
The worker reads `cf-connecting-ip` **only** for its in-memory rate limit (same as the feedback
worker) and never writes it to D1.

This list is short enough to print verbatim in `PRIVACY.md` and the README, which is the point:
the disclosure and the payload are the same four fields, and they can be checked against each
other by anyone reading the source.

## 2. Client — `UsagePingService`

### State file

`%APPDATA%\Namager\usage.json`, alongside the existing `tone3000.json` (same convention as
`T3kConfig`/`T3kTokenStore`):

```json
{ "installId": "8f3c1e64-…", "lastPingUtc": "2026-07-23" }
```

- `installId` is a `Guid.NewGuid()` generated and persisted on first use.
- Deleting the file resets the identity. This is the user's escape hatch and is documented.
- A missing, unreadable, or corrupt file is treated as first run: mint a new GUID, carry on.
  A failure to *write* the file is non-fatal — the ping still sends, it just isn't day-gated.

### Service contract

`UsagePingService` mirrors `UpdateCheckService`'s contract exactly, because it has the same job
(best-effort network call that must never affect the app):

- 10-second timeout; `HttpMessageHandler` injectable for tests.
- **Never throws.** Every failure mode — offline, DNS, 4xx, 5xx, malformed, cancelled — is a
  silent no-op.
- **Never blocks the UI.** Fire-and-forget from the caller.
- `lastPingUtc` is written **only after a successful POST**, so a week offline doesn't silently
  consume those days. The cost is one wasted request per launch while offline, which is free.

### Day gating

`ShouldPing(DateOnly todayUtc)` is a pure function — `lastPingUtc != todayUtc` — so the gating
logic is unit-testable without a clock, a file, or a socket. The current date is passed in.

## 3. Trigger point

In `ConnectionViewModel.ConnectAsync`, after `state.Connected` is confirmed true and
`Status`/`Repository`/`Reorder` are set up:

- Fires on the **first successful connect of the app run** only; subsequent reconnects in the
  same run do not re-ping. A run-scoped flag enforces this.
- A failed or never-attempted connect sends nothing.
- Values come straight off `SessionState`: `fw` from `state.Device!.Version`, `transport` from
  `state.Transport`.
- `SessionState.Transport` is a free-form `string?` carrying `ILinkProvider.Name` — today
  `"USB"` (`SerialLinkProvider`) or `"WiFi"` (`WifiLinkProvider`). The client **lowercases it**
  to `usb` / `wifi` before sending, so the wire format is stable even if a provider's display
  name is reworded. A null or unrecognised value is sent as `unknown` rather than dropping the
  ping.
- Dispatched fire-and-forget so a slow network never delays the connected UI.

### What the numbers mean (and don't)

"Monthly actives" means *installs that connected a pedal at least once in the window*. It
deliberately excludes people who downloaded the app and never plugged anything in. That top of
the funnel is still visible for free via GitHub's release-asset download counts, so the two
figures together give both interest and real use — with no extra code.

## 4. Worker — new `/ping` route

Added to the existing `infra/feedback-worker` worker (renamed in docs to "the NAMager worker";
the deployed name and URL stay put).

- **`/` stays byte-for-byte as it is.** Installed copies of the app POST feedback to the bare
  URL; changing that route would break them. A path check routes `/ping` to the new handler and
  everything else to the existing feedback handler.
- `/ping` validation, following the feedback route's defensive shape:

| Condition | Response |
|---|---|
| Not POST | 405 |
| `content-type` not JSON | 415 |
| Unparseable / non-object JSON | 400 |
| `installId` fails a strict GUID regex | 400 |
| `appVersion` missing, non-string, or > 20 chars | 400 |
| `fw` missing, non-string, or > 20 chars | 400 |
| `transport` not one of `usb` \| `wifi` \| `unknown` | 400 |
| Over the per-IP rate limit | 429 |
| Accepted | 204 |

  The GUID regex matters: it rejects junk and makes the table hard to pollute with invented IDs.
- Per-IP in-memory rate limit as on the feedback route, with the Cloudflare dashboard rule as
  the real backstop.
- The worker never returns row counts, IDs, or query results — `/ping` is write-only from the
  client's perspective.

## 5. D1 schema

```sql
CREATE TABLE pings (              -- append-only; answers questions not yet thought of
  install_id  TEXT NOT NULL,
  day         TEXT NOT NULL,      -- UTC date, YYYY-MM-DD
  app_version TEXT NOT NULL,
  fw_version  TEXT NOT NULL,
  transport   TEXT NOT NULL,
  PRIMARY KEY (install_id, day)
);

CREATE TABLE installs (           -- rolled-up state, upserted on each accepted ping
  install_id     TEXT PRIMARY KEY,
  first_seen     TEXT NOT NULL,
  last_seen      TEXT NOT NULL,
  active_days    INTEGER NOT NULL DEFAULT 0,
  app_version    TEXT NOT NULL,   -- most recent seen
  fw_version     TEXT NOT NULL,   -- most recent seen
  last_transport TEXT NOT NULL
);
```

`PRIMARY KEY (install_id, day)` is the server-side backstop for the client's day gate: a
reinstall, a clock change, or a replayed request cannot double-count a day. An insert that
violates it is ignored (`INSERT OR IGNORE`), and `installs.active_days` increments **only** when
the `pings` insert actually added a row — otherwise a hand-replayed request could inflate
`active_days` without adding a `pings` row.

Keeping the raw `pings` table means any metric thought of later is a SQL query rather than a
worker redeploy and a wait for new data.

## 6. Reporting

No dashboard. The worker README documents the queries, run via `npx wrangler d1 execute`:

```sql
-- Monthly actives
SELECT COUNT(*) FROM installs WHERE last_seen >= date('now','-30 days');

-- Retention: of installs first seen 30-60 days ago, how many are still connecting?
SELECT COUNT(*) FILTER (WHERE last_seen >= date('now','-14 days')) * 1.0 / COUNT(*)
FROM installs WHERE first_seen BETWEEN date('now','-60 days') AND date('now','-30 days');

-- New installs per week
SELECT first_seen, COUNT(*) FROM installs GROUP BY first_seen ORDER BY first_seen DESC;

-- Does anyone use WiFi, and regularly?
SELECT transport, COUNT(DISTINCT install_id) AS installs, COUNT(*) AS days
FROM pings WHERE day >= date('now','-60 days') GROUP BY transport;

-- App version and firmware spread
SELECT app_version, COUNT(*) FROM installs GROUP BY app_version ORDER BY 2 DESC;
SELECT fw_version,  COUNT(*) FROM installs GROUP BY fw_version  ORDER BY 2 DESC;
```

## 7. Disclosure

- **`PRIVACY.md`** at the repo root: the four fields verbatim, when the ping fires (only on a
  successful pedal connect, at most once a day), what is never sent, that there is no opt-out
  toggle, and that deleting `%APPDATA%\Namager\usage.json` resets the install ID.
- **README section** — a short paragraph linking to `PRIVACY.md`.

## 8. Testing

All logic worth testing is pure and offline; no hardware and no network.

**`Namager.App.Tests`**
- Round-trips `usage.json`; `installId` is stable across loads.
- First run mints a GUID and persists it.
- Missing / corrupt / unreadable file → treated as first run, no throw.
- `ShouldPing` gating: same UTC day → false; new day → true.
- Successful POST writes `lastPingUtc`; **failed POST leaves it unchanged** (the offline case).
- Never-throws contract: handler throws, times out, and returns 500 → `PingAsync` returns
  cleanly in all three.
- Payload shape: exactly the four documented keys, correct values, nothing extra.
- Transport normalization: `"USB"` → `usb`, `"WiFi"` → `wifi`, null/unrecognised → `unknown`.
- Read-only-filesystem case: write failure doesn't prevent the POST or throw.

**`ConnectionViewModel` tests**
- Successful connect → exactly one ping, carrying the session's `fw` and `transport`.
- Failed connect → no ping.
- Two successful connects in one run → still exactly one ping.

**Worker**
- A table test per row of the validation table in §4, including the GUID regex.
- Duplicate `(install_id, day)` → accepted (204) but adds no `pings` row and does not increment
  `active_days`.
- `/` still behaves exactly as before (regression guard on the feedback route).

## 9. Deployment sequence

1. Create the D1 database; apply the schema migration.
2. Bind D1 to the worker in `wrangler.toml`; `npx wrangler deploy`.
3. Confirm `/ping` accepts a hand-rolled `curl` and `/` still files a feedback issue.
4. Only then ship the app change, so the first real pings have somewhere to land.
