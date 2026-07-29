using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class Tone3000View : UserControl
{
    private Tone3000ViewModel? _hooked;

    public Tone3000View()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rehook();
        Rehook();
    }

    /// <summary>Follow whichever view-model is currently bound. The Tone3000 VM is created once per
    /// app, but DataContext is assigned after construction, so hooking only in the constructor
    /// would miss it.</summary>
    private void Rehook()
    {
        if (_hooked is not null) _hooked.PropertyChanged -= OnVmPropertyChanged;
        _hooked = DataContext as Tone3000ViewModel;
        if (_hooked is not null) _hooked.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>Scroll the detail panel back to the top when a different tone is picked. The detail
    /// used to be an unscrolled right-hand column; now that it is a bounded, scrolling bottom panel
    /// it keeps its offset across selections, so picking a new tone would drop you into the middle
    /// of its description with the title out of view.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Tone3000ViewModel.Selected))
            DetailScroll.Offset = new Vector(0, 0);
    }
}
