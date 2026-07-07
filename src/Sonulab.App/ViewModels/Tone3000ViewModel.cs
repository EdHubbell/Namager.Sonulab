using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Tone3000;

namespace Sonulab.App.ViewModels;

public enum T3kViewMode { Search, Favorites, Downloaded }

/// <summary>The Browse Tones ▸ Tone3000 tab. Consumes only the Sonulab.Tone3000 interfaces
/// (all fakeable); works with no device connected — SendToPedal alone gates on
/// IsDeviceReady, which MainWindowViewModel maintains.</summary>
public partial class Tone3000ViewModel : ObservableObject
{
    private readonly IT3kAuth? _auth;
    private readonly IT3kClient? _client;
    private readonly IT3kDownloader? _downloader;
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _debounceCts;

    public Tone3000ViewModel(IT3kAuth? auth, IT3kClient? client, IT3kDownloader? downloader,
        Action<Action>? dispatch = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _auth = auth; _client = client; _downloader = downloader;
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _delay = delay ?? Task.Delay;
        IsSignedIn = auth?.IsSignedIn ?? false;
        Username = auth?.Username;
    }

    /// <summary>False when tone3000.json is missing/invalid — the tab shows the keys card.</summary>
    public bool HasConfig => _auth is not null && _client is not null && _downloader is not null;
    public string KeysPath => T3kConfig.DefaultPath;

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _formatFilter;             // null | "nam" | "ir"
    [ObservableProperty] private T3kViewMode _viewMode = T3kViewMode.Search;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private T3kTone? _selected;
    [ObservableProperty] private bool _isDeviceReady;
    public ObservableCollection<T3kTone> Results { get; } = new();
    public ObservableCollection<T3kModel> SelectedModels { get; } = new();

    /// <summary>Handoff to MainWindowViewModel: local file path, SSMD notes, SSMD url, isIr.</summary>
    public event Action<string, string?, string?, bool>? SendToPedalRequested;

    /// <summary>Test seam: the last fire-and-forget load (debounced search, selection load).</summary>
    public Task? PendingOperation { get; private set; }

    partial void OnSearchTextChanged(string value) => Debounce();
    partial void OnFormatFilterChanged(string? value) => Debounce();
    partial void OnViewModeChanged(T3kViewMode value) { Page = 1; PendingOperation = LoadAsync(); }
    partial void OnSelectedChanged(T3kTone? value) => PendingOperation = LoadModelsAsync(value);

    private void Debounce()
    {
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        Page = 1;
        PendingOperation = DebouncedLoadAsync(cts.Token);
    }

    private async Task DebouncedLoadAsync(CancellationToken ct)
    {
        try { await _delay(TimeSpan.FromMilliseconds(300), ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_auth is null) return;
        Banner = null;
        try
        {
            await _auth.SignInAsync();
            IsSignedIn = _auth.IsSignedIn;
            Username = _auth.Username ?? (await TryGetUsernameAsync());
            PendingOperation = LoadAsync();
            if (PendingOperation is { } p) await p;
        }
        catch (T3kException ex) { Banner = ex.Message; }
    }

    private async Task<string?> TryGetUsernameAsync()
    {
        try { return _client is null ? null : (await _client.GetUserAsync())?.Username; }
        catch (T3kException) { return null; }
    }

    [RelayCommand]
    private void SignOut()
    {
        _auth?.SignOut();
        IsSignedIn = false; Username = null;
        Results.Clear(); SelectedModels.Clear(); Selected = null; Banner = null;
    }

    [RelayCommand] private Task SearchNowAsync() { Page = 1; return (PendingOperation = LoadAsync()); }
    [RelayCommand] private Task NextPageAsync() { if (Page < TotalPages) Page++; return (PendingOperation = LoadAsync()); }
    [RelayCommand] private Task PrevPageAsync() { if (Page > 1) Page--; return (PendingOperation = LoadAsync()); }

    private async Task LoadAsync()
    {
        if (_client is null || !IsSignedIn) return;
        IsLoading = true; Banner = null;
        try
        {
            var page = ViewMode switch
            {
                T3kViewMode.Favorites => await _client.FavoritedAsync(Page),
                T3kViewMode.Downloaded => await _client.DownloadedAsync(Page),
                _ => await _client.SearchAsync(
                        string.IsNullOrWhiteSpace(SearchText) ? null : SearchText, FormatFilter, Page),
            };
            _dispatch(() =>
            {
                Results.Clear();
                foreach (var t in page.Data) Results.Add(t);
                TotalPages = page.TotalPages;
            });
        }
        catch (T3kException ex) { _dispatch(() => { Results.Clear(); Banner = ex.Message; }); }
        finally { IsLoading = false; }
    }

    private async Task LoadModelsAsync(T3kTone? tone)
    {
        SelectedModels.Clear();
        if (tone is null || _client is null) return;
        try
        {
            var models = await _client.GetModelsAsync(tone.Id);
            _dispatch(() => { foreach (var m in models) SelectedModels.Add(m); });
        }
        catch (T3kException ex) { _dispatch(() => Banner = ex.Message); }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_client is null || Selected is not { } t) return;
        try { await _client.SetFavoriteAsync(t.Id, favorite: true); }
        catch (T3kException ex) { Banner = ex.Message; }
    }

    [RelayCommand]
    private async Task SendToPedalAsync(T3kModel? model)
    {
        if (model is null || _downloader is null || !IsDeviceReady) return;
        Banner = null;
        try
        {
            var path = await _downloader.DownloadAsync(model, Selected?.Format);
            // T3kModel.Format is always null on the live API (docs/tone3000-api-findings.md) —
            // derive isIr from the parent tone's Format first, falling back to the file extension.
            bool isIr = Selected?.Format is "ir" or "wav" ||
                        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            string? notes = Selected is { } t ? $"{t.Title} by {t.Author} (Tone3000)" : null;
            string? url = Selected?.PageUrl;
            SendToPedalRequested?.Invoke(path, notes, url, isIr);
        }
        catch (T3kException ex) { Banner = ex.Message; }
    }

    [RelayCommand]
    private void OpenTonePage()
    {
        if (Selected?.PageUrl is not { Length: > 0 } url ||
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a link must never crash the app */ }
    }
}
