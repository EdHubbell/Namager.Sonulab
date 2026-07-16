using Avalonia.Controls;
using Avalonia.Interactivity;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class FeedbackDialog : Window
{
    public FeedbackDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FeedbackViewModel vm)
                vm.CloseRequested += Close;
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
