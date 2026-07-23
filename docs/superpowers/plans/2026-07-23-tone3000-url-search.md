# Tone3000 URL/ID Search + Nav Icons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Tone3000 search box jump straight to a tone when the user pastes a tone3000.com link or a bare numeric ID, and give the left-nav distinct, meaningful icons.

**Architecture:** A new pure parser (`T3kSearchQuery`) in `Namager.Tone3000` classifies the box text as text-search / tone-id / bad-link. `Tone3000ViewModel.LoadAsync` gains one branch that routes an id to the existing `IT3kClient.GetToneAsync`, surfaces a single auto-selected result, and shows a banner on a bad link or dead id. A new `T3kError.NotFound` lets a 404 read as "no such tone". The nav-icon swap is static XAML: three new `Icons.axaml` keys and three `StaticResource` references in `MainWindow.axaml`.

**Tech Stack:** .NET 10, C#, Avalonia 12 (MVVM, CommunityToolkit.Mvvm), xUnit.

## Global Constraints

- Build: `dotnet build` · Test: `dotnet test` — all tests pass (490 today; count grows as tasks add tests).
- Avalonia 12 + built-in FluentTheme. Do NOT add FluentAvalonia or any third-party icon lib. Icons are `PathIcon` geometries — **fill only, no stroking**; every path must be a closed fill shape.
- UI colors come from `Styles/SonulabTheme.axaml` tokens — never hardcode hex in `.axaml`. (No color work in this plan, but the constraint stands.)
- `Namager.Tone3000` and `Sonulab.Core` are no-UI and fully unit-tested; new logic there ships with tests.
- Commit messages end with the repo's `Co-Authored-By:` / `Claude-Session:` trailers (see existing history). The commands below omit them for brevity — add them per repo convention.
- The publishable-key / secret-key split is unchanged; this plan touches no auth or config code.

---

### Task 1: `T3kSearchQuery` parser

Pure, static, no HTTP/UI/state. Classifies the search box text.

**Files:**
- Create: `src/Namager.Tone3000/T3kSearchQuery.cs`
- Test: `tests/Namager.Tone3000.Tests/T3kSearchQueryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public enum T3kQueryKind { Text, ToneId, BadLink }`
  - `public readonly record struct T3kQuery(T3kQueryKind Kind, long ToneId, string? Text)`
  - `public static class T3kSearchQuery { public static T3kQuery Parse(string? input); }`

- [ ] **Step 1: Write the failing tests**

Create `tests/Namager.Tone3000.Tests/T3kSearchQueryTests.cs`:

```csharp
using Namager.Tone3000;
using Xunit;

namespace Namager.Tone3000.Tests;

public class T3kSearchQueryTests
{
    [Theory]
    // Full slugged URL — the digits after the last hyphen are the id.
    [InlineData("https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141", 74141)]
    [InlineData("https://www.tone3000.com/tones/deluxe-65-27365", 27365)]
    [InlineData("https://tone3000.com/tones/27365", 27365)]              // no www, slugless
    [InlineData("http://www.tone3000.com/tones/deluxe-65-27365", 27365)] // http scheme
    [InlineData("https://www.tone3000.com/tones/deluxe-65-27365/", 27365)] // trailing slash
    [InlineData("https://www.tone3000.com/tones/27365?utm_source=x", 27365)] // query string
    [InlineData("https://www.tone3000.com/tones/27365#top", 27365)]      // fragment
    [InlineData("www.tone3000.com/tones/deluxe-65-27365", 27365)]        // scheme-less paste
    [InlineData("  https://www.tone3000.com/tones/27365  ", 27365)]      // surrounding whitespace
    [InlineData("74141", 74141)]                                        // bare numeric id
    public void Recognizes_tone_ids(string input, long expected)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.ToneId, q.Kind);
        Assert.Equal(expected, q.ToneId);
    }

    [Theory]
    [InlineData("https://www.tone3000.com/daweed")]                     // profile, not a tone
    [InlineData("https://example.com/tones/5")]                         // wrong host
    [InlineData("https://www.tone3000.com/tones/no-digits")]            // slug has no trailing number
    [InlineData("https://www.tone3000.com/tones/")]                     // empty tone segment
    [InlineData("http://tone3000.com/")]                                // no path
    public void Rejects_non_tone_links(string input) =>
        Assert.Equal(T3kQueryKind.BadLink, T3kSearchQuery.Parse(input).Kind);

    [Theory]
    [InlineData("deluxe reverb")]
    [InlineData("fender 65 deluxe")]     // contains digits but is a phrase, not a bare id
    public void Falls_through_to_text(string input)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.Text, q.Kind);
        Assert.Equal(input, q.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_text_with_no_payload(string? input)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.Text, q.Kind);
        Assert.Null(q.Text);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Namager.Tone3000.Tests --filter "FullyQualifiedName~T3kSearchQueryTests"`
