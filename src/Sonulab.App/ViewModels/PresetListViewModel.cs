using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class PresetListViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly ReorderService _reorder;
    private readonly bool _writes;

    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed; }

    public ObservableCollection<PresetItemViewModel> Items { get; } = new();
    [ObservableProperty] private PresetItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";

    private async Task<bool> RunAsync(string message, Func<Task> work)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message;
        try { await work(); await ReloadAsync(); return true; }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    private async Task ReloadAsync()
    {
        var slots = await _repo.ListPresetsAsync();
        Items.Clear();
        foreach (var s in slots) Items.Add(new PresetItemViewModel(s));
    }

    [RelayCommand] private Task RefreshAsync() => ReloadAsync();

    [RelayCommand] private async Task MoveUpAsync()
    {
        if (Selected is { Index: > 0 } s)
        {
            int dest = s.Index - 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} up…", () => _reorder.MoveAsync(s.Index, dest)) && dest < Items.Count)
                Selected = Items[dest];
        }
    }

    [RelayCommand] private async Task MoveDownAsync()
    {
        if (Selected is { } s && s.Index < Items.Count - 1)
        {
            int dest = s.Index + 1;
            if (await RunAsync($"Moving slot {s.DisplaySlot} down…", () => _reorder.MoveAsync(s.Index, dest)) && dest < Items.Count)
                Selected = Items[dest];
        }
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

    [RelayCommand] private async Task RenameAsync(string? newName)
    {
        if (Selected is { } s && !string.IsNullOrWhiteSpace(newName)) await RunAsync($"Renaming…", () => _repo.RenameAsync(s.Index, newName!));
    }
}
