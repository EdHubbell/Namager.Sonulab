using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;
using Sonulab.Distill;

namespace Sonulab.App.ViewModels;

public partial class AmpListViewModel : ObservableObject
{
    private readonly AmpService _amps;
    private readonly bool _writes;

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
        DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null)
    {
        _amps = amps; _writes = writesAllowed;
        _distill = distill ?? Sonulab.Distill.Distiller.DistillAsync;
        _distilledDir = distilledDir ?? Path.Combine("NAMFiles", "Distilled");
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
    }

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
    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        try { await work(); await ReloadAsync(); return true; }
        catch (AmpServiceException ex) { ErrorMessage = ex.Message; return false; }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        _detailsCache.Clear();
        var slots = await _amps.ListAmpsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new AmpItemViewModel(s));
    }

    [RelayCommand] private Task RefreshAsync() => CanRefresh ? ReloadAsync() : Task.CompletedTask;

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
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

        IsUploadPanelOpen = true;
    }

    [RelayCommand] private async Task StartUploadAsync()
    {
        if (!_writes || IsUploading || IsBusy || SelectedEmptySlot is not int slot) return;
        var name = UploadName.Trim();
        if (name.Length == 0) { UploadError = "Enter an amp name."; return; }
        if (Items.Any(i => !i.IsEmpty && string.Equals(i.Name, name, StringComparison.Ordinal)))
        { UploadError = $"An amp named '{name}' already exists — names must be unique."; return; }

        UploadError = null;
        IsUploading = true;
        _uploadCts = new CancellationTokenSource();
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
            DetailsLoadTask = LoadDetailsCoreAsync(Selected);
            await DetailsLoadTask;
        }
        catch (OperationCanceledException) { UploadError = "Cancelled."; }
        catch (Sonulab.Distill.DistillException ex) { UploadError = ex.Message; }
        catch (AmpServiceException ex) { UploadError = ex.Message; }
        catch (IOException ex) { UploadError = ex.Message; }
        catch (UnauthorizedAccessException ex) { UploadError = ex.Message; }
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
    [ObservableProperty] private bool _isDetailsVisible;
    [ObservableProperty] private bool _isDetailsLoading;
    [ObservableProperty] private bool _showNoMetadata;
    [ObservableProperty] private string? _detailsNotes;
    [ObservableProperty] private string? _detailsUrl;
    [ObservableProperty] private string? _detailsError;
    public ObservableCollection<MetadataField> DetailsFields { get; } = new();

    /// <summary>Last details load — test seam: set Selected, then await this.</summary>
    public Task? DetailsLoadTask { get; private set; }

    private readonly Dictionary<int, (string Name, byte[] Slot, AmpMetadata? Meta)> _detailsCache = new();
    private CancellationTokenSource? _detailsCts;

    partial void OnSelectedChanged(AmpItemViewModel? value)
    {
        // Never issue a read while another device operation may be in flight — serial
        // commands must not interleave. The pane just stays hidden; explicit callers
        // (post-upload, post-save) use LoadDetailsCoreAsync directly once idle.
        if (IsBusy || IsUploading) { IsDetailsVisible = false; return; }
        DetailsLoadTask = LoadDetailsCoreAsync(value);
    }

    private async Task LoadDetailsCoreAsync(AmpItemViewModel? item)
    {
        _detailsCts?.Cancel();
        DetailsFields.Clear();
        DetailsNotes = null; DetailsUrl = null; DetailsError = null; ShowNoMetadata = false;
        if (item is null || item.IsEmpty) { IsDetailsVisible = false; return; }
        IsDetailsVisible = true;

        if (!_detailsCache.TryGetValue(item.Index, out var entry) || entry.Name != item.Name)
        {
            var cts = new CancellationTokenSource();
            _detailsCts = cts;
            IsDetailsLoading = true;
            try
            {
                var slot = await _amps.ReadAmpAsync(item.Index, cts.Token);
                entry = (item.Name, slot, VxampMetadata.TryRead(slot));
                _detailsCache[item.Index] = entry;
            }
            catch (OperationCanceledException) { return; }   // superseded by a newer selection
            catch (AmpServiceException ex) { DetailsError = ex.Message; return; }
            finally { if (_detailsCts == cts) IsDetailsLoading = false; }
            if (cts.IsCancellationRequested || Selected != item) return;
        }
        PopulateDetails(entry.Meta);
    }

    private void PopulateDetails(AmpMetadata? meta)
    {
        DetailsFields.Clear();
        if (meta is null) { ShowNoMetadata = true; return; }
        ShowNoMetadata = false;
        if (meta.Source?.File is { } f) DetailsFields.Add(new("Source file", f));
        if (meta.Source?.Size is { } sz) DetailsFields.Add(new("Source size", FormatSize(sz)));
        if (meta.Source?.Modified is { } mo) DetailsFields.Add(new("Source date", mo));
        if (meta.Uploaded is { } up) DetailsFields.Add(new("Uploaded", up));
        if (meta.Nam is { } nam)
            foreach (var kv in nam)
                if (kv.Value is System.Text.Json.Nodes.JsonValue v)
                    DetailsFields.Add(new($"NAM {kv.Key}", v.ToString()));
        if (meta.Distill?.Version is { } dv) DetailsFields.Add(new("Distilled by", $"v{dv}"));
        if (meta.Distill?.ShapeErr is { } se) DetailsFields.Add(new("Fit error", $"{se:F3} (lower is better)"));
        DetailsNotes = meta.Notes;
        DetailsUrl = meta.Url;
    }

    private static string FormatSize(long b) =>
        b >= 1 << 20 ? $"{b / 1048576.0:F1} MB" : b >= 1 << 10 ? $"{b / 1024.0:F1} KB" : $"{b} B";

    [RelayCommand]
    private void OpenDetailsUrl()
    {
        if (DetailsUrl is { Length: > 0 } url &&
            (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>One label/value row of the amp details pane.</summary>
public sealed record MetadataField(string Label, string Value);
