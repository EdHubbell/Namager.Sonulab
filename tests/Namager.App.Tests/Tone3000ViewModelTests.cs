using System.Collections.ObjectModel;
using Namager.App.ViewModels;
using Namager.Tone3000;
using Xunit;

namespace Namager.App.Tests;

public class Tone3000ViewModelTests
{
    private sealed class FakeAuth : IT3kAuth
    {
        public bool SignedIn;
        public bool IsSignedIn => SignedIn;
        /// <summary>The real T3kAuth only ever learns the name during an interactive sign-in — a
        /// session restored from a saved refresh token reports null. Set false to model that.</summary>
        public bool ReportsUsername = true;
        public string? Username => SignedIn && ReportsUsername ? "ed" : null;
        public int SignInCalls;
        public Task SignInAsync(CancellationToken ct = default) { SignInCalls++; SignedIn = true; return Task.CompletedTask; }
        public void SignOut() => SignedIn = false;
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("at");
    }

    private sealed class FakeClient : IT3kClient
    {
        public List<(string? q, string? f, int p)> Searches = new();
        public T3kPage<T3kTone> NextPage = new(
            new[]
            {
                new T3kTone(1, "Deluxe", Gear: null, Description: null, Images: null,
                    PageUrl: "https://t3k/tones/1", Downloads: 5, Stars: 2, Format: "nam",
                    User: new T3kToneAuthor("ed")),
            }, 1, 20, 1, 1);
        public Exception? Throw;
        public Task<T3kPage<T3kTone>> SearchAsync(string? query, string? format, int page, CancellationToken ct = default)
        { if (Throw is not null) throw Throw; Searches.Add((query, format, page)); return Task.FromResult(NextPage); }
        public Task<T3kPage<T3kTone>> FavoritedAsync(int page, CancellationToken ct = default) => Task.FromResult(NextPage);
        public Task<T3kPage<T3kTone>> DownloadedAsync(int page, CancellationToken ct = default) => Task.FromResult(NextPage);
        public List<long> GetToneCalls = new();
        private T3kTone? _tone;
        private bool _toneSet;
        /// <summary>Set to override what GetToneAsync returns (including null for "not found").
        /// Unset → returns the first tone of NextPage, matching the old behavior.</summary>
        public T3kTone? ToneToReturn { set { _tone = value; _toneSet = true; } }
        public Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default)
        { GetToneCalls.Add(id); return Task.FromResult(_toneSet ? _tone : NextPage.Data.FirstOrDefault()); }
        /// <summary>Override the model list a tone returns (e.g. empty to model an A2-only miss).
        /// Unset → one "Clean" model, matching the old behavior.</summary>
        public IReadOnlyList<T3kModel>? ModelsToReturn;
        public Task<IReadOnlyList<T3kModel>> GetModelsAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult(ModelsToReturn ?? new[] { new T3kModel(9, "Clean", "nam", "https://x/9") });
        public int UserCalls;
        public Task<T3kUser?> GetUserAsync(CancellationToken ct = default)
        { UserCalls++; return Task.FromResult<T3kUser?>(new T3kUser("uuid-1", "ed")); }
        public Task SetFavoriteAsync(long toneId, bool favorite, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeDownloader : IT3kDownloader
    {
        public string PathToReturn = Path.Combine(Path.GetTempPath(), "t3k-test.nam");
        public Task<string> DownloadAsync(T3kModel model, string? toneFormat = null, CancellationToken ct = default) => Task.FromResult(PathToReturn);
    }

    /// <summary>I1 test seam: mutates the VM's Selected tone mid-download, mimicking a user
    /// clicking a different card while a "Send to pedal" download is still in flight.</summary>
    private sealed class SelectionSwitchingDownloader : IT3kDownloader
    {
        public Tone3000ViewModel? Vm;
        public T3kTone? SwitchSelectedTo;
        public string PathToReturn = Path.Combine(Path.GetTempPath(), "t3k-switch-test.nam");
        public Task<string> DownloadAsync(T3kModel model, string? toneFormat = null, CancellationToken ct = default)
        {
            if (Vm is not null) Vm.Selected = SwitchSelectedTo;
            return Task.FromResult(PathToReturn);
        }
    }

    /// <summary>I2+R1 test seam: the first SearchAsync call blocks on a gate (simulating a slow,
    /// in-flight network response); every later call returns immediately.</summary>
    private sealed class GatedSearchClient : IT3kClient
    {
        public readonly TaskCompletionSource Gate = new();
        private int _calls;
        public T3kPage<T3kTone> PageA = new(
            new[] { new T3kTone(1, "A", null, null, null, null, null, null, "nam", new T3kToneAuthor("ed")) }, 1, 20, 1, 1);
        public T3kPage<T3kTone> PageB = new(
            new[] { new T3kTone(2, "B", null, null, null, null, null, null, "nam", new T3kToneAuthor("ed")) }, 1, 20, 1, 1);

        public async Task<T3kPage<T3kTone>> SearchAsync(string? query, string? format, int page, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) { await Gate.Task; return PageA; }
            return PageB;
        }
        public Task<T3kPage<T3kTone>> FavoritedAsync(int page, CancellationToken ct = default) => Task.FromResult(PageB);
        public Task<T3kPage<T3kTone>> DownloadedAsync(int page, CancellationToken ct = default) => Task.FromResult(PageB);
        public Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default) => Task.FromResult<T3kTone?>(null);
        public Task<IReadOnlyList<T3kModel>> GetModelsAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T3kModel>>(Array.Empty<T3kModel>());
        public Task<T3kUser?> GetUserAsync(CancellationToken ct = default) => Task.FromResult<T3kUser?>(null);
        public Task SetFavoriteAsync(long toneId, bool favorite, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Tone3000ViewModel Make(FakeAuth? auth = null, FakeClient? client = null, FakeDownloader? dl = null) =>
        new(auth ?? new FakeAuth(), client ?? new FakeClient(), dl ?? new FakeDownloader(),
            dispatch: a => a(), delay: (_, _) => Task.CompletedTask);

    /// <summary>A session restored from a saved token starts with no username, because the real
    /// T3kAuth only assigns one during interactive sign-in. Without a backfill the header rendered
    /// "signed in as" followed by nothing. The first load must fill it in from the API.</summary>
    [Fact]
    public async Task Restored_session_backfills_the_username_on_first_load()
    {
        var auth = new FakeAuth { SignedIn = true, ReportsUsername = false };
        var vm = Make(auth);
        Assert.Null(vm.Username);                       // what the user actually saw

        await vm.SearchNowCommand.ExecuteAsync(null);

        Assert.Equal("ed", vm.Username);
    }

    [Fact]
    public async Task Username_backfill_does_not_refetch_once_known()
    {
        var client = new FakeClient();
        var vm = Make(new FakeAuth { SignedIn = true, ReportsUsername = false }, client);
        await vm.SearchNowCommand.ExecuteAsync(null);
        int after = client.UserCalls;
        await vm.SearchNowCommand.ExecuteAsync(null);
        Assert.Equal(after, client.UserCalls);
    }

    [Fact]
    public void Null_dependencies_mean_no_config_state()
    {
        var vm = new Tone3000ViewModel(null, null, null, dispatch: a => a());
        Assert.False(vm.HasConfig);
        Assert.Contains("tone3000.json", vm.KeysPath);
    }

    [Fact]
    public async Task SignIn_flips_state_and_runs_an_initial_search()
    {
        var auth = new FakeAuth(); var client = new FakeClient();
        var vm = Make(auth, client);
        Assert.False(vm.IsSignedIn);
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.IsSignedIn);
        Assert.Equal("ed", vm.Username);
        Assert.Single(client.Searches);                      // initial browse fired
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Search_text_change_debounces_then_searches()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient();
        var vm = Make(auth, client);
        vm.SearchText = "deluxe";
        await vm.PendingOperation!;
        Assert.Equal("deluxe", client.Searches.Last().q);
    }

