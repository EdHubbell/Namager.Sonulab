using CommunityToolkit.Mvvm.ComponentModel;
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

    public PresetItemViewModel(PresetSlot slot, int slotCount)
    {
        Index = slot.Index; _name = slot.Name;
        bool occupied = !string.IsNullOrEmpty(slot.Name);
        CanMoveUp = occupied && Index > 0;
        CanMoveDown = occupied && Index < slotCount - 1;
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
}