Expected: FAIL — build error, `T3kSearchQuery` / `T3kQuery` / `T3kQueryKind` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Namager.Tone3000/T3kSearchQuery.cs`:

```csharp
namespace Namager.Tone3000;

public enum T3kQueryKind { Text, ToneId, BadLink }

/// <summary>Classification of the search box text: a text search, a direct tone id, or
/// something that looks like a link but isn't a usable Tone3000 tone reference.</summary>
public readonly record struct T3kQuery(T3kQueryKind Kind, long ToneId, string? Text);

/// <summary>Pure classifier for the Tone3000 search box. No HTTP, no state.
/// Recognizes a tone3000.com tone URL or a bare numeric id so the UI can jump straight
/// to that tone (the API text search lags the website — docs/tone3000-api-findings.md).</summary>
public static class T3kSearchQuery
{
    private const int MaxIdDigits = 18;   // stays inside a long
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static T3kQuery Parse(string? input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return new(T3kQueryKind.Text, 0, null);

        // 1. Bare numeric id.
        if (IsToneNumber(s, out var bareId)) return new(T3kQueryKind.ToneId, bareId, null);

        // 2. Looks like a link? (explicit scheme, or a scheme-less tone3000.com paste)
        bool looksLink = s.StartsWith("http://", Ci) || s.StartsWith("https://", Ci)
                         || s.Contains("tone3000.com/", Ci);
        if (!looksLink) return new(T3kQueryKind.Text, 0, s);

        var withScheme = s.StartsWith("http://", Ci) || s.StartsWith("https://", Ci)
                         ? s : "https://" + s;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            return new(T3kQueryKind.BadLink, 0, null);

        if (!uri.Host.Equals("tone3000.com", Ci) && !uri.Host.Equals("www.tone3000.com", Ci))
            return new(T3kQueryKind.BadLink, 0, null);

        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2 || !segs[0].Equals("tones", Ci))
            return new(T3kQueryKind.BadLink, 0, null);

