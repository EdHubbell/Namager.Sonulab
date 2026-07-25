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

    public static async Task<bool> ShowAsync(Window owner, string title, string message,
                                             string confirmText = "OK", string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog { Title = title };
        dlg.MessageText.Text = message;
        dlg.ConfirmButton.Content = confirmText;
        dlg.CancelButton.Content = cancelText;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) { _result = true; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { _result = false; Close(); }
}
