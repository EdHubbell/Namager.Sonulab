using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Namager.App.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Namager.App.AppInfo.Version}";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
