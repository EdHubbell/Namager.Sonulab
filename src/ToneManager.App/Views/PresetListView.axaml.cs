using Avalonia.Controls;
using Avalonia.Interactivity;
using ToneManager.App.ViewModels;

namespace ToneManager.App.Views;

public partial class PresetListView : UserControl
{
    public PresetListView() => InitializeComponent();

    // Commit an in-place rename when the edit box loses focus (e.g. click elsewhere).
    // Guarded by IsEditing so an Escape (which clears IsEditing) won't re-commit the abandoned edit.
    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PresetItemViewModel item }
            && DataContext is PresetListViewModel vm && item.IsEditing)
            vm.CommitRenameCommand.Execute(item);
    }
}
