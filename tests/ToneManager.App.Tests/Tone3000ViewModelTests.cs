using System.Collections.ObjectModel;
using ToneManager.App.ViewModels;
using ToneManager.Tone3000;
using Xunit;

namespace ToneManager.App.Tests;

public class Tone3000ViewModelTests
{
    private sealed class FakeAuth : IT3kAuth
    {
        public bool SignedIn;
        public bool IsSignedIn => SignedIn;
        public string? Username => SignedIn ? "ed" : null;
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
        public Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default) => Task.FromResult<T3kTone?>(NextPage.Data.FirstOrDefault());
        public Task<IReadOnlyList<T3kModel>> GetModelsAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T3kModel>>(new[] { new T3kModel(9, "Clean", "nam", "https://x/9") });
        public Task<T3kUser?> GetUserAsync(CancellationToken ct = default) => Task.FromResult<T3kUser?>(new T3kUser("uuid-1", "ed"));
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

        (string path, string? notes, string? url, bool isIr)? received = null;
        vm.SendToPedalRequested += (p, n, u, ir) => received = (p, n, u, ir);
        await vm.SendToPedalCommand.ExecuteAsync(vm.SelectedModels[0]);

        Assert.NotNull(received);
        Assert.Equal(dl.PathToReturn, received!.Value.path);
        Assert.Equal("65 Deluxe Reverb by fabiossousa (Tone3000)", received.Value.notes);
        Assert.Equal("https://www.tone3000.com/tones/1", received.Value.url);
        Assert.False(received.Value.isIr);
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

        (string path, string? notes, string? url, bool isIr)? received = null;
        vm.SendToPedalRequested += (p, n, u, ir) => received = (p, n, u, ir);
        await vm.SendToPedalCommand.ExecuteAsync(vm.SelectedModels[0]);

        Assert.NotNull(received);
        Assert.Equal(switchedTo, vm.Selected);                // the selection change did happen mid-download
        Assert.Equal("65 Deluxe Reverb by fabiossousa (Tone3000)", received!.Value.notes);
        Assert.Equal("https://www.tone3000.com/tones/1", received.Value.url);
        Assert.False(received.Value.isIr);                    // original tone's Format ("nam"), not the switched-to tone's ("ir")
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

    [Fact]
    public async Task SendToPedal_is_a_noop_when_device_not_ready()
    {
        var vm = Make(new FakeAuth { SignedIn = true });
        vm.IsDeviceReady = false;
        var raised = false;
        vm.SendToPedalRequested += (_, _, _, _) => raised = true;
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
    public void SignOut_returns_to_signed_out_state()
    {
        var auth = new FakeAuth { SignedIn = true };
        var vm = Make(auth);
        vm.SignOutCommand.Execute(null);
        Assert.False(vm.IsSignedIn);
        Assert.Empty(vm.Results);
    }
}
