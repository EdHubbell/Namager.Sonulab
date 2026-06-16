using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class PresetItemViewModel : ObservableObject
{
    public int Index { get; }
    public int DisplaySlot => Index + 1;
    [ObservableProperty] private string _name;
    public bool IsEmpty => string.IsNullOrEmpty(Name);

    /// <summary>True when this slot holds a preset and is not the first slot.</summary>
    public bool CanMoveUp { get; }
    /// <summary>True when this slot holds a preset and is not the last slot.</summary>
    public bool CanMoveDown { get; }

    /// <summary>In-place rename state (display swaps a TextBlock for an edit TextBox while true).</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";

    public PresetItemViewModel(PresetSlot slot, int slotCount)
    {
        Index = slot.Index; _name = slot.Name;
        bool occupied = !string.IsNullOrEmpty(slot.Name);
        CanMoveUp = occupied && Index > 0;
        CanMoveDown = occupied && Index < slotCount - 1;
    }

    /// <summary>Enter in-place edit mode (no-op on an empty slot). The actual rename is committed by the list VM.</summary>
    [RelayCommand] private void BeginRename()
    {
        if (IsEmpty) return;
        EditName = Name;
        IsEditing = true;
    }

    /// <summary>Leave edit mode without renaming.</summary>
    [RelayCommand] private void CancelRename() => IsEditing = false;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
