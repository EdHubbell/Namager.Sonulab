using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class AmpListView : UserControl
{
    public AmpListView()
    {
        InitializeComponent();
        UploadNamButton.Click += async (_, _) => await PickAndBeginAsync("NAM model", "*.nam");
        UploadVxampButton.Click += async (_, _) => await PickAndBeginAsync("vxamp blob", "*.vxamp");
        DeleteButton.Click += async (_, _) => await DeleteAsync();
    }

    /// <summary>Confirm before deleting. The slot is archived first, so this is recoverable —
    /// the dialog says where, because a user who can't find the copy will treat it as gone.</summary>
    private async System.Threading.Tasks.Task DeleteAsync()
    {
        if (DataContext is not AmpListViewModel vm) return;
        if (vm.Selected is not { IsEmpty: false } sel) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            bool go = await ConfirmDialog.ShowAsync(owner, "Delete amp",
                $"Delete amp {sel.Index + 1:00} — “{sel.Name}”?\n\n" +
                $"A copy is saved to {Namager.App.Services.AppPaths.DeletedSlotBackups} first.",
                "Delete", "Cancel");
            if (go) await vm.DeleteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // async void handler: a throw here would kill the process.
            vm.ErrorMessage = $"Delete failed: {ex.Message}";
        }
    }

    private async System.Threading.Tasks.Task PickAndBeginAsync(string label, string pattern)
    {
        if (DataContext is not AmpListViewModel vm) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Choose a {label}",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(label) { Patterns = new[] { pattern } } },
            });
            if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
                vm.BeginUploadCommand.Execute(path);
        }
        catch (Exception ex)
        {
            vm.UploadError = ex.Message;
        }
    }

    // Commit an in-place rename when the edit box loses focus (same guard as PresetListView).
    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: AmpItemViewModel item }
            && DataContext is AmpListViewModel vm && item.IsEditing)
            vm.CommitRenameCommand.Execute(item);
    }
}
