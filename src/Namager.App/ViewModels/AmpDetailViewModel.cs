using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;
using Sonulab.Distill;

namespace Namager.App.ViewModels;

/// <summary>The amp *detail* concern, lifted out of AmpListViewModel so it can render in more than
/// one place: inline on the Amps tab, and in the preset editor's flyout (#9). Knows nothing about
/// list selection, uploads, or metadata editing — it loads one amp's metadata and exposes it.
/// The metadata reader is injected so hosts (and tests) supply their own.</summary>
public sealed partial class AmpDetailViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly Func<int, CancellationToken, Task<AmpMetadata?>> _readMetadata;

    public AmpDetailViewModel(Func<int, CancellationToken, Task<AmpMetadata?>> readMetadata) =>
        _readMetadata = readMetadata;

    [ObservableProperty] private string? _name;
    [ObservableProperty] private int _displaySlot;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _showNoMetadata;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private string? _error;
    public ObservableCollection<MetadataField> Fields { get; } = new();

    /// <summary>Last load — test seam: call LoadAsync, then await this.</summary>
    public Task? LoadTask { get; private set; }

    private readonly Dictionary<int, (string Name, AmpMetadata? Meta)> _cache = new();
    // Cancelled but deliberately never disposed: a superseded in-flight read may still be
    // awaiting on this token when the next selection cancels it, and disposing here could
    // throw ObjectDisposedException on that read's resumption. The CTS is small and
    // short-lived, so leaking it until GC is an acceptable tradeoff over that race.
    private CancellationTokenSource? _cts;

    /// <summary>Hide the pane and drop whatever it was showing.</summary>
    public void Clear()
    {
        _cts?.Cancel();
        ResetContent();
        IsVisible = false;
    }

    private void ResetContent()
    {
        Fields.Clear();
        Notes = null; Url = null; Error = null; ShowNoMetadata = false;
    }

    /// <summary>Show the detail for one amp slot. <paramref name="index"/> below zero means the name
    /// could not be resolved to a slot on this pedal — surfaced as an error, never read.</summary>
    public Task LoadAsync(int index, string name, bool isEmpty)
    {
        LoadTask = LoadCoreAsync(index, name, isEmpty);
        return LoadTask;
    }

    /// <summary>Show an error in the pane without attempting a read.</summary>
    public void SetError(string message)
    {
        _cts?.Cancel();
        ResetContent();
        IsLoading = false;
        IsVisible = true;
        Error = message;
    }

    private async Task LoadCoreAsync(int index, string name, bool isEmpty)
    {
        _cts?.Cancel();
        // Clear the pane SYNCHRONOUSLY on every load: the previous amp's fields must never
        // remain visible while another amp's read is in flight (or after a cancelled one).
        ResetContent();
        if (isEmpty) { IsVisible = false; return; }
        Name = name;
        DisplaySlot = index + 1;
        IsVisible = true;

        if (index < 0)
        {
            IsLoading = false;
            Error = $"'{name}' is not in the amp list on this pedal.";
            return;
        }

        if (!_cache.TryGetValue(index, out var entry) || entry.Name != name)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            IsLoading = true;
            try
            {
                entry = (name, await _readMetadata(index, cts.Token));
            }
            catch (OperationCanceledException) { return; }   // superseded by a newer selection,
                                                             // which owns the pane now — don't touch it
            catch (AmpServiceException ex)
            { Error = ex.Message; Fields.Clear(); return; }
            catch (Exception ex)
            {
                // Raw transport failure (e.g. the link dying mid-session). This task is awaited by
                // AmpListViewModel.SaveMetadataAsync's tail — an escape there is an unhandled
                // UI-thread rethrow (field-crash class). Surface it like a service failure.
                Log.Warn(ex, "amp details read failed");
                Error = $"Read failed: {ex.Message}";
                Fields.Clear();
                return;
            }
            finally { if (_cts == cts) IsLoading = false; }
            // Superseded by a newer load: don't let a stale read populate the cache or the pane.
            if (cts.IsCancellationRequested || _cts != cts) return;
            _cache[index] = entry;
        }
        else
        {
            IsLoading = false;   // cache hit: no read is in flight, so the spinner never applies
        }
        Populate(entry.Meta);
    }

    private void Populate(AmpMetadata? meta)
    {
        Fields.Clear();
        if (meta is null) { ShowNoMetadata = true; return; }
        ShowNoMetadata = false;
        if (meta.Source?.File is { } f) Fields.Add(new("Source file", f));
        if (meta.Source?.Size is { } sz) Fields.Add(new("Source size", FormatSize(sz)));
        if (meta.Source?.Modified is { } mo) Fields.Add(new("Source date", mo));
        if (meta.Uploaded is { } up) Fields.Add(new("Uploaded", up));
        if (meta.Nam is { } nam)
            foreach (var kv in nam)
                if (kv.Value is System.Text.Json.Nodes.JsonValue v)
                    Fields.Add(new($"NAM {kv.Key}", v.ToString()));
        if (meta.Distill?.Version is { } dv) Fields.Add(new("Distilled by", $"v{dv}"));
        if (meta.Distill?.ShapeErr is { } se) Fields.Add(new("Fit error", $"{se:F3} (lower is better)"));
        Notes = meta.Notes;
        Url = meta.Url;
    }

    private static string FormatSize(long b) =>
        b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : b >= 1 << 10 ? $"{b / 1024.0:F1} KB" : $"{b} B";

    /// <summary>Cancel any in-flight read without touching what the pane shows. Callers awaiting
    /// <see cref="LoadTask"/> drain it before starting a write burst — a full-slot dread overlapping
    /// a write burst can silently discard the commit (HwCheck finding).</summary>
    public void CancelInFlight() => _cts?.Cancel();

    /// <summary>Drop every cached entry (the slot list changed under us).</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>The cached metadata for a slot, when a load has already read it. The metadata editor
    /// needs this to build its candidate record without a second device read.</summary>
    public bool TryGetCached(int index, out AmpMetadata? meta)
    {
        if (_cache.TryGetValue(index, out var entry)) { meta = entry.Meta; return true; }
        meta = null;
        return false;
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (Url is { Length: > 0 } url &&
            (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* opening a link (missing handler, OS quirk, etc.) must never crash the app */ }
        }
    }
}
