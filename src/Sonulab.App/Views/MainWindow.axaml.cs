using Avalonia.Controls;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        NavList.SelectionChanged += OnNavSelectionChanged;
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        PresetsPage.IsVisible = NavList.SelectedIndex == 0;
        AmpsPage.IsVisible    = NavList.SelectedIndex == 1;
        IRsPage.IsVisible     = NavList.SelectedIndex == 2;

        if (DataContext is MainWindowViewModel vm)
        {
            vm.CurrentNavIndex = NavList.SelectedIndex;
            vm.EnsureTabLoaded(NavList.SelectedIndex);
        }
    }
}
