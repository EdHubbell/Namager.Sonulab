using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Namager.App.ViewModels;

namespace Namager.App.Views;

/// <summary>The "restore a single preset from a .pst on the PC" flow: pick a file, then hand it to
/// <see cref="PresetListViewModel.UploadAsync"/>, prompting for a slot only if the pedal is full.
///
/// Shared because there are two entry points — the up-arrow above the preset list and
/// File ▸ Restore Preset… — and they must behave identically. Living in one place is what keeps
/// them from drifting.</summary>
internal static class PresetUploadFlow
{
    /// <summary>Never throws: this is called from UI event handlers, where an escaping exception
    /// takes the process down. Failures surface on the view model's error channel.</summary>
    public static async Task RunAsync(Window owner, PresetListViewModel vm)
    {
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a preset file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Preset") { Patterns = new[] { "*.pst" } },
                },
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;

            await vm.UploadAsync(path, () => SlotPickerDialog.ShowAsync(owner, vm.Items));
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"Upload failed: {ex.Message}";
        }
    }
}
