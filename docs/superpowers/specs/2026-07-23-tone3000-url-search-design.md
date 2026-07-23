# Tone3000 search bar: accept a tone URL or ID (+ nav icons)

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

## Companion change: nav icons

Bundled in because it touches the same tab. Today the left-nav has three of four items sharing
`Icon.List`, and **Amps** uses `Icon.Power` — which collides with the Connect button
(`MainWindow.axaml:26` and `:75`). Give each destination a distinct, meaningful glyph.

Avalonia `PathIcon` **fills** its geometry (no stroking), so every path must be a closed fill
shape — all candidates below are. New paths are verbatim from the Material Design Icons (MDI)
source SVGs, 24×24 viewBox, matching the existing Material glyphs.

**New keys in `src/Namager.App/Icons.axaml`:**

| Key | MDI source | Path (`d`) |
|---|---|---|
| `Icon.Amp` | `amplifier` | `M10,2H14A1,1 0 0,1 15,3H21V21H19A1,1 0 0,1 18,22A1,1 0 0,1 17,21H7A1,1 0 0,1 6,22A1,1 0 0,1 5,21H3V3H9A1,1 0 0,1 10,2M5,5V9H19V5H5M7,6A1,1 0 0,1 8,7A1,1 0 0,1 7,8A1,1 0 0,1 6,7A1,1 0 0,1 7,6M12,6H14V7H12V6M15,6H16V8H15V6M17,6H18V8H17V6M12,11A4,4 0 0,0 8,15A4,4 0 0,0 12,19A4,4 0 0,0 16,15A4,4 0 0,0 12,11M10,6A1,1 0 0,1 11,7A1,1 0 0,1 10,8A1,1 0 0,1 9,7A1,1 0 0,1 10,6Z` |
| `Icon.Ir` | `waveform` | `M22 12L20 13L19 14L18 13L17 16L16 13L15 21L14 13L13 15L12 13L11 17L10 13L9 22L8 13L7 19L6 13L5 14L4 13L2 12L4 11L5 10L6 11L7 5L8 11L9 2L10 11L11 7L12 11L13 9L14 11L15 3L16 11L17 8L18 11L19 10L20 11L22 12Z` |
| `Icon.Web` | `web` | `M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z` |

**`src/Namager.App/Views/MainWindow.axaml`** — swap the `{StaticResource ...}` on three nav items:

| Nav item | Line | Old | New |
|---|---|---|---|
| Amps | 75 | `Icon.Power` | `Icon.Amp` |
| IRs | 81 | `Icon.List` | `Icon.Ir` |
| Tone3000 | 90 | `Icon.List` | `Icon.Web` |

Unchanged: **Presets** keeps `Icon.List` (now the sole user); **Connect** keeps `Icon.Power`
(now the sole user, `:26`). No new resources beyond the three keys; no code-behind changes.

No automated test — icons are static XAML resources. Verification is visual: run the app and
confirm all four nav items and Connect show distinct glyphs, legible at the 16×16 nav size, in
both light and dark theme variants. Added to the pending
`docs/HARDWARE-VALIDATION-ui-polish.md` visual checklist.
