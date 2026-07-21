using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ToneManager.Tone3000;

namespace ToneManager.App.ViewModels;

public enum T3kViewMode { Search, Favorites, Downloaded }

/// <summary>The Browse Tones ▸ Tone3000 tab. Consumes only the ToneManager.Tone3000 interfaces
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
    private int _loadGeneration;

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

    /// <summary>XAML glue: the ComboBox binds SelectedIndex here (TwoWay), which stays in sync
    /// with <see cref="ViewMode"/>. Kept as a plain int property (no converter) so it round-trips.</summary>
    public int ViewModeIndex
    {
        get => (int)ViewMode;
        set => ViewMode = (T3kViewMode)value;
    }

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
    partial void OnViewModeChanged(T3kViewMode value)
    {
        OnPropertyChanged(nameof(ViewModeIndex));
        Page = 1;
        PendingOperation = LoadAsync();
    }
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
        int gen = ++_loadGeneration;
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
                if (gen != _loadGeneration) return;           // a newer load already landed
                Results.Clear();
                foreach (var t in page.Data) Results.Add(t);
                TotalPages = page.TotalPages;
            });
        }
        catch (T3kException ex)
        {
            FlagAuthIfNeeded(ex);
            _dispatch(() => { if (gen != _loadGeneration) return; Results.Clear(); Banner = ex.Message; });
        }
        finally { IsLoading = false; }
    }

    private async Task LoadModelsAsync(T3kTone? tone)
    {
        int gen = ++_loadGeneration;                          // shares the counter with LoadAsync
        SelectedModels.Clear();
        if (tone is null || _client is null) return;
        try
        {
            var models = await _client.GetModelsAsync(tone.Id);
            _dispatch(() => { if (gen != _loadGeneration) return; foreach (var m in models) SelectedModels.Add(m); });
        }
        catch (T3kException ex)
        {
            FlagAuthIfNeeded(ex);
            _dispatch(() => { if (gen != _loadGeneration) return; Banner = ex.Message; });
        }
    }

    /// <summary>I4: a dead/expired session (T3kError.Auth) flips the tab back to the
    /// signed-out card instead of leaving a stale "signed in" state with a banner nobody reads.</summary>
    private void FlagAuthIfNeeded(T3kException ex)
    { if (ex.Kind == T3kError.Auth) IsSignedIn = _auth?.IsSignedIn ?? false; }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (_client is null || Selected is not { } t) return;
        try { await _client.SetFavoriteAsync(t.Id, favorite: true); }
        catch (T3kException ex) { Banner = ex.Message; FlagAuthIfNeeded(ex); }
    }

    [RelayCommand]
    private async Task SendToPedalAsync(T3kModel? model)
    {
        if (model is null || _downloader is null || !IsDeviceReady) return;
        var tone = Selected;                  // I1: capture BEFORE the download await — a selection
        Banner = null;                        // change mid-download must not re-stamp the wrong tone.
        try
        {
            var path = await _downloader.DownloadAsync(model, tone?.Format);
            // T3kModel.Format is always null on the live API (docs/tone3000-api-findings.md) —
            // derive isIr from the parent tone's Format first, falling back to the file extension.
            bool isIr = tone?.Format is "ir" or "wav" ||
                        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            string? notes = tone is { } t ? $"{t.Title} by {t.Author} (Tone3000)" : null;
            string? url = tone?.PageUrl;
            SendToPedalRequested?.Invoke(path, notes, url, isIr);
        }
        catch (T3kException ex) { Banner = ex.Message; FlagAuthIfNeeded(ex); }
    }

    [RelayCommand]
    private void OpenTonePage()
    {
        if (Selected?.PageUrl is not { Length: > 0 } url ||
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a link must never crash the app */ }
    }

    [RelayCommand] private void SelectTone(T3kTone? tone) => Selected = tone;
    [RelayCommand] private void SetFormat(string? format) => FormatFilter = FormatFilter == format ? null : format;
}

/// <summary>XAML glue for the Tone3000 view: chip checked-state.</summary>
public static class T3kConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter IsNam =
        new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(f => f == "nam");
    public static readonly Avalonia.Data.Converters.IValueConverter IsIr =
        new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(f => f == "ir");
}

/// <summary>Attached property that loads a remote image into an Image control off the UI
/// thread, with silent failure (the ♪ placeholder behind it stays visible).</summary>
public static class RemoteImage
{
    private static readonly HttpClient Http = new();

    public static readonly Avalonia.AttachedProperty<string?> SourceProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<Avalonia.Controls.Image, string?>("Source", typeof(RemoteImage));

    public static void SetSource(Avalonia.Controls.Image image, string? url) => image.SetValue(SourceProperty, url);
    public static string? GetSource(Avalonia.Controls.Image image) => image.GetValue(SourceProperty);

    static RemoteImage()
    {
        SourceProperty.Changed.AddClassHandler<Avalonia.Controls.Image>(async (img, e) =>
        {
            if (e.NewValue is not string url || !url.StartsWith("http")) { img.Source = null; return; }
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                img.Source = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            catch { /* placeholder stays */ }
        });
    }
}
