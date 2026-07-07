# Tone3000 integration ("Browse Tones" tab) — design

**Date:** 2026-07-07
**Status:** Approved in brainstorm (card-grid mockup in `.superpowers/brainstorm/17667-1783433208/content/`, gitignored).

## Problem

Getting tones onto the pedal today means hunting files in a browser, downloading them somewhere,
and picking them through a file dialog. Tone3000 (tone3000.com) hosts NAM amp captures and IRs
behind a documented REST API (v1) with OAuth. Users should browse, search, and send tones to the
pedal from inside the app — with their Tone3000 account, and with the SSMD metadata block
recording provenance automatically.

## Decisions (made in brainstorm)

- **Approach:** new UI-free `Sonulab.Tone3000` class library + a new app tab. No WebView, no
  new NuGet packages beyond the BCL.
- **Nav:** a muted **BROWSE TONES** section header in the nav pane below the device tabs;
  **Tone3000** is its first entry (future integrations slot under the same header).
- **Browse scope:** full catalog search (text + format/gear filters) plus Favorites and
  Downloaded views.
- **Results:** card grid (tone image, title, author, download count), pagination; tone detail
  panel on the right with per-model "Send to pedal".
- **To-pedal flow:** download to `NAMFiles\Tone3000\`, then open the existing Amps/IRs upload
  panel prefilled (name from file; SSMD notes = "«tone title» by «author» (Tone3000)"; SSMD
  url = the tone's page URL).
- **Auth:** OAuth 2.0 + PKCE, system browser + localhost loopback. **The `t3k_cs_` secret key
  is never used or shipped by the app** — publishable `client_id` only (their documented
  client-side rule). Refresh token persisted with DPAPI so sign-in survives restarts.
- **Delivery:** ONE spec, TWO plans — Plan 1 "library + auth + live probe", Plan 2 "tab UI +
  handoff". Ed already has API keys, so the live probe runs day one.

## API surface used (from tone3000.com/api, v1)

| Purpose | Endpoint |
|---|---|
| Authorize / token / refresh | `GET /api/v1/oauth/authorize`, `POST /api/v1/oauth/token` |
| Signed-in user | `GET /api/v1/user` |
| Catalog search | `GET /api/v1/tones/search` (`gears`, `format`, `architecture`, `calibrated`, text query; paginated) |
| Tone detail | `GET /api/v1/tones/{id}` |
| Models for a tone | `GET /api/v1/models?tone_id={id}` — each model has `model_url`, `name` |
| Favorites / Downloaded | `GET /api/v1/tones/favorited`, `GET /api/v1/tones/downloaded` |
| (Un)favorite | `PUT`/`DELETE /api/v1/tones/{id}/favorite` |

Pagination envelope: `data`, `page`, `page_size`, `total`, `total_pages`. Rate limit: 100
req/min default. Downloads: fetch `model_url` with the user's Bearer access token.

**Assumptions to verify in the day-one probe (Plan 1):** IRs are served through the same
models endpoint with a format filter and `.wav` `model_url`s; `model_url` accepts the user's
OAuth access token (not the server secret); tones carry an image URL usable for cards; the
exact search-parameter names/values. The probe's findings are recorded in
`docs/tone3000-api-findings.md` and lock the JSON mapping before Plan 2.

## 1. `src/Sonulab.Tone3000` (no UI; referenced by Sonulab.App only)

- **`T3kAuth`** — PKCE flow: verifier/challenge, system browser to `/oauth/authorize` with
  `redirect_uri=http://127.0.0.1:{port}/callback` on a one-shot `HttpListener`, code exchange,
  proactive refresh before expiry. Browser-opener and listener are injectable seams (tests run
  the whole flow with fakes). Exposes `SignInAsync`, `SignOut`, `GetAccessTokenAsync`
  (auto-refresh), `IsSignedIn`, and the signed-in username.
- **`T3kTokenStore`** — refresh token at `%APPDATA%\StompStationManager\tone3000.token`,
  encrypted via DPAPI (`ProtectedData`, CurrentUser). Delete = sign out.
- **`T3kClient`** — typed methods for every endpoint in the table; single injectable
  `HttpMessageHandler`; pagination envelope handled; failures map to `T3kException` with
  user-honest messages (401 → sign-in-again, 429 → rate-limited, network → retryable).
