using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Namager.App;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Namager.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"NAMager for Sonulab v{AppInfo.Version}";
        NavList.SelectionChanged += OnNavSelectionChanged;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NavigateRequested += i => NavList.SelectedIndex = i;
        };

        // Update check runs after the window shows so it can never delay startup.
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                _ = vm.CheckForUpdatesAsync(new UpdateCheckService());
        };

        ExitMenuItem.Click += (_, _) =>
        {
            try { Close(); }
            catch { /* handler must not escape onto the UI thread */ }
        };
        AboutMenuItem.Click += async (_, _) =>
        {
            try { await new AboutDialog().ShowDialog(this); }
            catch { /* async void-style handler: a throw here would kill the process */ }
        };
        BackupMenuItem.Click += async (_, _) => await BackupAsync();
        RestoreMenuItem.Click += async (_, _) => await RestoreAsync();
        RestorePresetMenuItem.Click += async (_, _) => await RestoreSinglePresetAsync();
        ExportSnapshotMenuItem.Click += async (_, _) => await ExportSnapshotFlowAsync();
        RestoreSnapshotMenuItem.Click += async (_, _) => await RestoreSnapshotFlowAsync();
    }

    /// <summary>The single .namsnap file type offered by both the save and open pickers below.</summary>
    private static readonly FilePickerFileType NamsnapFileType = new("NAMager Snapshot")
    {
        Patterns = new[] { "*.namsnap" },
    };

    /// <summary>File ▸ Restore Preset… — the menu twin of the up-arrow above the preset list.
    /// Both run <see cref="PresetUploadFlow"/> so they cannot drift apart.</summary>
    private async System.Threading.Tasks.Task RestoreSinglePresetAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.Presets is not { } presets)
        {
            vm.Status.Failure("Connect to the pedal first.");
            return;
        }
        await PresetUploadFlow.RunAsync(this, presets);
    }

    /// <summary>File ▸ Export Snapshot… — picks a .namsnap destination and hands it to
    /// MainWindowViewModel.ExportSnapshotAsync, which never throws (failures land on the status
    /// bar) and writes atomically (temp file + rename), so a cancelled or failed export never
    /// touches a pre-existing file at the chosen path.</summary>
    private async System.Threading.Tasks.Task ExportSnapshotFlowAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var proceed = await ConfirmDialog.ShowAsync(this, "Export Snapshot",
                "Exports all the presets, amps and IR files so you can restore them to this pedal " +
                "or another pedal at a later date.\n\nReading the pedal takes about 3 minutes.",
                confirmText: "Continue", cancelText: "Cancel");
            if (!proceed) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Snapshot",
                SuggestedFileName = $"StompStation {System.DateTime.Now:yyyy-MM-dd HHmmss}.namsnap",
                DefaultExtension = "namsnap",
                FileTypeChoices = new[] { NamsnapFileType },
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            await vm.ExportSnapshotAsync(path);
        }
        catch (System.Exception ex)
        {
            // async void-style handler: never let this escape onto the UI thread.
            vm.Status.Failure($"Export failed: {ex.Message}");
        }
    }

    /// <summary>File ▸ Restore Snapshot… — pick a .namsnap, plan it against the connected pedal,
    /// get explicit consent (with the safety-backup checkbox), then execute with a cancelable
    /// progress dialog. Restore is the app's most destructive operation: the consent dialog is
    /// the device-write gate, and every overwritten/cleared slot is archived first (safety
    /// snapshot and/or per-slot -prerestore files).</summary>
    private async System.Threading.Tasks.Task RestoreSnapshotFlowAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Restore Snapshot",
                AllowMultiple = false,
                FileTypeFilter = new[] { NamsnapFileType },
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path) return;

            var plan = await vm.PlanRestoreAsync(path);
            var m = plan.Manifest;
            var mismatch = vm.Connection.FirmwareVersion is { } fw && fw != m.Device.Fw
                ? $"\n\nNOTE: the snapshot was taken on firmware {m.Device.Fw}; this pedal runs {fw}."
                : "";
            var (confirmed, backupFirst) = await RestoreConfirmDialog.ShowAsync(this,
                $"Snapshot of a {m.Device.Model} (firmware {m.Device.Fw}), captured {m.CreatedUtc}.\n\n" +
                $"Restoring will make this pedal EXACTLY match the snapshot: " +
                $"{plan.WriteCount} file{(plan.WriteCount == 1 ? "" : "s")} will be written and " +
                $"{plan.ClearCount} slot{(plan.ClearCount == 1 ? "" : "s")} not in the snapshot will be cleared. " +
                $"This takes roughly {(plan.WriteCount * 8 + 59) / 60 + 1} minutes; slots already " +
                "identical to the snapshot are skipped, so re-running after an interruption is fast." +
                mismatch);
            if (!confirmed) return;

            try
            {
                (RestoreResult Result, string? SafetyPath) outcome = default;
                await RestoreProgressDialog.RunWithCountsAsync(this, async (progress, ct) =>
                {
                    outcome = await vm.ExecuteRestoreAsync(plan, backupFirst,
                        new Progress<SnapshotRestoreProgress>(p => progress.Report(
                            (MainWindowViewModel.FormatRestoreProgress(p), p.Done, p.Total))),
                        ct);
                });
                var (result, safetyPath) = outcome;
                await ConfirmDialog.ShowAsync(this, "Restore complete",
                    $"{result.Written} file{(result.Written == 1 ? "" : "s")} written, " +
                    $"{result.SkippedIdentical} already identical, {result.Cleared} cleared." +
                    (safetyPath is null ? "" : $"\n\nSafety backup: {safetyPath}"),
                    confirmText: null, cancelText: "Close");
            }
            catch (OperationCanceledException)
            {
                await ConfirmDialog.ShowAsync(this, "Restore canceled",
                    "Restore stopped between files — every file already written was verified. " +
                    "Run Restore Snapshot again with the same file to finish; already-restored " +
                    "files are skipped automatically.",
                    confirmText: null, cancelText: "Close");
            }
        }
        catch (SnapshotArchiveException ex)
        {
            vm.Status.Failure($"Restore failed: {ex.Message}");
            await ConfirmDialog.ShowAsync(this, "Restore failed",
                $"This file isn't a usable .namsnap snapshot:\n\n{ex.Message}",
                confirmText: null, cancelText: "Close");
        }
        catch (System.Exception ex)
        {
            vm.Status.Failure($"Restore failed: {ex.Message}");
            await ConfirmDialog.ShowAsync(this, "Restore failed",
                $"{ex.Message}\n\nEvery file already written was verified. Run Restore Snapshot " +
                "again with the same file to resume; already-restored files are skipped.",
                confirmText: null, cancelText: "Close");
        }
    }

    private async System.Threading.Tasks.Task BackupAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            if (await vm.BackupPresetsAsync() is not { } result) return;
            bool open = await ConfirmDialog.ShowAsync(this, "Backup complete",
                $"Backed up {result.Count} preset{(result.Count == 1 ? "" : "s")} to:\n\n{result.Folder}",
                "Open Folder", "Close");
            if (open)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(result.Folder) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            // async void handler: never let this escape.
            vm.Status.Failure($"Backup failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task RestoreAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose a backup folder", AllowMultiple = false });
            if (folders.Count != 1 || folders[0].TryGetLocalPath() is not { } dir) return;

            var plan = RejectWrongSizedFiles(
                PresetFileNaming.PlanRestore(System.IO.Directory.GetFiles(dir, "*.pst")));

            if (plan.Items.Count == 0)
            {
                // Skipped files are named even here: a user who hand-curated filenames and got
                // every one wrong deserves "here's why", not "your folder is empty" — the two look
                // identical without this (plan.Skipped was silently dropped before this fix).
                var nothingMessage = $"No files named \"NN - Name.pst\" were found in:\n\n{dir}"
                    + (plan.Skipped.Count > 0
                        ? $"\n\nSkipped: {string.Join(", ", plan.Skipped)}"
                        : "");
                await ConfirmDialog.ShowAsync(this, "Nothing to restore", nothingMessage, confirmText: null, cancelText: "Close");
                return;
            }

            var slots = string.Join(", ", plan.Items.Select(i => (i.Index + 1).ToString("00")));
            var message =
                $"{plan.Items.Count} preset{(plan.Items.Count == 1 ? "" : "s")} will be written to slot{(plan.Items.Count == 1 ? "" : "s")} {slots}.\n\n" +
                "Those slots will be overwritten. Every other slot is left untouched.\n\n" +
                $"This takes about {plan.Items.Count * 10} seconds." +
                // "Skipped" alone, not "no NN - slot number": PlanRestore also routes a file with a
                // VALID but DUPLICATE slot number here (first file for a slot wins), so a reason
                // tied to "no slot number" would be wrong for that case.
                (plan.Skipped.Count > 0
                    ? $"\n\nSkipped: {string.Join(", ", plan.Skipped)}"
                    : "");

            if (!await ConfirmDialog.ShowAsync(this, "Restore presets", message, "Restore", "Cancel"))
                return;

            await RestoreProgressDialog.RunAsync(this, plan,
                (progress, ct) => vm.RestorePresetsAsync(plan, progress, ct));
        }
        catch (System.Exception ex)
        {
            // async void-style handler: never let this escape onto the UI thread.
            vm.Status.Failure($"Restore failed: {ex.Message}");
        }
    }

    /// <summary>Move any planned item whose file is not exactly <see cref="PresetDocument.BlobSize"/>
    /// bytes from <see cref="RestorePlan.Items"/> into <see cref="RestorePlan.Skipped"/>. A
    /// truncated (or otherwise malformed-length) .pst still parses — PresetDocument.Parse just
    /// reads to the first NUL — and DeviceRepository's read-back verify would then compare the
    /// write against that SAME short document and pass, so this must run before the file ever
    /// reaches a device write.
    ///
    /// Deliberately NOT inside PresetFileNaming.PlanRestore: that method is pure (file-NAME
    /// inspection only, no I/O) by design, and stays that way. This method already has file-system
    /// access — it runs immediately after the Directory.GetFiles call that built the raw plan — so
    /// the length check belongs here, where the plan is consumed, not inside the pure planner.</summary>
    private static RestorePlan RejectWrongSizedFiles(RestorePlan plan)
    {
        var items = new List<RestoreItem>();
        var skipped = new List<string>(plan.Skipped);
        foreach (var item in plan.Items)
        {
            if (new System.IO.FileInfo(item.Path).Length == PresetDocument.BlobSize)
                items.Add(item);
            else
                skipped.Add(System.IO.Path.GetFileName(item.Path));
        }
        return new RestorePlan(items, skipped);
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        PresetsPage.IsVisible  = NavList.SelectedIndex == 0;
        AmpsPage.IsVisible     = NavList.SelectedIndex == 1;
        IRsPage.IsVisible      = NavList.SelectedIndex == 2;
        Tone3000Page.IsVisible = NavList.SelectedIndex == 4;   // 3 = the disabled section header

        if (DataContext is MainWindowViewModel vm)
        {
            vm.CurrentNavIndex = NavList.SelectedIndex;
            vm.EnsureTabLoaded(NavList.SelectedIndex);
        }
    }

    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { UpdateAvailable: { } update })
            _ = Launcher.LaunchUriAsync(new Uri(update.Url));
    }

    private async void OnFeedbackClick(object? sender, RoutedEventArgs e)
    {
        var vm = new FeedbackViewModel(
            new FeedbackService(),
            AppInfo.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        await new FeedbackDialog { DataContext = vm }.ShowDialog(this);
    }
}
