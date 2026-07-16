# Feedback worker

Receives `{ name, email, message, appVersion, os, website }` POSTs from the app's
Send Feedback dialog and creates a GitHub issue labeled `user-feedback` on
`EdHubbell/StompStationManager`. `website` is a honeypot — always empty from the real app.

**This worker is deployed manually, never by CI.**

## One-time setup (Ed)

1. **Create the fine-grained GitHub PAT**
   github.com → Settings → Developer settings → Fine-grained tokens → Generate new token.
   - Repository access: *Only select repositories* → `StompStationManager`
   - Permissions: *Issues: Read and write*. Nothing else.
   - Expiration: 1 year (calendar a renewal reminder).
   Copy the token.

2. **Create the issue label** (once):
   ```
   gh label create user-feedback --repo EdHubbell/StompStationManager --color F9D0C4 --description "Submitted from the app's Send Feedback dialog"
   ```

3. **Deploy the worker** (needs Node; no install required):
   ```
   cd infra/feedback-worker
   npx wrangler login          # first time only: opens browser to your Cloudflare account
   npx wrangler deploy
   npx wrangler secret put GITHUB_TOKEN    # paste the PAT when prompted
   ```
   `deploy` prints the live URL, e.g. `https://stompstation-feedback.<your-subdomain>.workers.dev`.

4. **Sync the app**: if the printed URL differs from `FeedbackService.EndpointUrl`
   (`src/Sonulab.App/Services/FeedbackService.cs`), update that constant and commit.

5. **Rate-limit backstop** (recommended): Cloudflare dashboard → Workers & Pages →
   stompstation-feedback → Settings → add a rate limiting rule, e.g. 10 requests per
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
