using System.Threading.Tasks;
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
                vm.CloseRequested += async () =>
                {
                    await Task.Delay(900);   // let "Thanks — feedback sent!" register before closing
                    Close();
                };
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
