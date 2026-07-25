using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Namager.App.ViewModels;

public partial class PresetListViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    private readonly ReorderService _reorder;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;
    private readonly Namager.App.Services.IPresetUsageService _usage;

    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed,
                               Namager.App.Services.IStatusService? status = null,
                               Namager.App.Services.IPresetUsageService? usage = null)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed;
      _status = status ?? Namager.App.Services.NullStatusService.Instance;
      _usage = usage ?? Namager.App.Services.NullPresetUsageService.Instance; }

    public ObservableCollection<PresetItemViewModel> Items { get; } = new();
    [ObservableProperty] private PresetItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyMessage = "";
    /// <summary>Last device-operation failure, shown to the user. Null when the last op succeeded.</summary>
    [ObservableProperty] private string? _errorMessage;

    private async Task<bool> RunAsync(string message, string success, Func<Task> work,
                                      Action? onSuccessMapUpdate = null)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try
        {
            await work();
            if (onSuccessMapUpdate is not null) onSuccessMapUpdate();   // targeted map maintenance
            else _usage.Invalidate();                                   // default: full rescan
            await ReloadAsync();
            _status.Success(success);
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
            _usage.Invalidate();   // device state uncertain after a failed mutation → force rescan
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
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

    [RelayCommand] private async Task RefreshAsync()
    {
        // NOT RunAsync: refresh must work in read-only mode (no _writes gate) — but it needs the
        // same crash-guard: a dead link mid-session must surface, not tear down the app.
        IsBusy = true; BusyMessage = "Reading presets…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading presets…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "preset refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }

    [RelayCommand] private async Task MoveItemUpAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index <= 0) return;
        int dest = s.Index - 1;
        if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up",
                () => _reorder.MoveStepAsync(s.Index, up: true),
                () => _usage.NotifyPresetMoved(s.Index, dest)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task MoveItemDownAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEmpty: false } s || s.Index >= DeviceRepository.SlotCount - 1) return;
        int dest = s.Index + 1;
        if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down",
                () => _reorder.MoveStepAsync(s.Index, up: false),
                () => _usage.NotifyPresetMoved(s.Index, dest)) && dest < Items.Count)
            Selected = Items[dest];
    }

    [RelayCommand] private async Task DuplicateAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        int dest = Items.FirstOrDefault(i => i.IsEmpty)?.Index ?? -1;
        if (dest < 0) return;
        await RunAsync($"Duplicating '{s.Name}'…", $"Duplicated '{s.Name}'", () => _repo.DuplicateAsync(s.Index, dest, s.Name + " copy"));
    }

    /// <summary>Upload a .pst from disk. The VIEW picks <paramref name="path"/> and supplies
    /// <paramref name="chooseSlot"/>, which is invoked ONLY when every slot is occupied and returns
    /// the 0-based slot to overwrite (null cancels). Not a [RelayCommand] because it takes two
    /// arguments; the view calls it directly.</summary>
    public async Task UploadAsync(string path, Func<Task<int?>> chooseSlot)
    {
        if (!_writes) return;
        ErrorMessage = null;

        PresetDocument doc;
        try
        {
            doc = PresetDocument.Parse(await File.ReadAllBytesAsync(path));
        }
        catch (Exception ex)
        {
            // Parse/read failures happen BEFORE any device contact — nothing to roll back.
            Log.Warn(ex, "preset upload could not read '{0}'", path);
            ErrorMessage = $"Couldn't read that preset file: {ex.Message}";
            _status.Failure("Upload failed — unreadable file.");
            return;
        }
        if (doc.Lines.Count == 0 || doc.Lines.All(string.IsNullOrWhiteSpace))
        {
            ErrorMessage = "That .pst file has no preset data in it.";
            _status.Failure("Upload failed — empty file.");
            return;
        }

        var desired = Namager.App.Services.PresetFileNaming.NameFromFile(path);
        if (desired.Length == 0) desired = "Preset";
        var name = Namager.App.Services.PresetFileNaming.ResolveUnique(desired, Items.Select(i => i.Name));

        int slot = Items.FirstOrDefault(i => i.IsEmpty)?.Index ?? -1;
        if (slot < 0)
        {
            if (await chooseSlot() is not { } chosen) return;      // user cancelled: no write
            slot = chosen;
        }

        int target = slot;
        if (await RunAsync($"Uploading '{name}'…", $"Uploaded '{name}' to slot {target + 1}",
                () => _repo.WritePresetToSlotAsync(target, name, doc, verify: true),
                () => _usage.NotifyPresetContentWritten(target, name, doc))
            && !string.Equals(name, desired, StringComparison.Ordinal))
        {
            // Overrides RunAsync's success line so the rename is the thing the user actually reads.
            _status.Success($"Uploaded as '{name}' — a preset named '{desired}' already exists.");
        }
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _repo.DeleteAsync(s.Index),
                () => _usage.NotifyPresetDeleted(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;          // guard: Escape-then-LostFocus won't re-commit
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        // RunAsync reloads the list (recreating items) on success; on a gated/failed write it does not,
        // so clear the edit flag ourselves in that case.
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _repo.RenameAsync(s.Index, name),
                () => _usage.NotifyPresetRenamed(s.Index, name)))
            s.IsEditing = false;
    }
}
