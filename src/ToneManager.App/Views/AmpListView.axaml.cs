using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ToneManager.App.ViewModels;

namespace ToneManager.App.Views;

public partial class AmpListView : UserControl
{
    public AmpListView()
    {
        InitializeComponent();
        UploadNamButton.Click += async (_, _) => await PickAndBeginAsync("NAM model", "*.nam");
        UploadVxampButton.Click += async (_, _) => await PickAndBeginAsync("vxamp blob", "*.vxamp");
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
