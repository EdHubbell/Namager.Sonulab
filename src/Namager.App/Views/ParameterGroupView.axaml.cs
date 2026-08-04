using System;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class ParameterGroupView : UserControl
{
    public ParameterGroupView() => InitializeComponent();

    /// <summary>The Level block's match-volume button. This control's DataContext is the GROUP, so
    /// the editor view model is reached through the ancestor view — the same object the button's
    /// IsEnabled binding walks to.</summary>
    private async void OnMatchVolumeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<ParameterEditorView>()?.DataContext is not ParameterEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var presets = (owner.DataContext as MainWindowViewModel)?.Presets;
        if (presets is null) return;
        // async void event handler: nothing may escape to the UI thread. MatchVolumeAsync
        // already catches its own failures; this guards the picker itself.
        try { await vm.MatchVolumeAsync(() => MatchPresetDialog.ShowAsync(owner, presets.Items, vm.LoadedIndex)); }
        catch (Exception ex) { vm.ErrorMessage = $"Match failed: {ex.Message}"; }
    }
}
