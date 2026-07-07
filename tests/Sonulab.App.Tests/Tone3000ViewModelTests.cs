using System.Collections.ObjectModel;
using Sonulab.App.ViewModels;
using Sonulab.Tone3000;
using Xunit;

namespace Sonulab.App.Tests;

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