        var slug = segs[1];
        var tail = slug.Contains('-') ? slug[(slug.LastIndexOf('-') + 1)..] : slug;
        return IsToneNumber(tail, out var id)
            ? new(T3kQueryKind.ToneId, id, null)
            : new(T3kQueryKind.BadLink, 0, null);
    }

    private static bool IsToneNumber(string s, out long id)
    {
        id = 0;
        if (s.Length is 0 or > MaxIdDigits) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return long.TryParse(s, out id);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Namager.Tone3000.Tests --filter "FullyQualifiedName~T3kSearchQueryTests"`
Expected: PASS — all theory cases green.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.Tone3000/T3kSearchQuery.cs tests/Namager.Tone3000.Tests/T3kSearchQueryTests.cs
git commit -m "feat(tone3000): parse a tone URL or bare id from the search box"
```

---

### Task 2: `T3kError.NotFound` + 404 mapping

So a dead id reads as "no such tone" instead of a generic HTTP failure.

**Files:**
- Modify: `src/Namager.Tone3000/T3kException.cs` (the `T3kError` enum)
- Modify: `src/Namager.Tone3000/T3kClient.cs` (the `SendAsync` status switch, ~lines 89-96)
- Test: `tests/Namager.Tone3000.Tests/T3kClientTests.cs` (extend the existing `Http_failures_map_to_typed_errors` theory, ~lines 118-128)

**Interfaces:**
- Consumes: nothing new.
- Produces: `T3kError.NotFound` — a `GetToneAsync` against a missing id throws `T3kException` with `Kind == T3kError.NotFound`.

- [ ] **Step 1: Write the failing test**

In `tests/Namager.Tone3000.Tests/T3kClientTests.cs`, add one `InlineData` line to the existing theory (just above the `InternalServerError` line):

```csharp
    [InlineData(HttpStatusCode.NotFound, T3kError.NotFound)]
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Namager.Tone3000.Tests --filter "FullyQualifiedName~T3kClientTests.Http_failures_map_to_typed_errors"`
Expected: FAIL — build error (`T3kError.NotFound` undefined) or, once the enum exists but the switch doesn't map it, the `NotFound` case asserts `Api != NotFound`.

- [ ] **Step 3: Add the enum member**

In `src/Namager.Tone3000/T3kException.cs`:

```csharp
public enum T3kError { Auth, RateLimited, Network, NotFound, Api }
```

- [ ] **Step 4: Add the switch arm**

In `src/Namager.Tone3000/T3kClient.cs`, in the `throw resp.StatusCode switch { ... }` block, add before the `_ =>` default:

```csharp
            HttpStatusCode.NotFound =>
                new T3kException("Tone3000 couldn't find that tone.", T3kError.NotFound),
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.Tone3000.Tests --filter "FullyQualifiedName~T3kClientTests"`
Expected: PASS — all four status→error rows green.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.Tone3000/T3kException.cs src/Namager.Tone3000/T3kClient.cs tests/Namager.Tone3000.Tests/T3kClientTests.cs
git commit -m "feat(tone3000): map HTTP 404 to T3kError.NotFound"
```

---

### Task 3: Route id / bad-link in `Tone3000ViewModel.LoadAsync`

**Files:**
- Modify: `src/Namager.App/ViewModels/Tone3000ViewModel.cs` (`LoadAsync`, lines 129-157)
- Test: `tests/Namager.App.Tests/Tone3000ViewModelTests.cs` (extend `FakeClient`, add four facts)

**Interfaces:**
- Consumes: `T3kSearchQuery.Parse` (Task 1); `T3kError.NotFound` (Task 2); existing `IT3kClient.GetToneAsync(long)`.
- Produces: nothing new for later tasks.

- [ ] **Step 1: Extend `FakeClient` and write the failing tests**

In `tests/Namager.App.Tests/Tone3000ViewModelTests.cs`, replace `FakeClient.GetToneAsync` (line 36) with a call-recording, settable version. Add these members to `FakeClient` and swap the method body:

```csharp
        public List<long> GetToneCalls = new();
        private T3kTone? _tone;
        private bool _toneSet;
        /// <summary>Set to override what GetToneAsync returns (including null for "not found").
        /// Unset → returns the first tone of NextPage, matching the old behavior.</summary>
        public T3kTone? ToneToReturn { set { _tone = value; _toneSet = true; } }
        public Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default)
        { GetToneCalls.Add(id); return Task.FromResult(_toneSet ? _tone : NextPage.Data.FirstOrDefault()); }
