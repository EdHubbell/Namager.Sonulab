using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class AmpItemViewModel : ObservableObject
{
    public int Index { get; }
    public int DisplaySlot => Index + 1;
    [ObservableProperty] private string _name;
    public bool IsEmpty => string.IsNullOrEmpty(Name);

    /// <summary>In-place rename state (display swaps a TextBlock for an edit TextBox while true).</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";

    public AmpItemViewModel(AmpSlot slot) { Index = slot.Index; _name = slot.Name; }

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
