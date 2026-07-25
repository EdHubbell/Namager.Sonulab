using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Namager.App.Views;

/// <summary>Small modal yes/no. Both button captions are caller-supplied so the same dialog serves
/// "Download anyway / Cancel", "Overwrite / Cancel" and "Open Folder / Close".</summary>
public partial class ConfirmDialog : Window
{
    private bool _result;

    public ConfirmDialog() => InitializeComponent();

    /// <summary>Pass <paramref name="confirmText"/> null for a single-button, dismiss-only dialog
    /// (e.g. an informational "Close") — this collapses the confirm button instead of showing two
    /// buttons that would both read "Close". The return value is meaningless in that mode; callers
    /// use it purely to await the dialog closing.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
                                             string? confirmText = "OK", string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog { Title = title };
        dlg.MessageText.Text = message;
        dlg.ConfirmButton.IsVisible = confirmText is not null;
        if (confirmText is not null) dlg.ConfirmButton.Content = confirmText;
        dlg.CancelButton.Content = cancelText;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) { _result = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { _result = false; Close(); }
}
