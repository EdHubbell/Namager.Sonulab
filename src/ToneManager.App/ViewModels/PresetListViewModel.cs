using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace ToneManager.App.ViewModels;

public partial class PresetListViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    private readonly ReorderService _reorder;
    private readonly bool _writes;

    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed; }

    public ObservableCollection<PresetItemViewModel> Items { get; } = new();
    [ObservableProperty] private PresetItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";
    /// <summary>Last device-operation failure, shown to the user. Null when the last op succeeded.</summary>
    [ObservableProperty] private string? _errorMessage;

    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        try
        {
            await work();
            await ReloadAsync();
            return true;
        }
        catch (Exception ex)
        {
            // A device/reorder failure must NEVER crash the app. It did in the field (v0.9.1): an
            // unhandled exception out of a [RelayCommand] is rethrown by AsyncRelayCommand on the UI
            // thread and tears down the process. Surface it, resync the list, and stay alive.
            // (No CancellationToken is threaded into preset ops, so this won't swallow a genuine user
            // cancellation; the broad catch is deliberate — guaranteeing a live UI outranks it.)
            Log.Warn(ex, "preset operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            try { await ReloadAsync(); }
            catch (Exception reloadEx) { Log.Warn(reloadEx, "reload after a failed operation also failed"); }
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        var slots = await _repo.ListPresetsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new PresetItemViewModel(s, slots.Count));
    }

    [RelayCommand] private Task RefreshAsync() => ReloadAsync();

    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { IsEmpty: false, Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} up…", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { IsEmpty: false } s && s.Index < Items.Count - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} down…", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveItemUpAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= DeviceRepository.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task DuplicateAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        int dest = Items.FirstOrDefault(i => i.IsEmpty)?.Index ?? -1;
        if (dest < 0) return;
        await RunAsync($"Duplicating '{s.Name}'…", () => _repo.DuplicateAsync(s.Index, dest, s.Name + " copy"));
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s) await RunAsync($"Deleting '{s.Name}'…", () => _repo.DeleteAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;          // guard: Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        // RunAsync reloads the list (recreating items) on success; on a gated/failed write it does not,
        // so clear the edit flag ourselves in that case.
        if (!await RunAsync($"Renaming '{s.Name}'…", () => _repo.RenameAsync(s.Index, name)))
            s.IsEditing = false;
    }
}
