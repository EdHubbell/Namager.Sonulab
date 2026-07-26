using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Namager.App.ViewModels;

public partial class IrListViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IrService _irs;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;
    private readonly Namager.App.Services.IPresetUsageService _usage;
    private readonly Namager.App.Services.CatalogVersion _catalog;

    /// <summary>.wav -> device blob seam — Sonulab.Distill.WavToIr.Convert in the app,
    /// a fake in tests. Conversion is instant and synchronous (no cancel/progress needed).</summary>
    private readonly Func<string, byte[]> _convertWav;
    private readonly Action<Action> _dispatch;              // marshals worker-thread progress to the UI thread
    private string _uploadSourcePath = "";

    /// <summary>Where the Tone3000 IR identity index is read from and written to. Null = the real
    /// %APPDATA%\Namager\ir-index.json (matches how UsagePingService takes statePath). Tests pass a
    /// temp path — a test that records an entry must never touch the developer's own index.</summary>
    private readonly string? _irIndexPath;

    /// <summary>Set by BeginUploadFromTone3000, consumed (and cleared) once the pending upload's
    /// device write succeeds. Cleared by BeginUpload too, so a later local-file upload can never
    /// inherit a stale identity.</summary>
    private T3kIrSource? _pendingSource;

    public IrListViewModel(IrService irs, bool writesAllowed,
                           Namager.App.Services.IStatusService? status = null,
                           Func<string, byte[]>? convertWav = null,
                           Action<Action>? dispatch = null,
                           Namager.App.Services.IPresetUsageService? usage = null,
                           Namager.App.Services.CatalogVersion? catalog = null,
                           string? irIndexPath = null)
    {
        _irs = irs; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _convertWav = convertWav ?? Sonulab.Distill.WavToIr.Convert;
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance;
        _catalog = catalog ?? new Namager.App.Services.CatalogVersion();
        _irIndexPath = irIndexPath;
        // Progressive highlight fill: the background scan publishes after each preset resolves.
        // MapUpdated may fire on a worker thread — marshal through the dispatch seam.
        _usage.MapUpdated += () => _dispatch(ApplyUsage);
    }

    public ObservableCollection<IrItemViewModel> Items { get; } = new();
    [ObservableProperty] private IrItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Reads allowed while nothing is running (no writes requirement).</summary>
    public bool CanRefresh => !IsBusy && !IsUploading;
    /// <summary>Mutating operations additionally require writesAllowed.</summary>
    public bool CanMutate => _writes && CanRefresh;
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanMutate)); }
    partial void OnIsUploadingChanged(bool value)
    { OnPropertyChanged(nameof(CanRefresh)); OnPropertyChanged(nameof(CanMutate)); }

    /// <summary>Busy-gated write helper (mirrors AmpListViewModel.RunAsync) with an
    /// error channel: IR operations throw IrServiceException on guarded-write failures.</summary>
    private async Task<bool> RunAsync(string message, string success, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try
        {
            await work();
            await ReloadAsync();
            // Every RunAsync caller (delete / rename / reorder) changes the IR NAME LIST or its
            // order, which is exactly what the parameter editor's picker shows. Reads never come
            // through here, so this can't fire on a plain refresh.
            _catalog.Bump();
            _status.Success(success);
            return true;
        }
        catch (IrServiceException ex) { ErrorMessage = ex.Message; _status.Failure(ex.Message); return false; }
        catch (Exception ex)
        {
            // Transport/unexpected failures (e.g. the WiFi link dying mid-session) must surface,
            // never escape the [RelayCommand] (unhandled = process death — field crash class).
            Log.Warn(ex, "IR operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        var slots = await _irs.ListIrsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new IrItemViewModel(s));
        ApplyUsage();
        _usage.EnsureScanning();     // non-blocking: highlights stream in via MapUpdated
    }

    /// <summary>Tag each item with the presets that use it, from the CURRENT (possibly partial
    /// or stale) map — best-effort by design; the fail-closed check lives in the guards.</summary>
    private void ApplyUsage()
    {
        var map = _usage.Current;
        foreach (var item in Items)
            item.UsedInPresets = item.IsEmpty
                ? System.Array.Empty<PresetRef>() : map.PresetsUsingIr(item.Name);
    }

    /// <summary>Re-apply highlighting from the current map and make sure a scan is running if
    /// it is incomplete/stale. Never sets IsBusy — the scan streams in via MapUpdated.</summary>
    public Task RefreshUsageAsync()
    {
        ApplyUsage();
        _usage.EnsureScanning();
        return Task.CompletedTask;
    }

    [RelayCommand] private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsBusy = true; BusyMessage = "Reading IRs…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading IRs…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            // Same crash-guard as amps/presets: a dead link must surface, not tear down the app.
            Log.Warn(ex, "IR refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    /// <summary>Fail-closed guard: resolve the preset-usage of <paramref name="s"/> COMPLETELY
    /// before a delete/rename. If the map is incomplete, finishes the scan now (foreground reads,
    /// status-scoped). Returns null when usage cannot be determined — the caller must refuse.</summary>
    private async Task<IReadOnlyList<PresetRef>?> ResolveUsageAsync(IrItemViewModel s)
    {
        if (_usage.IsComplete) return _usage.Current.PresetsUsingIr(s.Name);
        IsBusy = true; BusyMessage = "Checking preset usage…";
        using var op = _status.BeginOperation("Checking preset usage…");
        try { return (await _usage.EnsureCompleteAsync()).PresetsUsingIr(s.Name); }
        catch (Exception ex)
        {
            Log.Warn(ex, "usage check failed");
            ErrorMessage = "Couldn't verify preset usage — try again.";
            _status.Failure("Couldn't verify preset usage.");
            return null;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        if (await ResolveUsageAsync(s) is not { } refs) return;        // unknown → refuse
        s.UsedInPresets = refs;
        if (refs.Count > 0) { BlockUsed(s, "delete"); return; }
        await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _irs.DeleteIrAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(IrItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (await ResolveUsageAsync(s) is not { } refs) { s.IsEditing = false; return; }
        s.UsedInPresets = refs;
        if (refs.Count > 0) { s.IsEditing = false; BlockUsed(s, "rename"); return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _irs.RenameIrAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }

    [RelayCommand] private async Task MoveItemUpAsync(IrItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _irs.MoveIrStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(IrItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= SlotBlobService.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _irs.MoveIrStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }

    /// <summary>Refuse a delete/rename of an IR a preset references, and say which presets.</summary>
    private void BlockUsed(IrItemViewModel s, string verb)
    {
        var presets = s.UsedInPresets;
        ErrorMessage =
            $"This IR file is used in the following presets: {Namager.App.Services.PresetRefFormat.Join(presets)}. " +
            $"You can only {verb} files that aren't in an active preset.";
        _status.Failure($"Can't {verb} '{s.Name}' — used by {presets.Count} preset{(presets.Count == 1 ? "" : "s")}.");
    }

    // ---- upload panel state ----
    [ObservableProperty] private bool _isUploadPanelOpen;
    [ObservableProperty] private string _uploadSourceFileName = "";
    [ObservableProperty] private string _uploadName = "";
    public ObservableCollection<int> EmptySlots { get; } = new();
    [ObservableProperty] private int? _selectedEmptySlot;
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _uploadStatus = "";
    [ObservableProperty] private string? _uploadError;
    [ObservableProperty] private double _uploadProgressValue;
    [ObservableProperty] private string? _uploadBlockedMessage;

    /// <summary>Open the upload panel for a picked .wav/.irblob file (called by the view
    /// after the OS file picker). Empty slots only — spec decision.</summary>
    [RelayCommand] private void BeginUpload(string? path)
    {
        // Clear any Tone3000 identity from a previous BeginUploadFromTone3000 call — a local-file
        // upload started afterward (even one that no-ops below) must never inherit a stale identity.
        _pendingSource = null;
        if (!CanMutate || string.IsNullOrEmpty(path)) return;
        UploadBlockedMessage = null;
        EmptySlots.Clear();
        foreach (var i in Items.Where(i => i.IsEmpty).Select(i => i.Index)) EmptySlots.Add(i);
        if (EmptySlots.Count == 0)
        {
            UploadBlockedMessage = "No empty IR slots — delete an IR first, then upload.";
            IsUploadPanelOpen = false;
            return;
        }
        _uploadSourcePath = path;
        UploadSourceFileName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        UploadName = stem.Length > SlotBlobService.NameMaxChars ? stem[..SlotBlobService.NameMaxChars] : stem;
        SelectedEmptySlot = EmptySlots[0];
        UploadError = null; UploadStatus = ""; UploadProgressValue = 0;
        IsUploadPanelOpen = true;
    }

    /// <summary>Begins an upload that came from Tone3000, remembering the identity to record in the
    /// IR index once the write succeeds. Prefills the name the same way BeginUpload does.</summary>
    public void BeginUploadFromTone3000(string path, T3kIrSource source)
    {
        BeginUploadCommand.Execute(path);
        // BeginUpload clears _pendingSource unconditionally, then only stages the upload — setting
        // _uploadSourcePath and opening the panel — when writes are allowed AND an empty slot
        // exists; on either blocked branch (writes gated, no empty slots) it returns early without
        // touching those two fields. Checking the staged path/panel state (rather than
        // re-deriving CanMutate/empty-slot logic here, which would drift from BeginUpload's own
        // guards) is the reliable signal that THIS call actually armed an upload — arming the
        // identity on a blocked call would let a later, unrelated local-file upload land under a
        // stale Tone3000 identity.
        if (IsUploadPanelOpen && _uploadSourcePath == path) _pendingSource = source;
    }

    [RelayCommand] private async Task StartUploadAsync()
    {
        if (!_writes || IsUploading || IsBusy || SelectedEmptySlot is not int slot) return;
        var name = UploadName.Trim();
        if (name.Length == 0) { UploadError = "Enter an IR name."; return; }
        if (Items.Any(i => !i.IsEmpty && string.Equals(i.Name, name, StringComparison.Ordinal)))
        { UploadError = $"An IR named '{name}' already exists — names must be unique."; return; }

        UploadError = null;
        using var op = _status.BeginOperation($"Uploading '{name}'…");
        IsUploading = true;
        try
        {
            byte[] bytes = Path.GetExtension(_uploadSourcePath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? _convertWav(_uploadSourcePath)
                : await File.ReadAllBytesAsync(_uploadSourcePath);

            var uploadProgress = new SyncActionProgress<SlotUploadProgress>(p =>
            {
                UploadProgressValue = p.ChunksTotal > 0 ? (double)p.ChunksDone / p.ChunksTotal : 0;
                UploadStatus = p.Stage switch
                {
                    SlotUploadStage.BackingUp => "Backing up slot…",
                    SlotUploadStage.Writing => $"Writing chunk {p.ChunksDone}/{p.ChunksTotal}",
                    SlotUploadStage.Verifying => "Verifying…",
                    _ => $"Done — '{name}' in slot {slot + 1}",
                };
            });
            await _irs.UploadIrAsync(slot, bytes, name, uploadProgress);

            // Record identity only after the device write succeeded: an index entry for content
            // that never landed would resolve to a slot the user doesn't have.
            if (_pendingSource is { } src)
            {
                Namager.App.Services.IrIndex.Load(_irIndexPath)
                       .Record(new Namager.App.Services.IrIndexEntry(
                           Namager.App.Services.IrIndex.ShaOf(bytes), src.ToneId, src.ModelId, src.Title))
                       .Save(_irIndexPath);
                _pendingSource = null;
            }

            UploadStatus = $"Done — '{name}' in slot {slot + 1}";
            await ReloadAsync();
            Selected = Items.FirstOrDefault(i => i.Index == slot);
            _status.Success($"Uploaded '{name}' to slot {slot + 1}");
            _catalog.Bump();                                         // a new IR must appear in the editor's picker
        }
        catch (InvalidDataException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (IrServiceException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (IOException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (UnauthorizedAccessException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (Exception ex)
        {
            // Transport failures (link dying mid-upload) must surface, never escape (crash class).
            Log.Warn(ex, "IR upload failed");
            UploadError = $"Upload failed: {ex.Message}";
            _status.Failure($"Upload failed: {ex.Message}");
        }
        finally { IsUploading = false; }
    }

    [RelayCommand] private void CloseUploadPanel() { if (!IsUploading) IsUploadPanelOpen = false; }

    /// <summary>Synchronous IProgress: IrService progress arrives on the awaiter's context
    /// already — nothing to marshal (conversion is synchronous, no worker thread involved).</summary>
    private sealed class SyncActionProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
