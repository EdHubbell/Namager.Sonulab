using Avalonia.Controls;
using Sonulab.App;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"StompStation Manager v{AppInfo.Version}";
        NavList.SelectionChanged += OnNavSelectionChanged;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NavigateRequested += i => NavList.SelectedIndex = i;
        };
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
}
