using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class IrListViewModel : ObservableObject
{
    private readonly IrService _irs;
    private readonly bool _writes;

    /// <summary>.wav -> device blob seam — Sonulab.Distill.WavToIr.Convert in the app,
    /// a fake in tests. Conversion is instant and synchronous (no cancel/progress needed).</summary>
    private readonly Func<string, byte[]> _convertWav;
    private string _uploadSourcePath = "";

    public IrListViewModel(IrService irs, bool writesAllowed, Func<string, byte[]>? convertWav = null)
    {
        _irs = irs; _writes = writesAllowed;
        _convertWav = convertWav ?? Sonulab.Distill.WavToIr.Convert;
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
    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        try { await work(); await ReloadAsync(); return true; }
        catch (IrServiceException ex) { ErrorMessage = ex.Message; return false; }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        var slots = await _irs.ListIrsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new IrItemViewModel(s));
    }

    [RelayCommand] private Task RefreshAsync() => CanRefresh ? ReloadAsync() : Task.CompletedTask;

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", () => _irs.DeleteIrAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(IrItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;      // Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", () => _irs.RenameIrAsync(s.Index, name)))
            s.IsEditing = false;                            // gated/failed write: leave edit mode ourselves
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

    [RelayCommand] private async Task StartUploadAsync()
    {
        if (!_writes || IsUploading || IsBusy || SelectedEmptySlot is not int slot) return;
        var name = UploadName.Trim();
        if (name.Length == 0) { UploadError = "Enter an IR name."; return; }
        if (Items.Any(i => !i.IsEmpty && string.Equals(i.Name, name, StringComparison.Ordinal)))
        { UploadError = $"An IR named '{name}' already exists — names must be unique."; return; }

        UploadError = null;
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

            UploadStatus = $"Done — '{name}' in slot {slot + 1}";
            await ReloadAsync();
            Selected = Items.FirstOrDefault(i => i.Index == slot);
        }
        catch (InvalidDataException ex) { UploadError = ex.Message; }
        catch (IrServiceException ex) { UploadError = ex.Message; }
        catch (IOException ex) { UploadError = ex.Message; }
        catch (UnauthorizedAccessException ex) { UploadError = ex.Message; }
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
