using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class PresetListView : UserControl
{
    public PresetListView()
    {
        InitializeComponent();
        UploadButton.Click += async (_, _) => await UploadAsync();
        DeleteButton.Click += async (_, _) => await DeleteAsync();
    }

    // Commit an in-place rename when the edit box loses focus (e.g. click elsewhere).
    // Guarded by IsEditing so an Escape (which clears IsEditing) won't re-commit the abandoned edit.
    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PresetItemViewModel item }
            && DataContext is PresetListViewModel vm && item.IsEditing)
            vm.CommitRenameCommand.Execute(item);
    }

    private async Task UploadAsync()
    {
        if (DataContext is not PresetListViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await PresetUploadFlow.RunAsync(owner, vm);
    }

    /// <summary>Confirm before deleting — a preset lives only on the pedal, so an accidental
    /// click is unrecoverable. The command itself is unchanged; this only gates it.</summary>
    private async Task DeleteAsync()
    {
        if (DataContext is not PresetListViewModel vm) return;
        if (vm.Selected is not { IsEmpty: false } sel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            bool go = await ConfirmDialog.ShowAsync(owner, "Delete preset",
                $"Delete preset {sel.DisplaySlot:00} — “{sel.Name}”?\n\n" +
                "This removes it from the pedal and cannot be undone.",
                "Delete", "Cancel");
            if (go) await vm.DeleteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // async void handler: a throw here would kill the process.
            vm.ErrorMessage = $"Delete failed: {ex.Message}";
        }
    }
}
