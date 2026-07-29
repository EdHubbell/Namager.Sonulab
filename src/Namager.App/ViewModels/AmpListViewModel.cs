using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;
using Sonulab.Distill;

namespace Namager.App.ViewModels;

public partial class AmpListViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly AmpService _amps;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;
    private readonly Namager.App.Services.IPresetUsageService _usage;
    private readonly Namager.App.Services.CatalogVersion _catalog;

    /// <summary>Distillation seam — Sonulab.Distill.Distiller.DistillAsync in the app,
    /// a fake in tests. Returns the fidelity ShapeErr (lower is better).</summary>
    public delegate Task<double> DistillRunner(string namPath, string outPath,
        IProgress<Sonulab.Distill.DistillProgress>? progress, CancellationToken ct);

    private readonly DistillRunner _distill;
    private readonly string _distilledDir;
    private readonly Action<Action> _dispatch;              // marshals worker-thread progress to the UI thread
    private string _uploadSourcePath = "";
    private CancellationTokenSource? _uploadCts;

    public AmpListViewModel(AmpService amps, bool writesAllowed,
        Namager.App.Services.IStatusService? status = null,
        DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null,
        Namager.App.Services.IPresetUsageService? usage = null,
        Namager.App.Services.CatalogVersion? catalog = null,
        Namager.App.Services.IPresetNavigator? navigator = null)
    {
        _amps = amps; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _distill = distill ?? Sonulab.Distill.Distiller.DistillAsync;
        _distilledDir = distilledDir ?? Path.Combine("NAMFiles", "Distilled");
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance;
        _catalog = catalog ?? new Namager.App.Services.CatalogVersion();
        // Progressive highlight fill: the background scan publishes after each preset resolves.
        // MapUpdated may fire on a worker thread — marshal through the dispatch seam.
        _usage.MapUpdated += () => _dispatch(ApplyUsage);
        Detail = new AmpDetailViewModel(ReadMetadataAsync, _usage, navigator, _dispatch);
    }

    /// <summary>The selected amp's metadata card. Its own view-model so the preset editor can render
    /// the identical card in a flyout (#9) — see AmpDetailViewModel.</summary>
    public AmpDetailViewModel Detail { get; }

    public ObservableCollection<AmpItemViewModel> Items { get; } = new();
    [ObservableProperty] private AmpItemViewModel? _selected;
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

    /// <summary>Busy-gated write helper (mirrors PresetListViewModel.RunAsync) with an
    /// error channel: amp operations throw AmpServiceException on guarded-write failures.</summary>
    private async Task<bool> RunAsync(string message, string success, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        // Drain any in-flight details read before a write burst starts: a full-slot dread
        // overlapping a write burst can silently discard the commit (HwCheck finding) — the
        // two must never interleave even though SonuClient serializes individual commands.
        Detail.CancelInFlight();
        if (DetailsLoadTask is { } detailsLoad)
        { try { await detailsLoad; } catch { /* cancelled/superseded read */ } }
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try
        {
            await work();
            await ReloadAsync();
            // Every RunAsync caller (delete / rename / reorder) changes the amp NAME LIST or its
            // order, which is exactly what the parameter editor's picker shows. Reads never come
            // through here, so this can't fire on a plain refresh.
            _catalog.Bump();
            _status.Success(success);
            return true;
        }
        catch (AmpServiceException ex) { ErrorMessage = ex.Message; _status.Failure(ex.Message); return false; }
        catch (Exception ex)
        {
            // Transport/unexpected failures (e.g. the WiFi link dying mid-session) must surface,
            // never escape the [RelayCommand] — an unhandled rethrow on the UI thread killed the
            // app in the field (v0.9.3 test build, amps refresh over WiFi).
            Log.Warn(ex, "amp operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        Detail.CancelInFlight();    // an in-flight details read must not repopulate the cache below
        Detail.ClearCache();
        var slots = await _amps.ListAmpsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new AmpItemViewModel(s));
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
                ? System.Array.Empty<Sonulab.Core.Services.PresetRef>() : map.PresetsUsingAmp(item.Name);
    }

    /// <summary>Re-apply highlighting from the current map and make sure a scan is running if
    /// it is incomplete/stale. Never sets IsBusy — the scan streams in via MapUpdated.</summary>
    public Task RefreshUsageAsync()
    {
        ApplyUsage();
        _usage.EnsureScanning();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // Wrap in the busy pattern so OnSelectedChanged's guard blocks a details read from
        // interleaving with a plain refresh (mirrors RunAsync, but not write-gated).
        if (!CanRefresh) return;
        IsBusy = true; BusyMessage = "Reading amps…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading amps…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            // Field crash (v0.9.3 test build): the WiFi link died and this command had no catch —
            // AsyncRelayCommand rethrew on the UI thread and took the app down.
            Log.Warn(ex, "amp refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    /// <summary>Fail-closed guard: resolve the preset-usage of <paramref name="s"/> COMPLETELY
    /// before a delete/rename. If the map is incomplete, finishes the scan now (foreground reads,
    /// status-scoped). Returns null when usage cannot be determined — the caller must refuse.</summary>
    private async Task<IReadOnlyList<Sonulab.Core.Services.PresetRef>?> ResolveUsageAsync(AmpItemViewModel s)
    {
        if (_usage.IsComplete) return _usage.Current.PresetsUsingAmp(s.Name);
        IsBusy = true; BusyMessage = "Checking preset usage…";
        using var op = _status.BeginOperation("Checking preset usage…");
        try { return (await _usage.EnsureCompleteAsync()).PresetsUsingAmp(s.Name); }
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
        await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task MoveItemUpAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _amps.MoveAmpStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= AmpService.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _amps.MoveAmpStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (await ResolveUsageAsync(s) is not { } refs) { s.IsEditing = false; return; }
        s.UsedInPresets = refs;
        if (refs.Count > 0) { s.IsEditing = false; BlockUsed(s, "rename"); return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
    }

    /// <summary>Refuse a delete/rename of an amp a preset references, and say which presets.
    /// Renaming/deleting it would leave those presets pointing at a name the device can't resolve.</summary>
    private void BlockUsed(AmpItemViewModel s, string verb)
    {
        var presets = s.UsedInPresets;
        ErrorMessage =
            $"This amp file is used in the following presets: {Namager.App.Services.PresetRefFormat.Join(presets)}. " +
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
    [ObservableProperty] private bool _canCancelUpload;
    [ObservableProperty] private string _uploadStatus = "";
    [ObservableProperty] private string? _uploadError;
    [ObservableProperty] private double _uploadProgressValue;
    [ObservableProperty] private bool _isUploadIndeterminate;
    [ObservableProperty] private string? _uploadBlockedMessage;

    [ObservableProperty] private string _uploadNotes = "";
    [ObservableProperty] private string _uploadUrl = "";
    private AmpSourceInfo? _pendingSource;                  // captured at BeginUpload
    private JsonObject? _pendingNam;                        // .nam metadata passthrough
    private AmpMetadata? _pendingExisting;                  // pre-existing block of a picked .vxamp

    partial void OnUploadNotesChanged(string value) => OnPropertyChanged(nameof(NotesBudgetWarning));
    partial void OnUploadUrlChanged(string value) => OnPropertyChanged(nameof(NotesBudgetWarning));

    /// <summary>Live budget check: the SSMD JSON cap is 4024 B; warn (not block) when the
    /// notes would be truncated. Uses a fixed-width ShapeErr placeholder pre-distillation.</summary>
    public string? NotesBudgetWarning
    {
        get
        {
            int total = VxampMetadata.JsonByteCount(BuildUploadMetadata(0.1234567890123456));
            int over = total - VxampMetadata.MaxJsonBytes;
            return over > 0 ? $"Metadata is {over} B over budget — notes will be truncated on upload." : null;
        }
    }

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    private static string? NullIfEmpty(string s) => s.Trim().Length == 0 ? null : s.Trim();

    private static string? DistillerVersion() =>
        typeof(Sonulab.Distill.Distiller).Assembly.GetName().Version?.ToString(3);

    /// <summary>Read the top-level "metadata" object of a .nam. Failures degrade to null —
    /// metadata capture must never block an upload (spec §5).</summary>
    private static JsonObject? TryReadNamMetadataFile(string namPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(namPath))?["metadata"] is JsonObject o
                ? (JsonObject)o.DeepClone() : null;
        }
        catch { return null; }
    }

    private static AmpSourceInfo? TryCaptureSource(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return new AmpSourceInfo(fi.Name, fi.Length,
                fi.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        catch { return new AmpSourceInfo(Path.GetFileName(path)); }
    }

    /// <summary>Merge captured + user-entered metadata for the pending upload. For a .vxamp
    /// with an existing block, its fields are kept and only user-entered fields overwrite.</summary>
    private AmpMetadata BuildUploadMetadata(double? shapeErr)
    {
        bool isNam = Path.GetExtension(_uploadSourcePath).Equals(".nam", StringComparison.OrdinalIgnoreCase);
        var baseline = _pendingExisting ?? new AmpMetadata();
        return baseline with
        {
            Source = _pendingSource,
            Uploaded = NowIso(),
            Nam = isNam ? _pendingNam : baseline.Nam,
            Distill = isNam ? new AmpDistillInfo(DistillerVersion(), shapeErr) : baseline.Distill,
            Notes = NullIfEmpty(UploadNotes) ?? baseline.Notes,
            Url = NullIfEmpty(UploadUrl) ?? baseline.Url,
        };
    }

    /// <summary>Open the upload panel for a picked .nam/.vxamp file (called by the view
    /// after the OS file picker). Empty slots only — spec decision.</summary>
    [RelayCommand] private void BeginUpload(string? path)
    { if (path is not null) BeginUploadPrefilled(path, notes: null, url: null); }

    /// <summary>Open the upload panel for a picked .nam/.vxamp file, optionally prefilled
    /// with provenance (the Tone3000 handoff, spec 2026-07-07 §3). Null notes/url makes
    /// this byte-identical to the plain command path.</summary>
    public void BeginUploadPrefilled(string path, string? notes, string? url)
    {
        if (!CanMutate || string.IsNullOrEmpty(path)) return;
        UploadBlockedMessage = null;
        EmptySlots.Clear();
        foreach (var i in Items.Where(i => i.IsEmpty).Select(i => i.Index)) EmptySlots.Add(i);
        if (EmptySlots.Count == 0)
        {
            UploadBlockedMessage = "No empty amp slots — delete an amp first, then upload.";
            IsUploadPanelOpen = false;
            return;
        }
        _uploadSourcePath = path;
        UploadSourceFileName = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        UploadName = stem.Length > AmpService.NameMaxChars ? stem[..AmpService.NameMaxChars] : stem;
        SelectedEmptySlot = EmptySlots[0];
        UploadError = null; UploadStatus = ""; UploadProgressValue = 0;

        _pendingSource = TryCaptureSource(path);
        _pendingNam = null; _pendingExisting = null;
        UploadNotes = ""; UploadUrl = "";
        if (Path.GetExtension(path).Equals(".nam", StringComparison.OrdinalIgnoreCase))
            _pendingNam = TryReadNamMetadataFile(path);
        else
        {
            try
            {
                _pendingExisting = VxampMetadata.TryRead(File.ReadAllBytes(path));
                UploadNotes = _pendingExisting?.Notes ?? "";
                UploadUrl = _pendingExisting?.Url ?? "";
            }
            catch { /* unreadable file will fail loudly at StartUpload; metadata never blocks */ }
        }

        // The warning depends on _pendingNam/_pendingSource/_pendingExisting too, which change
        // per pick even when the Notes/Url setters above no-op (e.g. both "" -> "") and so don't
        // raise their own change notification.
        OnPropertyChanged(nameof(NotesBudgetWarning));
        IsUploadPanelOpen = true;
        if (notes is not null) UploadNotes = notes;
        if (url is not null) UploadUrl = url;
    }

    [RelayCommand] private async Task StartUploadAsync()
    {
        if (!_writes || IsUploading || IsBusy || SelectedEmptySlot is not int slot) return;
        var name = UploadName.Trim();
        if (name.Length == 0) { UploadError = "Enter an amp name."; return; }
        if (Items.Any(i => !i.IsEmpty && string.Equals(i.Name, name, StringComparison.Ordinal)))
        { UploadError = $"An amp named '{name}' already exists — names must be unique."; return; }

        // Drain any in-flight details read before device work begins: a full-slot dread
        // overlapping the write burst can silently discard the commit (HwCheck finding).
        Detail.CancelInFlight();
        if (DetailsLoadTask is { } detailsLoad)
        { try { await detailsLoad; } catch { /* cancelled/superseded read */ } }

        UploadError = null;
        IsUploading = true;
        _uploadCts = new CancellationTokenSource();
        using var op = _status.BeginOperation($"Uploading '{name}'…");
        try
        {
            double? shapeErr = null;
            string vxampPath = _uploadSourcePath;
            if (Path.GetExtension(_uploadSourcePath).Equals(".nam", StringComparison.OrdinalIgnoreCase))
            {
                CanCancelUpload = true;                     // safe to cancel: nothing written yet
                IsUploadIndeterminate = true;
                UploadStatus = "Distilling…";
                Directory.CreateDirectory(_distilledDir);
                vxampPath = Path.Combine(_distilledDir, $"{name}.vxamp");
                var distillProgress = new SyncActionProgress<Sonulab.Distill.DistillProgress>(
                    p => _dispatch(() => UploadStatus = $"Distilling — {p.Message}"));
                shapeErr = await _distill(_uploadSourcePath, vxampPath, distillProgress, _uploadCts.Token);
            }

            CanCancelUpload = false;                        // device writes begin: no cancelling now
            IsUploadIndeterminate = false;
            var bytes = await File.ReadAllBytesAsync(vxampPath);
            try
            {
                VxampMetadata.Write(bytes, BuildUploadMetadata(shapeErr));
                if (!vxampPath.Equals(_uploadSourcePath, StringComparison.OrdinalIgnoreCase))
                    await File.WriteAllBytesAsync(vxampPath, bytes);   // only rewrite our own distilled copy
            }
            catch { /* spec §5: metadata failure must never block the upload */ }
            var uploadProgress = new SyncActionProgress<AmpUploadProgress>(p =>
            {
                UploadProgressValue = p.ChunksTotal > 0 ? (double)p.ChunksDone / p.ChunksTotal : 0;
                UploadStatus = p.Stage switch
                {
                    AmpUploadStage.BackingUp => "Backing up slot…",
                    AmpUploadStage.Writing => $"Writing chunk {p.ChunksDone}/{p.ChunksTotal}",
                    AmpUploadStage.Verifying => "Verifying…",
                    _ => $"Done — '{name}' in slot {slot + 1}",
                };
            });
            await _amps.UploadAmpAsync(slot, bytes, name, uploadProgress);

            UploadStatus = $"Done — '{name}' in slot {slot + 1}";
            await ReloadAsync();
            Selected = Items.FirstOrDefault(i => i.Index == slot);
            DetailsLoadTask = Selected is { } sel
                ? Detail.LoadAsync(sel.Index, sel.Name, sel.IsEmpty)
                : Task.CompletedTask;
            await DetailsLoadTask;

            IsUploadPanelOpen = false;                               // #5: auto-close into the detail view
            _status.Success($"Uploaded '{name}' to slot {slot + 1}");
            _catalog.Bump();                                         // a new amp must appear in the editor's picker
        }
        catch (OperationCanceledException) { UploadError = "Cancelled."; }
        catch (Sonulab.Distill.DistillException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (AmpServiceException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (IOException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (UnauthorizedAccessException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (Exception ex)
        {
            // The longest-running device op is the MOST exposed to the link dying mid-session —
            // a transport exception here escaped the [RelayCommand] and killed the app (review
            // finding on the first crash-guard sweep). Surface it like every other failure.
            Log.Warn(ex, "amp upload failed");
            UploadError = $"Upload failed: {ex.Message}";
            _status.Failure($"Upload failed: {ex.Message}");
        }
        finally
        {
            IsUploading = false; CanCancelUpload = false; IsUploadIndeterminate = false;
            _uploadCts?.Dispose(); _uploadCts = null;
        }
    }

    /// <summary>Only effective during distillation — device writes are never interrupted.</summary>
    [RelayCommand] private void CancelUpload() { if (CanCancelUpload) _uploadCts?.Cancel(); }

    [RelayCommand] private void CloseUploadPanel() { if (!IsUploading) IsUploadPanelOpen = false; }

    /// <summary>Synchronous IProgress: AmpService progress arrives on the awaiter's context
    /// already; distill progress is marshaled by the caller via _dispatch. Progress&lt;T&gt;
    /// would re-post and race unit tests.</summary>
    private sealed class SyncActionProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ---- details pane (selected amp metadata) ----
    // The detail concern itself lives in AmpDetailViewModel so the preset editor can render the same
    // card in a flyout (#9). This VM keeps only the wiring: when to load, and when to stand down.

    /// <summary>Last details load — test seam: set Selected, then await this.</summary>
    public Task? DetailsLoadTask { get; private set; }

    partial void OnSelectedChanged(AmpItemViewModel? value)
    {
        // Any selection change cancels an open edit — EditNotes/EditUrl are VM-level state,
        // not per-amp, so a leftover edit must never get stamped onto a different amp's
        // metadata. Unconditional (before the busy guard below) so it still applies when
        // the guard hides the pane.
        IsEditingMetadata = false;
        // Never issue a read while another device operation may be in flight — serial
        // commands must not interleave. The pane just stays hidden; explicit callers
        // (post-upload, post-save) use Detail.LoadAsync directly once idle.
        if (IsBusy || IsUploading) { Detail.Clear(); return; }
        DetailsLoadTask = value is null
            ? Task.CompletedTask
            : Detail.LoadAsync(value.Index, value.Name, value.IsEmpty);
    }

    private Task<AmpMetadata?> ReadMetadataAsync(int index, CancellationToken ct) =>
        ReadMetadataAsync(_amps, index, ct);

    /// <summary>Region-only metadata fetch: one chunk to find the SSMD block and its length,
    /// then exactly the chunks it spans (~0.4 s typical vs ~5 s full-slot). Display-only —
    /// SaveMetadataAsync still does a FULL fresh read with integrity guards before flashing.
    /// Static so the preset editor's amp flyout (#9) reads metadata exactly the same way.</summary>
    public static async Task<AmpMetadata?> ReadMetadataAsync(AmpService amps, int index, CancellationToken ct)
    {
        var head = await amps.ReadChunksAsync(index, VxampMetadata.FirstRegionChunk, 1, ct);
        var regionStart = head.AsSpan(VxampMetadata.OffsetInFirstChunk);
        if (VxampMetadata.BlockLength(regionStart) is not { } blockLen) return null;

        var region = new byte[VxampMetadata.RegionSize];
        regionStart.CopyTo(region);
        int firstChunkLen = regionStart.Length;   // captured before the next await: Span can't cross it
        int lastChunk = VxampMetadata.LastRegionChunk(blockLen);
        if (lastChunk > VxampMetadata.FirstRegionChunk)
        {
            var rest = await amps.ReadChunksAsync(index, VxampMetadata.FirstRegionChunk + 1,
                                                  lastChunk - VxampMetadata.FirstRegionChunk, ct);
            rest.CopyTo(region, firstChunkLen);
        }
        return VxampMetadata.TryReadRegion(region);
    }

    // ---- metadata editing (notes/url only; auto-captured fields are read-only) ----
    [ObservableProperty] private bool _isEditingMetadata;
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private string _editUrl = "";

    partial void OnEditNotesChanged(string value) => OnPropertyChanged(nameof(EditBudgetWarning));
    partial void OnEditUrlChanged(string value) => OnPropertyChanged(nameof(EditBudgetWarning));

    /// <summary>Live budget check for the edit panel, mirroring <see cref="NotesBudgetWarning"/>:
    /// builds the same candidate metadata SaveMetadataAsync would write and warns (not blocks)
    /// when it would be trimmed/rejected.</summary>
    public string? EditBudgetWarning
    {
        get
        {
            if (Selected is not { IsEmpty: false } s || !Detail.TryGetCached(s.Index, out var cached))
                return null;
            var meta = (cached ?? new AmpMetadata()) with
            {
                Notes = NullIfEmpty(EditNotes),
                Url = NullIfEmpty(EditUrl),
            };
            int total = VxampMetadata.JsonByteCount(meta);
            int over = total - VxampMetadata.MaxJsonBytes;
            return over > 0 ? $"Metadata is {over} B over budget — notes will be truncated on save." : null;
        }
    }

    [RelayCommand]
    private void BeginEditMetadata()
    {
        if (!CanMutate || Selected is not { IsEmpty: false } s) return;
        if (!Detail.TryGetCached(s.Index, out _)) return;   // details not loaded yet
        EditNotes = Detail.Notes ?? "";
        EditUrl = Detail.Url ?? "";
        IsEditingMetadata = true;
    }

    [RelayCommand] private void CancelEditMetadata() => IsEditingMetadata = false;

    /// <summary>Rewrites only the SSMD region, then re-flashes the slot through the guarded
    /// upload path (~4 s: fresh read -> backup -> acked chunks -> verify). The merge base and
    /// payload come from a FRESH device read, never the details cache: a cache entry whose
    /// SSMD block failed to parse (e.g. a glitched earlier read) must not become "there is no
    /// metadata" and wipe the on-device block — nor re-flash corrupted payload bytes
    /// (slot-26 incident, 2026-07-06).</summary>
    [RelayCommand]
    private async Task SaveMetadataAsync()
    {
        if (!IsEditingMetadata) return;    // no open edit (e.g. cancelled by a selection change
                                            // since BeginEdit) — a stale programmatic save is a no-op
        if (Selected is not { IsEmpty: false } s) return;
        int index = s.Index;
        var name = s.Name;
        if (await RunAsync($"Saving metadata for '{name}'…", $"Saved metadata for '{name}'", async () =>
            {
                var bytes = await _amps.ReadAmpAsync(index);   // device truth, length-validated
                // Integrity guards before flashing anything back: the vxamp header is a fixed
                // 32-byte constant, and an UNPARSEABLE-but-non-zero metadata region means the
                // read (or a future block format) can't be trusted as a merge base.
                if (!bytes.AsSpan(0, Sonulab.Distill.VxampFormat.HeaderSize)
                          .SequenceEqual(Sonulab.Distill.VxampFormat.HeaderBytes))
                    throw new AmpServiceException(
                        $"Slot {index + 1} read has a corrupt amp header — not saving. Re-select the amp and try again.");
                var current = VxampMetadata.TryRead(bytes);
                if (current is null &&
                    bytes.AsSpan(VxampMetadata.Offset).IndexOfAnyExcept((byte)0) >= 0)
                    throw new AmpServiceException(
                        $"Slot {index + 1} has an unreadable (corrupt or newer-format) metadata block — refusing to overwrite it. Re-select the amp and try again.");
                var meta = (current ?? new AmpMetadata()) with
                {
                    Notes = NullIfEmpty(EditNotes),
                    Url = NullIfEmpty(EditUrl),
                };
                try { VxampMetadata.Write(bytes, meta); }
                catch (ArgumentException ex)                   // e.g. an over-budget URL, which
                {                                              // the codec never trims
                    throw new AmpServiceException(ex.Message);
                }
                await _amps.UploadAmpAsync(index, bytes, name);
            }))
        {
            IsEditingMetadata = false;
            // Items were rebuilt by ReloadAsync, so this is always a fresh instance — assigning
            // it fires OnSelectedChanged (IsBusy/IsUploading are both false by now), which starts
            // the details read itself. Awaiting that seam avoids a second, redundant device read.
            Selected = Items.FirstOrDefault(i => i.Index == index);
            if (DetailsLoadTask is not null) await DetailsLoadTask;
        }
    }
}

/// <summary>One label/value row of the amp details pane.</summary>
public sealed record MetadataField(string Label, string Value);
