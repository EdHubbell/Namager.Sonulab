using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Namager.App.Views;

/// <summary>Consent dialog for File ▸ Restore Snapshot… — the device-write gate for the app's
/// most destructive operation. Adds a safety-backup checkbox to the plain yes/no shape of
/// <see cref="ConfirmDialog"/> since restore additionally needs the caller to know whether to
/// capture a pre-restore snapshot first.</summary>
public partial class RestoreConfirmDialog : Window
{
    private bool _confirmed;

    public RestoreConfirmDialog() => InitializeComponent();

    /// <summary>Returns (confirmed, backupFirst). The message is pre-formatted by the caller.</summary>
    public static async Task<(bool Confirmed, bool BackupFirst)> ShowAsync(Window owner, string message)
    {
        var dlg = new RestoreConfirmDialog();
        dlg.MessageText.Text = message;
        await dlg.ShowDialog(owner);
        return (dlg._confirmed, dlg.BackupCheck.IsChecked == true);
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e) { _confirmed = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
