# NAMager worker

Two routes on one worker:

- **`/`** — the app's Send Feedback dialog. Receives `{ name, email, message, appVersion, os,
  website }` and creates a GitHub issue labeled `user-feedback`. `website` is a honeypot.
- **`/ping`** — the anonymous usage ping. Receives `{ installId, appVersion, fw, transport }`
  and records it in the `namager-usage` D1 database. See `PRIVACY.md`.

**This worker is deployed manually, never by CI.**

## One-time setup (Ed)

1. **Create the fine-grained GitHub PAT**
   github.com → Settings → Developer settings → Fine-grained tokens → Generate new token.
   - Repository access: *Only select repositories* → `Namager.Sonulab`
   - Permissions: *Issues: Read and write*. Nothing else.
   - Expiration: 1 year (calendar a renewal reminder).
   Copy the token.

2. **Create the issue label** (once):
   ```
   gh label create user-feedback --repo EdHubbell/Namager.Sonulab --color F9D0C4 --description "Submitted from the app's Send Feedback dialog"
   ```

3. **Deploy the worker** (needs Node; no install required):
   ```
   cd infra/feedback-worker
   npx wrangler login          # first time only: opens browser to your Cloudflare account
   npx wrangler deploy
   npx wrangler secret put GITHUB_TOKEN    # paste the PAT when prompted
   ```
   `deploy` prints the live URL, e.g. `https://namager-sonulab-feedback.<your-subdomain>.workers.dev`.

   Note: until the secret is set, the endpoint returns 502 — harmless, since nobody has the URL yet.

4. **Sync the app**: if the printed URL differs from `FeedbackService.EndpointUrl`
   (`src/Namager.App/Services/FeedbackService.cs`), update that constant and commit.

5. **Rate-limit backstop** (recommended): Cloudflare dashboard → Workers & Pages →
   namager-sonulab-feedback → Settings → add a rate limiting rule, e.g. 10 requests per
   minute per IP. (The worker also self-limits to 5/hour/IP, best-effort.)

## Smoke tests

Local (`npx wrangler dev`, then against http://localhost:8787) or against the live URL:

```bash
# happy path -> 201 and a new issue appears
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"Test","email":"t@example.com","message":"smoke test - please close","appVersion":"0.0.0","os":"curl","website":""}'

# honeypot filled -> 201 but NO issue created
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"Bot","email":"b@example.com","message":"spam","website":"http://spam"}'

# bad email -> 400
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"T","email":"nope","message":"x","website":""}'

# GET -> 405
curl -si <URL>
```

(For `wrangler dev`, put the PAT in `infra/feedback-worker/.dev.vars` as
`GITHUB_TOKEN=...` — that file is gitignored, never commit it.)

## Usage telemetry (D1)

One-time setup:

```
npx wrangler d1 create namager-usage         # paste database_id into wrangler.toml
npx wrangler d1 execute namager-usage --remote --file schema.sql
npx wrangler deploy
```

### Reporting queries

Run with `npx wrangler d1 execute namager-usage --remote --command "<sql>"`.

```sql
-- Monthly actives: installs that connected a pedal in the last 30 days
SELECT COUNT(*) FROM installs WHERE last_seen >= date('now','-30 days');

-- Retention: of installs first seen 30-60 days ago, how many are still connecting?
SELECT COUNT(*) FILTER (WHERE last_seen >= date('now','-14 days')) * 1.0 / COUNT(*)
FROM installs WHERE first_seen BETWEEN date('now','-60 days') AND date('now','-30 days');

-- New installs per day
SELECT first_seen, COUNT(*) FROM installs GROUP BY first_seen ORDER BY first_seen DESC;

-- Does anyone use WiFi, and regularly?
SELECT transport, COUNT(DISTINCT install_id) AS installs, COUNT(*) AS days
FROM pings WHERE day >= date('now','-60 days') GROUP BY transport;

-- App version and firmware spread
SELECT app_version, COUNT(*) FROM installs GROUP BY app_version ORDER BY 2 DESC;
SELECT fw_version,  COUNT(*) FROM installs GROUP BY fw_version  ORDER BY 2 DESC;

-- How engaged are the returning users? (active days per install)
SELECT active_days, COUNT(*) FROM installs GROUP BY active_days ORDER BY active_days;
```

**Reading these honestly:** these counts include only people who *connected a pedal*. Anyone who
downloaded the app and never plugged in is invisible here — that top of the funnel is the release
asset download count on GitHub Releases, which is a separate number.
