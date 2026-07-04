using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class AmpListViewModel : ObservableObject
{
    private readonly AmpService _amps;
    private readonly bool _writes;

    /// <summary>Distillation seam — Sonulab.Distill.Distiller.DistillAsync in the app,
    /// a fake in tests.</summary>
    public delegate Task DistillRunner(string namPath, string outPath,
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
                await _distill(_uploadSourcePath, vxampPath, distillProgress, _uploadCts.Token);
            }

            CanCancelUpload = false;                        // device writes begin: no cancelling now
            IsUploadIndeterminate = false;
            var bytes = await File.ReadAllBytesAsync(vxampPath);
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
}
