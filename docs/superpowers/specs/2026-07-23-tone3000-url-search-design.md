# Tone3000 search bar: accept a tone URL or ID

**Date:** 2026-07-23
**Status:** approved (design)

## Problem

The Tone3000 API's text search does not surface everything the website shows. A tone that is
plainly visible at `https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141` cannot
reliably be found by typing its title into the tab's search box, so a tone the user is looking
straight at is unreachable from the app.

The website URL already carries an unambiguous identifier, and `IT3kClient.GetToneAsync(long id)`
already fetches a tone by that identifier. The gap is only that the search box has no way to say
"this is an ID, not a phrase".

Evidence that the trailing digits are the id — a real captured response
(`tests/Namager.Tone3000.Tests/T3kJsonTests.cs:62`):

```json
{ "id": 27365, ..., "url": "https://www.tone3000.com/tones/deluxe-65-27365" }
```

## Scope

Make the existing search box accept a Tone3000 tone URL or a bare numeric tone ID and jump
straight to that tone.

**Not in scope:** improving the API text search itself. This does not fix the API lagging the
website; it provides a reliable path around it when the user already has the page open.

## Design

### 1. `T3kSearchQuery` — new, `src/Namager.Tone3000/T3kSearchQuery.cs`

A pure static parser: no UI, no HTTP, no state. Lives in `Namager.Tone3000` alongside the other
no-UI, unit-tested pieces.

```csharp
public enum T3kQueryKind { Text, ToneId, BadLink }
public readonly record struct T3kQuery(T3kQueryKind Kind, long ToneId, string? Text);
public static class T3kSearchQuery { public static T3kQuery Parse(string? input); }
```

Rules, applied in order to the trimmed input:

1. **All digits** (1–18 of them, so it fits a `long`) → `ToneId`.
2. **Looks like a link** — starts with `http://` or `https://`, *or* contains `tone3000.com/`
   (so a pasted `www.tone3000.com/tones/…` without a scheme works, by prepending `https://`).
   Parse as a URI, then require all of:
   - host is `tone3000.com` or `www.tone3000.com`;
   - first path segment is `tones`;
   - the second path segment ends in digits — take the substring after the last `-`, or the
     whole segment if it has no `-` — and that substring parses as a `long`.

   All three hold → `ToneId`. Any fail → `BadLink`. Query strings, fragments and trailing
   slashes are ignored.
3. **Otherwise** → `Text`, carrying the trimmed input. This is today's behavior, unchanged.

Rationale for a separate parser rather than sniffing inside `T3kClient.SearchAsync`: routing
hidden behind a method named "search" has nowhere honest to report `BadLink`, and a pure
function is exhaustively table-testable in milliseconds.

### 2. `T3kError.NotFound` — `src/Namager.Tone3000/T3kException.cs`, `T3kClient.cs`

A 404 currently surfaces as the generic `"Tone3000 request failed (HTTP 404)."`. Add one enum
member and one arm to the `SendAsync` status switch so the tab can say something useful about a
dead ID. Nothing else branches on it; existing `T3kError` consumers are unaffected.

### 3. `Tone3000ViewModel.LoadAsync` — one new branch

Only the `T3kViewMode.Search` path changes; `Favorites` and `Downloaded` are untouched.

| `Parse(SearchText).Kind` | Behavior |
|---|---|
| `ToneId` | `await GetToneAsync(id)`. **Hit:** `Results` = that single tone, `TotalPages = 1`, `Selected` = it — which auto-fires the existing `LoadModelsAsync`, so its NAM/IR models load in the same gesture. **Miss** (`null` returned, or `T3kException` with `Kind == NotFound`): clear `Results`, `Banner = "No Tone3000 tone with ID {id}."` |
| `BadLink` | Clear `Results`, `Banner = "That doesn't look like a Tone3000 tone link."` No HTTP call is made. |
| `Text` | Today's `SearchAsync(query, FormatFilter, Page)` path, unchanged. |

Existing machinery carries over untouched, because a paste is just a text change:

- the 300 ms debounce (`Debounce` / `DebouncedLoadAsync`);
- the `_loadGeneration` stale-response guard, so a slow ID fetch cannot overwrite a newer load;
- the signed-in gate at the top of `LoadAsync` (`GetToneAsync` needs the same Bearer token);
- `FlagAuthIfNeeded` on `T3kException`.

`FormatFilter` is ignored for an ID lookup — it is a direct fetch, not a search. Toggling a NAM
or IR chip re-runs the same lookup and returns the same tone, which is harmless. `TotalPages = 1`
with `Page = 1` leaves the existing paging buttons inert without any view change.

### 4. View — `src/Namager.App/Views/Tone3000View.axaml:45`

The only UI diff. No new controls.

- Watermark: `Search tones, or paste Tone3000 URL or Tone ID…`
- Tooltip on the same `TextBox`: `Search tone titles, or paste a tone3000.com tone link (or its numeric ID) to jump straight to it.`

The box is the flexible `*` column sharing its row with the NAM/IR chips and the mode combo, so a
long watermark clips on a narrow window (Avalonia clips rather than ellipsizes). The tooltip
covers that case.

## Testing

TDD throughout.

**Parser** — table-driven, `tests/Namager.Tone3000.Tests`:

- Accepts → `ToneId`: `https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141` (74141);
  slugless `https://www.tone3000.com/tones/27365`; no-`www` host; `http://` scheme; trailing
  slash; `?utm_source=x`; `#fragment`; scheme-less `www.tone3000.com/tones/deluxe-65-27365`;
  surrounding whitespace; bare `74141`.
- Rejects → `BadLink`: profile URL `https://www.tone3000.com/daweed`;
  `https://example.com/tones/5`; `https://www.tone3000.com/tones/no-digits`;
  `https://www.tone3000.com/tones/` (empty segment).
- Falls through → `Text`: `deluxe reverb`; `65`-containing phrases such as `fender 65 deluxe`;
  empty / whitespace-only input (`Text` with a null-or-empty payload, matching today's
  "no query" behavior).

**View model** — `tests/Namager.App.Tests/Tone3000ViewModelTests.cs`, against the existing
`FakeClient`:

- URL paste → exactly one result, `Selected` is it, `SelectedModels` populated.
- Tone lookup returns `null` → `Results` empty, banner names the ID.
- `BadLink` → banner shown and the client is never called (assert a call counter on the fake).
- Ordinary text → still routes to `SearchAsync` with the text and the current `FormatFilter`.

Full suite (`dotnet test`) must stay green.