    [Fact]
    public async Task Format_chip_filters_the_search()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient();
        var vm = Make(auth, client);
        vm.FormatFilter = "ir";
        await vm.PendingOperation!;
        Assert.Equal("ir", client.Searches.Last().f);
    }

    [Fact]
    public async Task Selecting_a_tone_loads_its_models()
    {
        var vm = Make(new FakeAuth { SignedIn = true });
        vm.Selected = new T3kTone(1, "Deluxe", Gear: null, Description: null, Images: null,
            PageUrl: null, Downloads: null, Stars: null, Format: "nam", User: new T3kToneAuthor("ed"));
        await vm.PendingOperation!;
        Assert.Single(vm.SelectedModels);
        Assert.Equal("Clean", vm.SelectedModels[0].Name);
        Assert.False(vm.NoModelsForSelection);                // has models → no note
    }

    [Fact]
    public async Task Selecting_an_A2less_tone_flags_no_models()
    {
        var client = new FakeClient { ModelsToReturn = Array.Empty<T3kModel>() };
        var vm = Make(new FakeAuth { SignedIn = true }, client);
        vm.Selected = new T3kTone(2, "A1-only amp", Gear: null, Description: null, Images: null,
            PageUrl: null, Downloads: null, Stars: null, Format: "nam", User: new T3kToneAuthor("ed"));
        await vm.PendingOperation!;
        Assert.Empty(vm.SelectedModels);
        Assert.True(vm.NoModelsForSelection);                 // empty A2 fetch → show the note
    }

    [Fact]
    public async Task Reselecting_a_tone_with_models_clears_the_no_models_flag()
    {
        var client = new FakeClient { ModelsToReturn = Array.Empty<T3kModel>() };
        var vm = Make(new FakeAuth { SignedIn = true }, client);
        vm.Selected = new T3kTone(2, "A1-only", null, null, null, null, null, null, "nam", new T3kToneAuthor("ed"));
        await vm.PendingOperation!;
        Assert.True(vm.NoModelsForSelection);

        client.ModelsToReturn = null;                          // next tone has models again
        vm.Selected = new T3kTone(3, "A2 amp", null, null, null, null, null, null, "nam", new T3kToneAuthor("ed"));
        await vm.PendingOperation!;
        Assert.False(vm.NoModelsForSelection);                 // flag cleared on the new selection
    }

    [Fact]
    public async Task SendToPedal_downloads_and_raises_the_handoff_event()
    {
        var dl = new FakeDownloader();
        var vm = Make(new FakeAuth { SignedIn = true }, dl: dl);
        vm.IsDeviceReady = true;
        vm.Selected = new T3kTone(1, "65 Deluxe Reverb", Gear: null, Description: null, Images: null,
            PageUrl: "https://www.tone3000.com/tones/1", Downloads: null, Stars: null, Format: "nam",
            User: new T3kToneAuthor("fabiossousa"));
        await vm.PendingOperation!;

        (string path, string? notes, string? url, bool isIr, T3kIrSource? irSource)? received = null;
        vm.SendToPedalRequested += (p, n, u, ir, src) => received = (p, n, u, ir, src);
        await vm.SendToPedalCommand.ExecuteAsync(vm.SelectedModels[0]);

        Assert.NotNull(received);
        Assert.Equal(dl.PathToReturn, received!.Value.path);
        Assert.Equal("65 Deluxe Reverb by fabiossousa (Tone3000)", received.Value.notes);
        Assert.Equal("https://www.tone3000.com/tones/1", received.Value.url);
        Assert.False(received.Value.isIr);
        Assert.Null(received.Value.irSource);          // "nam" format tone -> not an IR
    }

    [Fact]
    public async Task SendToPedal_uses_the_tone_selected_at_click_time()
    {
        var dl = new SelectionSwitchingDownloader();
        var vm = new Tone3000ViewModel(new FakeAuth { SignedIn = true }, new FakeClient(), dl,
            dispatch: a => a(), delay: (_, _) => Task.CompletedTask);
        vm.IsDeviceReady = true;

        var original = new T3kTone(1, "65 Deluxe Reverb", Gear: null, Description: null, Images: null,
            PageUrl: "https://www.tone3000.com/tones/1", Downloads: null, Stars: null, Format: "nam",
            User: new T3kToneAuthor("fabiossousa"));
        var switchedTo = new T3kTone(2, "Other Tone", Gear: null, Description: null, Images: null,
            PageUrl: "https://www.tone3000.com/tones/2", Downloads: null, Stars: null, Format: "ir",
            User: new T3kToneAuthor("someone-else"));

        vm.Selected = original;
        await vm.PendingOperation!;
        dl.Vm = vm; dl.SwitchSelectedTo = switchedTo;

        (string path, string? notes, string? url, bool isIr, T3kIrSource? irSource)? received = null;
        vm.SendToPedalRequested += (p, n, u, ir, src) => received = (p, n, u, ir, src);
        await vm.SendToPedalCommand.ExecuteAsync(vm.SelectedModels[0]);

        Assert.NotNull(received);
        Assert.Equal(switchedTo, vm.Selected);                // the selection change did happen mid-download
        Assert.Equal("65 Deluxe Reverb by fabiossousa (Tone3000)", received!.Value.notes);
        Assert.Equal("https://www.tone3000.com/tones/1", received.Value.url);
        Assert.False(received.Value.isIr);                    // original tone's Format ("nam"), not the switched-to tone's ("ir")
        Assert.Null(received.Value.irSource);
    }

    [Fact]
    public async Task Stale_search_response_does_not_overwrite_newer_results()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new GatedSearchClient();
        var vm = new Tone3000ViewModel(auth, client, new FakeDownloader(), dispatch: a => a(), delay: (_, _) => Task.CompletedTask);

        var first = vm.SearchNowCommand.ExecuteAsync(null);   // call 1: gated, still in flight
        var second = vm.SearchNowCommand.ExecuteAsync(null);  // call 2: returns immediately
        await second;

        Assert.Single(vm.Results);
        Assert.Equal("B", vm.Results[0].Title);               // the newer response landed

        client.Gate.SetResult();
        await first;                                          // the stale call finishes late...

        Assert.Single(vm.Results);
        Assert.Equal("B", vm.Results[0].Title);                // ...but must not clobber page B with stale page A
    }

    [Fact]
    public async Task Auth_failure_flips_back_to_signed_out()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient
        {
            Throw = new T3kException("Your Tone3000 session expired — sign in again.", T3kError.Auth)
        };
        var vm = Make(auth, client);
        auth.SignedIn = false;                                // mirrors T3kAuth: a dead refresh already signs out internally
        vm.SearchText = "x";
        await vm.PendingOperation!;

        Assert.False(vm.IsSignedIn);
        Assert.Contains("session expired", vm.Banner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a signed-in, device-ready VM with a Selected tone of the given format/id/
    /// title and its models loaded — the shared setup for the two T3kIrSource handoff tests below.</summary>
    private static async Task<Tone3000ViewModel> BuildVmWithTone(string format, long toneId, string title)
    {
        var vm = Make(new FakeAuth { SignedIn = true });
        vm.IsDeviceReady = true;
        vm.Selected = new T3kTone(toneId, title, Gear: null, Description: null, Images: null,
            PageUrl: null, Downloads: null, Stars: null, Format: format, User: new T3kToneAuthor("someone"));
        await vm.PendingOperation!;
        return vm;
    }

    [Fact]
    public async Task SendToPedal_passes_the_tone_and_model_ids_for_an_IR()
    {
        var vm = await BuildVmWithTone(format: "ir", toneId: 2468, title: "4x12 Greenback");
        T3kIrSource? captured = null;
        vm.SendToPedalRequested += (_, _, _, _, src) => captured = src;

        await vm.SendToPedalCommand.ExecuteAsync(new T3kModel(1357, "Model", "ir", null));

        Assert.NotNull(captured);
        Assert.Equal(2468, captured!.ToneId);
        Assert.Equal(1357, captured.ModelId);
        Assert.Equal("4x12 Greenback", captured.Title);
    }

    [Fact]
    public async Task SendToPedal_passes_no_IR_source_for_a_NAM_amp()
    {
        var vm = await BuildVmWithTone(format: "nam", toneId: 11, title: "Dumble");
        T3kIrSource? captured = new(0, 0, null);
        vm.SendToPedalRequested += (_, _, _, _, src) => captured = src;

        await vm.SendToPedalCommand.ExecuteAsync(new T3kModel(22, "Model", "nam", null));

        Assert.Null(captured);   // amps are not indexed — see the plan's Scope section
    }

    [Fact]
    public async Task SendToPedal_is_a_noop_when_device_not_ready()
    {
        var vm = Make(new FakeAuth { SignedIn = true });
        vm.IsDeviceReady = false;
        var raised = false;
        vm.SendToPedalRequested += (_, _, _, _, _) => raised = true;
        await vm.SendToPedalCommand.ExecuteAsync(new T3kModel(9, "Clean", "nam", "https://x/9"));
        Assert.False(raised);
    }

    [Fact]
    public async Task Api_failure_lands_in_the_banner_not_a_crash()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient { Throw = new T3kException("Tone3000 rate limit reached — wait a minute and retry.", T3kError.RateLimited) };
        var vm = Make(auth, client);
        vm.SearchText = "x";
        await vm.PendingOperation!;
        Assert.Contains("rate limit", vm.Banner, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Empty_results_show_no_banner()
    {
        var auth = new FakeAuth { SignedIn = true };
        var client = new FakeClient { NextPage = T3kPage<T3kTone>.Empty };
        var vm = Make(auth, client);
        vm.SearchText = "zzz";
        await vm.PendingOperation!;
        Assert.Null(vm.Banner);                              // "no results" is not an error
        Assert.Empty(vm.Results);
    }

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

    [Fact]
    public void SignOut_returns_to_signed_out_state()
    {
        var auth = new FakeAuth { SignedIn = true };
        var vm = Make(auth);
        vm.SignOutCommand.Execute(null);
        Assert.False(vm.IsSignedIn);
        Assert.Empty(vm.Results);
    }

    // ---- #12: per-file selection for the bottom detail panel ----

    private static T3kTone Tone(long id) =>
        new(id, $"Tone {id}", null, null, null, null, null, null, "nam", new T3kToneAuthor("ed"));

    private static T3kModel Model(string name) => new(name.GetHashCode(), name, "nam", $"https://x/{name}");

    [Fact]
    public async Task Selecting_a_tone_defaults_to_its_first_file()
    {
        var client = new FakeClient { ModelsToReturn = new[] { Model("a"), Model("b"), Model("c") } };
        var vm = Make(new FakeAuth { SignedIn = true }, client);

        vm.Selected = Tone(1);
        await vm.PendingOperation!;

        Assert.Equal(3, vm.SelectedModels.Count);
        Assert.Same(vm.SelectedModels[0], vm.SelectedModel);
    }

    [Fact]
    public async Task Selecting_a_tone_with_no_files_leaves_the_selection_null()
    {
        var client = new FakeClient { ModelsToReturn = Array.Empty<T3kModel>() };
        var vm = Make(new FakeAuth { SignedIn = true }, client);

        vm.Selected = Tone(1);
        await vm.PendingOperation!;

        Assert.Null(vm.SelectedModel);
        Assert.True(vm.NoModelsForSelection);
    }

    [Fact]
    public async Task Switching_tones_reselects_the_new_tones_first_file()
    {
        var client = new FakeClient { ModelsToReturn = new[] { Model("a"), Model("b") } };
        var vm = Make(new FakeAuth { SignedIn = true }, client);

        vm.Selected = Tone(1);
        await vm.PendingOperation!;
        var first = vm.SelectedModel;

        client.ModelsToReturn = new[] { Model("x"), Model("y"), Model("z") };
        vm.Selected = Tone(2);
        await vm.PendingOperation!;

        Assert.NotSame(first, vm.SelectedModel);
        Assert.Same(vm.SelectedModels[0], vm.SelectedModel);
    }
}
