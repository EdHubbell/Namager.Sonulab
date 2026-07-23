# Validation — usage telemetry `/ping`

Run against the deployed worker. Set `URL` first:

```bash
URL=https://namager-sonulab-feedback.ed-eed.workers.dev
GUID=8f3c1e64-0000-4000-8000-000000000001
J='content-type: application/json'
```

Each row states the exact command and the exact expected status.

| # | Case | Command | Expect |
|---|---|---|---|
| 1 | Happy path | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `204` |
| 2 | Duplicate same day | repeat command 1 | `204` (no new row — verify in #12) |
| 3 | Not POST | `curl -si $URL/ping` | `405` |
| 4 | Wrong content type | `curl -si -X POST $URL/ping -H 'content-type: text/plain' -d 'x'` | `415` |
| 5 | Bad JSON | `curl -si -X POST $URL/ping -H "$J" -d 'not json'` | `400` |
| 6 | Null JSON | `curl -si -X POST $URL/ping -H "$J" -d 'null'` | `400` |
| 7 | Bad GUID | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"nope\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `400` |
| 8 | Oversized appVersion | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"123456789012345678901\",\"fw\":\"2.5.1\",\"transport\":\"usb\"}"` | `400` |
| 9 | Missing fw | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"transport\":\"usb\"}"` | `400` |
| 10 | Bad transport | `curl -si -X POST $URL/ping -H "$J" -d "{\"installId\":\"$GUID\",\"appVersion\":\"1.2.0\",\"fw\":\"2.5.1\",\"transport\":\"ble\"}"` | `400` |
| 11 | Rate limit | run command 1 twenty-one times in a minute | final one `429` |

- [ ] **12. Duplicate did not double-count.** After commands 1 and 2:

```bash
npx wrangler d1 execute namager-usage --remote \
  --command "SELECT active_days FROM installs WHERE install_id='$GUID'"
npx wrangler d1 execute namager-usage --remote \
  --command "SELECT COUNT(*) FROM pings WHERE install_id='$GUID'"
```

Expect `active_days = 1` from the first query, and `COUNT(*) = 1` from the second (exactly one row
in `pings` for that install/day).

- [ ] **13. Feedback route regression.** Submit feedback from the app's Send Feedback dialog
  (or POST to the bare `$URL`) and confirm a `user-feedback` issue appears on
  `EdHubbell/Namager.Sonulab`. `/` must be unaffected by the router change.

- [ ] **14. Real end-to-end.** Run a **release-versioned** build (a `-dev` build will not ping
  by design), connect the pedal, then confirm a row landed:

```bash
npx wrangler d1 execute namager-usage --remote --command "SELECT * FROM installs"
```
