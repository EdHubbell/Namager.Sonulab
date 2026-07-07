# Tone3000 API findings (live probe, 2026-07-07)

Probe: `tools/T3kProbe`, run with the account's server (secret-key) credential via
`dotnet run --project tools/T3kProbe -- deluxe`. Keys masked (`t3k_cs_…a5`, never printed in
full). 6 live requests total, well under the documented 100 req/min limit; no retries, no 429s.

## Verified endpoints & shapes

### GET /api/v1/tones/search — actual query params accepted, actual response fields
- Query params used and confirmed working: `query` (text search — assumption was correct),
  `format` (`nam` / `ir`), `page`, `page_size`. Returned `HTTP 200` for both `format=nam` and
  `format=ir`.
- Response envelope: `{ data: [...], page, page_size, total, total_pages }` — matches
  `T3kPage<T>` exactly (assumption correct).
- Per-tone fields actually present: `id` (int), `title`, `description`, `tags` (array of
  `{id, name}` or `{name}`), `makes` (array of `{name}` or `{id, name}`), `models_count`,
  `a1_models_count`, `a2_models_count`, `irs_count`, `custom_models_count`,
  `favorites_count`, `downloads_count`, `created_at`, `updated_at`, `images` (array of URL
  strings, possibly `null`), `user_id`, `user` (nested `{id, username, avatar_url, url}`),
  `url`, `format` (`"nam"` / `"ir"`), `gear` (`"amp-cab"`, `"cab"`, …).
- `tags`, `makes`, and the `*_count` fields (other than downloads/favorites) are not modeled
  in `T3kTone` — out of scope for this task; lenient parsing ignores them safely. Flagged for
  Plan 2 in case the browse UI wants tag chips or per-format counts.

### GET /api/v1/tones/{id} — actual fields
Same shape as a search result plus: `is_public`, `license` (`"t3k"`), `sizes`
(`["standard"]`), `links` (array, empty in the sample). Author/user, images, url, counts all
match the search-result shape above (nested `user`, plural `images`, `url` not `page_url`,
`downloads_count`/`favorites_count`).

### GET /api/v1/models?tone_id= — actual fields; model_url form
- Envelope: same `{ data, page, page_size, total, total_pages }` shape — confirmed.
- Per-model fields: `id`, `tone_id`, `user_id`, `created_at`, `updated_at`, `name`,
  `model_url`, `size` (`"standard"`), `architecture_version` (`"1"`). **No `format` field**
  on the model object — see Divergences below.
- `model_url` is an **absolute** URL on `www.tone3000.com`
  (`https://www.tone3000.com/api/v1/models/{id}/download/{slug}.nam`), not a bare path or a
  separate CDN host, and carries no signed query-string token in this sample.

### Download behavior — status, content-type, whether Bearer was required, first bytes
- `GET` on `model_url` with the same secret-key `Bearer` header used for the rest of the
  probe: `HTTP 200`, `Content-Type: text/plain; charset=UTF-8`, `283190` bytes.
- Bearer WAS required/accepted (no separate download token/redirect observed) — the same
  credential that authenticates search/detail calls also authenticates the download.
- First bytes: `7B 22 76 65 72 73 69 6F 6E 22 3A 22 30 2E 35 2E` = `{"version":"0.5.` — NAM
  files are JSON, confirming the spec's assumption ('{' prefix for `.nam`).

## IR support
`format=ir` search returned real tone results (not a different shape) — IRs are just tones
with `format: "ir"` and `gear: "cab"`. Sample titles/tags confirm cab-IR content (e.g. "1966
Deluxe Reverb 1x12", tags including `"ir"`, `"cab"`). The probe only drills into the **first
NAM result's** models/download branch, so an actual IR model's `model_url` extension (expected
`.wav` per spec) and its download bytes (`RIFF` prefix) were **not directly exercised** —
**verify during Plan 2 live checklist.**

## Images
Card art lives in the `images` field — a **plural array** of URL strings (can be `null` or
absent, e.g. two of the three `format=ir` search results had `"images": null`). There is no
singular `image_url` field. `T3kTone.ImageUrl` is now a computed convenience property that
returns the first entry or `null`.

## Divergences from spec assumptions

| Assumed (Task 1 DTO) | Actual (live) | What changed in `T3kJson.cs` |
|---|---|---|
| `T3kTone.Author` — top-level `author` string | No top-level `author`; nested `user.username` | Added `T3kToneAuthor(string? Username)` + `User` property (maps to `user`); `Author` is now a computed `=> User?.Username` |
| `T3kTone.ImageUrl` — singular `image_url` string | Plural `images: string[]` (nullable) | Added `Images` (`IReadOnlyList<string>?`, maps to `images`); `ImageUrl` is now computed `=> Images?[0]` or null |
| `T3kTone.PageUrl` — `page_url` field | Field is named `url` | Added `[JsonPropertyName("url")]` on `PageUrl` |
| `T3kTone.Downloads` — `downloads` field | Field is `downloads_count` | Added `[JsonPropertyName("downloads_count")]` |
| `T3kTone.Stars` — `stars` field | Field is `favorites_count` | Added `[JsonPropertyName("favorites_count")]` |
| `T3kUser.Id` — `long` | `id` is a UUID **string** (`"ca1c3703-…"`) | Changed `Id` from `long` to `string?` (a `long` mapping would throw `JsonException` on every real response, silently nulling the whole object via `Parse<T>`'s catch) |
| `T3kModel.Format` — per-model `format` field | Not returned by `/api/v1/models` at all | No code change (already nullable, already lenient); doc comment added noting callers must derive the extension from `ModelUrl` or the parent tone's `Format` |
| `T3kTone.Id` / `T3kModel.Id` — `long` | Confirmed `long` (integer ids) — correct as-is | none |
| Query param `query` | Confirmed correct | none |
| Pagination envelope shape | Confirmed correct | none |

Regression tests added to `T3kJsonTests.cs` using sanitized real snippets from this run:
`Live_tone_search_shape_maps_via_convenience_accessors`, `Live_user_shape_has_string_id_not_long`,
`Live_model_shape_has_no_format_field`.

## Unverifiable with secret key
- `GET /api/v1/user` returned `HTTP 200` with the secret (server) key and identified the
  account (`edhubbell`) — so, contrary to the spec's caution that `/user` "may be
  OAuth-user-scoped," the secret key **was** sufficient here. This does not confirm OAuth
  PKCE user-token behavior (Task 3's `T3kAuth` flow) — that still needs a live check against
  a real browser-completed OAuth exchange in Plan 2's checklist, since the secret key and a
  user access token are different credentials that may have different scopes in practice.
- IR-specific model/download shape (extension, `RIFF` bytes) — not exercised by this probe
  run (see IR support above); verify during Plan 2 live checklist.
- Rate-limit headers (e.g. `X-RateLimit-*`) were not inspected — the probe only logs
  status/body, not response headers. Unknown whether/how the API surfaces remaining quota;
  verify during Plan 2 if a client-side backoff/UI indicator is wanted.

## Rate-limit observations
6 requests in one run, no delay between them, all returned promptly with `HTTP 200` — no
`429`s, no `Retry-After` observed. Far below the 100 req/min budget; nothing further to report
without a header-level or higher-volume probe (out of scope here).
