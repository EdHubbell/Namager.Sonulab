using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class PresetItemViewModel : ObservableObject
{
    public int Index { get; }
    public int DisplaySlot => Index + 1;
    [ObservableProperty] private string _name;
    public bool IsEmpty => string.IsNullOrEmpty(Name);

    public PresetItemViewModel(PresetSlot slot) { Index = slot.Index; _name = slot.Name; }
}
