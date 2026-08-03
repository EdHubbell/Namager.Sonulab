using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Namager.App.ViewModels;

namespace Namager.App.Views;

/// <summary>Asks which other preset this one should match in loudness. A sibling of
/// <see cref="SlotPickerDialog"/>, with one difference: the loaded preset itself is excluded, since
/// it cannot be matched to itself.</summary>
public partial class MatchPresetDialog : Window
{
    private readonly List<int> _indices = new();
    private readonly List<string> _labels = new();
    private int? _result;

    public MatchPresetDialog()
    {
        InitializeComponent();
        PresetCombo.ItemsSource = _labels;   // the XAML leaves ItemsSource null; own it here
    }

    public static async Task<int?> ShowAsync(Window owner, IReadOnlyList<PresetItemViewModel> items, int excludeIndex)
    {
        var dlg = new MatchPresetDialog();
        foreach (var i in items.Where(i => !i.IsEmpty && i.Index != excludeIndex))
        {
            dlg._indices.Add(i.Index);
            dlg._labels.Add($"{i.DisplaySlot:00}  {i.Name}");
        }
        dlg.PresetCombo.SelectedIndex = dlg._indices.Count > 0 ? 0 : -1;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        int sel = PresetCombo.SelectedIndex;
        _result = sel >= 0 && sel < _indices.Count ? _indices[sel] : null;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) { _result = null; Close(); }
}
