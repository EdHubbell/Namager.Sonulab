using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class AmpListViewModel : ObservableObject
{
    private readonly AmpService _amps;
    private readonly bool _writes;

    public AmpListViewModel(AmpService amps, bool writesAllowed)
    { _amps = amps; _writes = writesAllowed; }

    public ObservableCollection<AmpItemViewModel> Items { get; } = new();
    [ObservableProperty] private AmpItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Busy-gated write helper (mirrors PresetListViewModel.RunAsync) with an
    /// error channel: amp operations throw AmpServiceException on guarded-write failures.</summary>
    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes) return false;
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

    [RelayCommand] private Task RefreshAsync() => ReloadAsync();

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
}