```

Then add four facts to the class:

```csharp
    [Fact]
    public async Task Pasting_a_tone_url_shows_one_auto_selected_result_with_models()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient();
        var vm = Make(auth, client);
        vm.SearchText = "https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141";
        await vm.PendingOperation!;

        Assert.Equal(new long[] { 74141 }, client.GetToneCalls);   // fetched by id
        Assert.Empty(client.Searches);                             // NOT a text search
        Assert.Single(vm.Results);
        Assert.Same(vm.Results[0], vm.Selected);                   // auto-selected
        await vm.PendingOperation!;                                // the selection's model load
        Assert.Single(vm.SelectedModels);                          // models loaded in the same gesture
        Assert.Equal(1, vm.TotalPages);
        Assert.Null(vm.Banner);
    }

    [Fact]
    public async Task A_bare_numeric_id_is_fetched_by_id()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient();
        var vm = Make(auth, client);
        vm.SearchText = "74141";
        await vm.PendingOperation!;

        Assert.Equal(new long[] { 74141 }, client.GetToneCalls);
        Assert.Empty(client.Searches);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task An_unknown_id_clears_results_and_names_the_id()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient { ToneToReturn = null };
        var vm = Make(auth, client);
        vm.SearchText = "https://www.tone3000.com/tones/999999";
        await vm.PendingOperation!;

        Assert.Empty(vm.Results);
        Assert.Null(vm.Selected);
        Assert.Contains("999999", vm.Banner);
    }

    [Fact]
    public async Task A_bad_link_shows_a_banner_and_never_calls_the_client()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient();
        var vm = Make(auth, client);
        vm.SearchText = "https://www.tone3000.com/daweed";
        await vm.PendingOperation!;

        Assert.Empty(vm.Results);
        Assert.NotNull(vm.Banner);
        Assert.Empty(client.GetToneCalls);
        Assert.Empty(client.Searches);            // no HTTP at all
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~Tone3000ViewModelTests"`
Expected: FAIL — the new facts fail (a URL currently goes to `SearchAsync`, so `GetToneCalls` is empty / `Searches` is non-empty). Existing facts still pass.

- [ ] **Step 3: Add the routing branch in `LoadAsync`**

In `src/Namager.App/ViewModels/Tone3000ViewModel.cs`, inside `LoadAsync`, immediately after `IsLoading = true; Banner = null;` and before the `var page = ViewMode switch` block, insert:

```csharp
            if (ViewMode == T3kViewMode.Search)
            {
                var parsed = T3kSearchQuery.Parse(SearchText);
                if (parsed.Kind == T3kQueryKind.BadLink)
                {
                    _dispatch(() =>
                    {
                        if (gen != _loadGeneration) return;
                        Results.Clear(); TotalPages = 0;
                        Banner = "That doesn't look like a Tone3000 tone link.";
                    });
                    return;
                }
                if (parsed.Kind == T3kQueryKind.ToneId)
                {
                    T3kTone? tone;
                    try { tone = await _client.GetToneAsync(parsed.ToneId); }
                    catch (T3kException ex) when (ex.Kind == T3kError.NotFound) { tone = null; }
                    _dispatch(() =>
                    {
                        if (gen != _loadGeneration) return;
                        Results.Clear();
                        if (tone is null)
                        { TotalPages = 0; Banner = $"No Tone3000 tone with ID {parsed.ToneId}."; }
                        else
                        { Results.Add(tone); TotalPages = 1; Selected = tone; }
                    });
                    return;
                }
            }
```

Note: `IsLoading` is reset by the existing `finally`, which still runs on these early returns. Auth/Network/RateLimited/Api exceptions from `GetToneAsync` fall through to the existing `catch (T3kException ex)` block (which runs `FlagAuthIfNeeded` and shows `ex.Message`) — only `NotFound` is swallowed here into the "no such tone" banner. The `Text` kind falls through unchanged to the existing `SearchAsync` path.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter "FullyQualifiedName~Tone3000ViewModelTests"`
Expected: PASS — new facts green, all existing facts still green.

- [ ] **Step 5: Full suite**

Run: `dotnet test`
Expected: PASS — no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/Tone3000ViewModel.cs tests/Namager.App.Tests/Tone3000ViewModelTests.cs
git commit -m "feat(tone3000): jump to a pasted tone URL or id from the search box"
```

---

### Task 4: Search box watermark + tooltip

**Files:**
- Modify: `src/Namager.App/Views/Tone3000View.axaml:45`

**Interfaces:** none (view-only).

