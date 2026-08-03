using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Namager.App.ViewModels;

namespace Namager.App.Views;

public partial class ParameterEditorView : UserControl
{
    public ParameterEditorView()
    {
        InitializeComponent();
        DownloadButton.Click += async (_, _) => await DownloadAsync();
    }

    private async void OnMatchVolumeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.ParameterEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var presets = (owner.DataContext as ViewModels.MainWindowViewModel)?.Presets;
        if (presets is null) return;
        // async void event handler: nothing may escape to the UI thread. MatchVolumeAsync
        // already catches its own failures; this guards the picker itself.
        try { await vm.MatchVolumeAsync(() => MatchPresetDialog.ShowAsync(owner, presets.Items, vm.LoadedIndex)); }
        catch (Exception ex) { vm.ErrorMessage = $"Match failed: {ex.Message}"; }
    }

    /// <summary>Read the pedal's copy of the loaded preset and write it wherever the user says.
    /// The VM owns the device read and the error reporting; the view owns the dialogs.</summary>
    private async Task DownloadAsync()
    {
        if (DataContext is not ParameterEditorViewModel vm || !vm.CanDownload) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        try
        {
            if (vm.IsDirty && top is Window owner)
            {
                bool go = await ConfirmDialog.ShowAsync(owner, "Unsaved changes",
                    "The file will contain the preset as it is saved on the pedal, not your unsaved edits.\n\n" +
                    "Download the pedal's version?", "Download", "Cancel");
                if (!go) return;
            }

            var bytes = await vm.ReadLoadedPresetBytesAsync();
            if (bytes is null) return;                      // VM already reported the failure

            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save preset",
                SuggestedFileName = vm.SuggestedFileName,
                DefaultExtension = "pst",
                FileTypeChoices = new[] { new FilePickerFileType("Preset") { Patterns = new[] { "*.pst" } } },
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            await System.IO.File.WriteAllBytesAsync(path, bytes);
            vm.ReportDownloaded(path);
        }
        catch (Exception ex)
        {
            // A throw out of an async void event handler is process death — surface it instead.
            vm.ErrorMessage = $"Download failed: {ex.Message}";
        }
    }
}
