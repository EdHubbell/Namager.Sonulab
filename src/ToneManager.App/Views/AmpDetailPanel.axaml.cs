using Avalonia.Controls;

namespace ToneManager.App.Views;

/// <summary>Right-hand panel of the Amps tab. Pure view: inherits AmpListViewModel as
/// DataContext and cycles upload form / details / placeholder off existing bindings.</summary>
public partial class AmpDetailPanel : UserControl
{
    public AmpDetailPanel() => InitializeComponent();
}