No automated test — this is a static XAML string change; verified visually in Task 6.

- [ ] **Step 1: Update the TextBox**

In `src/Namager.App/Views/Tone3000View.axaml`, replace line 45:

```xml
          <TextBox Grid.Column="0" Text="{Binding SearchText}" Watermark="Search tones…"/>
```

with:

```xml
          <TextBox Grid.Column="0" Text="{Binding SearchText}"
                   Watermark="Search tones, or paste Tone3000 URL or Tone ID…"
                   ToolTip.Tip="Search tone titles, or paste a tone3000.com tone link (or its numeric ID) to jump straight to it."/>
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: build succeeds (XAML compiles).

- [ ] **Step 3: Commit**

```bash
git add src/Namager.App/Views/Tone3000View.axaml
git commit -m "feat(tone3000): watermark + tooltip for URL/ID search"
```

---

### Task 5: Nav icons

Replace the three duplicated/colliding nav glyphs. Amps = MDI `amplifier`, IRs = MDI `waveform`, Tone3000 = MDI `web`. Presets keeps `Icon.List`; Connect keeps `Icon.Power`.

**Files:**
- Modify: `src/Namager.App/Icons.axaml` (add three `StreamGeometry` resources)
- Modify: `src/Namager.App/Views/MainWindow.axaml` (three `PathIcon Data` swaps: Amps `:75`, IRs `:81`, Tone3000 `:90`)

**Interfaces:** none (view-only).

No automated test — static XAML resources; verified visually in Task 6.

- [ ] **Step 1: Add the three icon resources**

In `src/Namager.App/Icons.axaml`, add before the closing `</ResourceDictionary>`:

```xml
  <StreamGeometry x:Key="Icon.Amp">M10,2H14A1,1 0 0,1 15,3H21V21H19A1,1 0 0,1 18,22A1,1 0 0,1 17,21H7A1,1 0 0,1 6,22A1,1 0 0,1 5,21H3V3H9A1,1 0 0,1 10,2M5,5V9H19V5H5M7,6A1,1 0 0,1 8,7A1,1 0 0,1 7,8A1,1 0 0,1 6,7A1,1 0 0,1 7,6M12,6H14V7H12V6M15,6H16V8H15V6M17,6H18V8H17V6M12,11A4,4 0 0,0 8,15A4,4 0 0,0 12,19A4,4 0 0,0 16,15A4,4 0 0,0 12,11M10,6A1,1 0 0,1 11,7A1,1 0 0,1 10,8A1,1 0 0,1 9,7A1,1 0 0,1 10,6Z</StreamGeometry>
  <StreamGeometry x:Key="Icon.Ir">M22 12L20 13L19 14L18 13L17 16L16 13L15 21L14 13L13 15L12 13L11 17L10 13L9 22L8 13L7 19L6 13L5 14L4 13L2 12L4 11L5 10L6 11L7 5L8 11L9 2L10 11L11 7L12 11L13 9L14 11L15 3L16 11L17 8L18 11L19 10L20 11L22 12Z</StreamGeometry>
  <StreamGeometry x:Key="Icon.Web">M16.36,14C16.44,13.34 16.5,12.68 16.5,12C16.5,11.32 16.44,10.66 16.36,10H19.74C19.9,10.64 20,11.31 20,12C20,12.69 19.9,13.36 19.74,14M14.59,19.56C15.19,18.45 15.65,17.25 15.97,16H18.92C17.96,17.65 16.43,18.93 14.59,19.56M14.34,14H9.66C9.56,13.34 9.5,12.68 9.5,12C9.5,11.32 9.56,10.65 9.66,10H14.34C14.43,10.65 14.5,11.32 14.5,12C14.5,12.68 14.43,13.34 14.34,14M12,19.96C11.17,18.76 10.5,17.43 10.09,16H13.91C13.5,17.43 12.83,18.76 12,19.96M8,8H5.08C6.03,6.34 7.57,5.06 9.4,4.44C8.8,5.55 8.35,6.75 8,8M5.08,16H8C8.35,17.25 8.8,18.45 9.4,19.56C7.57,18.93 6.03,17.65 5.08,16M4.26,14C4.1,13.36 4,12.69 4,12C4,11.31 4.1,10.64 4.26,10H7.64C7.56,10.66 7.5,11.32 7.5,12C7.5,12.68 7.56,13.34 7.64,14M12,4.03C12.83,5.23 13.5,6.57 13.91,8H10.09C10.5,6.57 11.17,5.23 12,4.03M18.92,8H15.97C15.65,6.75 15.19,5.55 14.59,4.44C16.43,5.07 17.96,6.34 18.92,8M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z</StreamGeometry>
