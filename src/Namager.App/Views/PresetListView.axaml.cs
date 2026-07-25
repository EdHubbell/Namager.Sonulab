using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class PresetListView : UserControl
{
    public PresetListView()
    {
        InitializeComponent();
        UploadButton.Click += async (_, _) => await UploadAsync();
    }

    // Commit an in-place rename when the edit box loses focus (e.g. click elsewhere).
    // Guarded by IsEditing so an Escape (which clears IsEditing) won't re-commit the abandoned edit.
    private void OnEditBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PresetItemViewModel item }
            && DataContext is PresetListViewModel vm && item.IsEditing)
            vm.CommitRenameCommand.Execute(item);
    }

    private async System.Threading.Tasks.Task UploadAsync()
    {
        if (DataContext is not PresetListViewModel vm) return;
        var top = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (top is not Avalonia.Controls.Window owner) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Choose a preset file",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Preset")
                            { Patterns = new[] { "*.pst" } },
                    },
                });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;

            await vm.UploadAsync(path, () => SlotPickerDialog.ShowAsync(owner, vm.Items));
        }
        catch (System.Exception ex)
        {
            // async void handler: a throw here would kill the process.
            vm.ErrorMessage = $"Upload failed: {ex.Message}";
        }
    }
}
