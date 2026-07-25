using Avalonia.Controls;
using Avalonia.Interactivity;
using Namager.App;
using Namager.App.Services;
using Namager.App.ViewModels;

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