```

- [ ] **Step 2: Swap the three nav `PathIcon` references**

In `src/Namager.App/Views/MainWindow.axaml`:

- Amps (line 75): `Data="{StaticResource Icon.Power}"` → `Data="{StaticResource Icon.Amp}"`
- IRs (line 81): `Data="{StaticResource Icon.List}"` → `Data="{StaticResource Icon.Ir}"`
- Tone3000 (line 90): `Data="{StaticResource Icon.List}"` → `Data="{StaticResource Icon.Web}"`

Leave Presets (`Icon.List`, line 69) and the Connect button (`Icon.Power`, line 26) unchanged.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: build succeeds — all three `StaticResource` keys resolve.

- [ ] **Step 4: Commit**

```bash
git add src/Namager.App/Icons.axaml src/Namager.App/Views/MainWindow.axaml
git commit -m "feat(ui): distinct nav icons — amp, waveform, globe"
```

---

### Task 6: Visual verification

**Files:** none (manual check).

Not automatable — Avalonia rendering. Run the app (VoidX-Control must be CLOSED if a device is attached; the tab also works with no device):

- [ ] **Step 1: Launch**

Run: `dotnet run --project src/Namager.App`

- [ ] **Step 2: Nav icons** — confirm all four nav items and the Connect button show distinct glyphs (Presets = list, Amps = amp head, IRs = waveform, Tone3000 = globe, Connect = power), legible at 16×16. Toggle the theme and re-check light + dark. If `waveform` or `web` looks too faint at 16px, note it — the fallbacks (graphic-EQ bars / solid `public` globe) are recorded in the spec.

- [ ] **Step 3: URL/ID search** (needs Tone3000 sign-in) — paste `https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141` into the search box: one result appears, auto-selected, its models listed. Type `74141`: same tone. Paste a profile URL like `https://www.tone3000.com/daweed`: results clear, banner "That doesn't look like a Tone3000 tone link." Enter a nonexistent id: banner naming the id. Type ordinary text (`deluxe`): normal search results.

- [ ] **Step 4: Record results** — append a dated entry to `docs/HARDWARE-VALIDATION-ui-polish.md` (nav-icon legibility) and `docs/HARDWARE-VALIDATION-tone3000.md` (URL/ID search) with pass/fail per check.

---

## Self-Review

**Spec coverage:**
- `T3kSearchQuery` (parse rules 1–3, all URL shapes) → Task 1. ✓
- `T3kError.NotFound` + 404 mapping → Task 2. ✓
- `LoadAsync` branch (ToneId hit/miss, BadLink, Text unchanged; gen-guard, signed-in gate, debounce all preserved) → Task 3. ✓
- Watermark + tooltip → Task 4. ✓
- Nav icons (three new keys, three swaps, Presets/Connect unchanged, fill-only constraint) → Task 5. ✓
- Visual checklist entries → Task 6. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every command has an expected result.

**Type consistency:** `T3kQuery`/`T3kQueryKind`/`T3kSearchQuery.Parse` and `T3kError.NotFound` are defined in Tasks 1–2 and consumed with the same names/signatures in Task 3. `GetToneAsync(long)` matches the existing `IT3kClient` signature. `FakeClient.ToneToReturn`/`GetToneCalls` are defined and used within Task 3.