- **`T3kDownloader`** — Bearer download of `model_url` to `NAMFiles\Tone3000\<safe-name>.<ext>`
  (filename sanitized; extension from the model's format, `.nam`/`.wav`); atomic temp+rename;
  returns existing file without re-downloading.
- **`T3kJson`** — ALL JSON contracts in one file, lenient parsing (unknown fields ignored,
  optionals null) since the API is explicitly v1-unstable.
- **`T3kConfig`** — loads `%APPDATA%\StompStationManager\tone3000.json` (see §4).

## 2. The tab (Plan 2)

`Tone3000ViewModel` + `Tone3000View`, registered under the new nav section.

- **States:** signed-out card ("Connect your Tone3000 account" + Sign in button) → browse UI
  ("signed in as {user}" + Sign out in a corner).
- **Browse UI:** search box (debounced), format chips NAM/IR, gear filter, in-page view
  switch Search | Favorites | Downloaded; card grid (image with placeholder fallback, title,
  author, ⬇ count), pager; selecting a card fills the right-hand detail panel: title, author,
  gear, ★ count, description, "Open on tone3000.com" link (existing `OpenDetailsUrl` pattern),
  favorite toggle, and the tone's models each with **Send to pedal**.
- **Works offline from the pedal:** browsing/downloading never touches the device; "Send to
  pedal" is disabled (tooltip) until connected AND writes are allowed — same `CanMutate`
  discipline as the device tabs.
- **Styling:** Studio-warm tokens/classes throughout (cards use `card`, labels `section-label`,
  accent chips/buttons `accent-outline`).

## 3. The handoff (Plan 2; the only touch on existing code)

- `AmpListViewModel` and `IrListViewModel` gain a public method
  `BeginUploadPrefilled(string path, string? notes, string? url)`; the existing
  `BeginUploadCommand` path delegates to it with nulls, so behavior today is byte-identical
  (existing tests unmodified). (A separate method, not command-parameter overloading — MVVM
  Toolkit relay commands take a single parameter.)
- New `MainWindowViewModel.NavigateToUpload(UploadKind kind, string path, string? notes, string? url)`:
  switches the nav selection to Amps or IRs and invokes the prefilled upload flow. The Tone3000
  VM calls it after a successful download; `.nam` → Amps, `.wav` → IRs (model format field,
  extension fallback).
- SSMD result: uploads that came from Tone3000 carry the tone's page URL and an attribution
  note without the user typing anything. (IR slots have no SSMD — prefill applies to name only
  until IR metadata exists.)

## 4. Keys & configuration

`%APPDATA%\StompStationManager\tone3000.json` (never committed; the repo carries
`tone3000.json.example` and a `.gitignore` entry):

```json
{
  "publishable_key": "t3k_pk_...",
  "secret_key": "t3k_cs_...  (OPTIONAL - never read by the app; dev probe tool only)",
  "redirect_port": 0
}
```

- `publishable_key` — the OAuth `client_id`. Required for sign-in.
- `secret_key` — **the app never reads this field.** It exists so the dev-only probe tool
  (`tools/T3kProbe`, Plan 1) can explore server-side endpoints during contract verification.
  Stays on Ed's machine.
- `redirect_port` — 0 = pick a free port (their registration allows loopback redirects;
  verify during the probe whether a fixed port must be registered — if so, set it here).
- Missing/invalid file → the tab shows a friendly "add your Tone3000 keys" card with the path.

## 5. Error handling

- Every network/auth failure surfaces as a message inside the tab (banner or detail-panel
  text) — never a crash, never a silent empty grid ("No results" ≠ "request failed").
- 429 responses show a rate-limit message; search debouncing (300 ms) keeps normal typing far
  under 100 req/min.
- Downloads are atomic; a failed download leaves no partial file; "Send to pedal" after a
  failed download does nothing but show the error.
- OAuth callback timeout (user closes browser): sign-in returns to the signed-out state with
  a retry message; the listener always shuts down.

## 6. Testing

- **Library:** PKCE flow end-to-end with fake browser/listener (correct challenge, state
  echo, token exchange body, refresh); token store round-trip (DPAPI real, CurrentUser);
  client methods against canned JSON incl. pagination and every error mapping; downloader
  atomicity + skip-existing + sanitization.
- **App (Plan 2):** VM against a fake `IT3kClient`/`IT3kDownloader` (search/filter/pagination
  state, sign-in state flips, send-to-pedal dispatch); handoff into the existing upload-panel
  seams (prefill lands in `UploadNotes`/`UploadUrl`; null-prefill behavior unchanged).
- **Live probe (Plan 1, dev tool):** `tools/T3kProbe` verifies the §"Assumptions" list with
  Ed's real keys; findings recorded in `docs/tone3000-api-findings.md`.

## Out of scope

- Other tone sites (the nav header leaves room; nothing else built).
- Audio preview/playback, tone uploads TO Tone3000, comments/ratings.
- IR SSMD metadata (unchanged prior decision).
- Any use of the secret key inside the shipped app.
