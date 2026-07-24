using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Model;
using Sonulab.Core.Services;      // PresetRef
using Namager.App.Services;       // PresetRefFormat

namespace Namager.App.ViewModels;

public partial class IrItemViewModel : ObservableObject
{
    public int Index { get; }
    public int DisplaySlot => Index + 1;
    [ObservableProperty] private string _name;
    public bool IsEmpty => string.IsNullOrEmpty(Name);

    /// <summary>In-place rename state (display swaps a TextBlock for an edit TextBox while true).</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";

    /// <summary>Presets that reference this amp (set by the list VM after a usage scan). Empty = unused.</summary>
    [ObservableProperty] private IReadOnlyList<PresetRef> _usedInPresets = System.Array.Empty<PresetRef>();
    public bool IsUsed => UsedInPresets.Count > 0;
    public string? UsedInTooltip => IsUsed ? "Used in: " + PresetRefFormat.Join(UsedInPresets) : null;
    partial void OnUsedInPresetsChanged(IReadOnlyList<PresetRef> value)
    { OnPropertyChanged(nameof(IsUsed)); OnPropertyChanged(nameof(UsedInTooltip)); }

    public IrItemViewModel(SlotEntry slot) { Index = slot.Index; _name = slot.Name; }

    /// <summary>Enter in-place edit mode (no-op on an empty slot). The rename is committed by the list VM.</summary>
    [RelayCommand] private void BeginRename()
    {
        if (IsEmpty) return;
        EditName = Name;
        IsEditing = true;
    }

    [RelayCommand] private void CancelRename() => IsEditing = false;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
