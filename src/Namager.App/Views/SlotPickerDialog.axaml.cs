using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Namager.App.ViewModels;

namespace Namager.App.Views;

/// <summary>Asks which occupied preset slot to overwrite. Only reached when every slot is full.</summary>
public partial class SlotPickerDialog : Window
{
    private readonly List<int> _indices = new();
    private readonly List<string> _labels = new();
    private int? _result;

    public SlotPickerDialog()
    {
        InitializeComponent();
        SlotCombo.ItemsSource = _labels;   // the XAML leaves ItemsSource null; own it here
    }

    public static async Task<int?> ShowAsync(Window owner, IReadOnlyList<PresetItemViewModel> items)
    {
        var dlg = new SlotPickerDialog();
        foreach (var i in items.Where(i => !i.IsEmpty))
        {
            dlg._indices.Add(i.Index);
            dlg._labels.Add($"{i.DisplaySlot:00}  {i.Name}");
        }
        dlg.SlotCombo.SelectedIndex = dlg._indices.Count > 0 ? 0 : -1;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        int sel = SlotCombo.SelectedIndex;
        _result = sel >= 0 && sel < _indices.Count ? _indices[sel] : null;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) { _result = null; Close(); }
}
