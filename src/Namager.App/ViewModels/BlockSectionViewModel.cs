using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Namager.App.ViewModels;

public sealed partial class BlockSectionViewModel : ObservableObject
{
    public string Header { get; }
    [ObservableProperty] private bool _isExpanded;   // collapsed by default (editor-polish spec)
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    public ObservableCollection<SubGroupViewModel> SubGroups { get; } = new();

    public BlockSectionViewModel(string header)
    {
        Header = header;
        Fields.CollectionChanged += (_, e) =>
        {
            foreach (var f in e.NewItems?.Cast<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
                f.PropertyChanged += OnFieldValueChanged;
            foreach (var f in e.OldItems?.Cast<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
                f.PropertyChanged -= OnFieldValueChanged;
            OnPropertyChanged(nameof(IsEqActive));
        };
    }

    /// <summary>True for the `eq` block: show the equalizer glyph in the header. EQ is the one block
    /// with no `on_off` field (see <see cref="Enabled"/>), so that header slot is otherwise empty.</summary>
    [ObservableProperty] private bool _showEqIcon;

    /// <summary>True when any float field in this block sits away from its firmware default (where
    /// the schema omits one, 0). Drives the equalizer glyph's highlight so a non-flat EQ is visible
    /// without expanding the block. Delegates to the field's own
    /// <see cref="ParameterFieldViewModel.IsChangedFromDefault"/> — the same rule that highlights
    /// each reset button, so the block glyph and its sliders always agree.</summary>
    public bool IsEqActive => Fields.Any(f => f.IsChangedFromDefault);

    private void OnFieldValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterFieldViewModel.Number) or nameof(ParameterFieldViewModel.Text))
            OnPropertyChanged(nameof(IsEqActive));
    }

    private ParameterFieldViewModel? _enableField;

    /// <summary>The block's `on_off` field if it has one; drives <see cref="Enabled"/>.</summary>
    public ParameterFieldViewModel? EnableField
    {
        get => _enableField;
        set
        {
            if (_enableField is not null) _enableField.PropertyChanged -= OnEnableFieldChanged;
            _enableField = value;
            if (_enableField is not null) _enableField.PropertyChanged += OnEnableFieldChanged;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    /// <summary>True/false when the block has an on_off toggle (ON/OFF); null when it has none (e.g. eq).</summary>
    public bool? Enabled => _enableField is null
        ? null
        : string.Equals(_enableField.Text, "ON", StringComparison.OrdinalIgnoreCase);

    private void OnEnableFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterFieldViewModel.Text)) OnPropertyChanged(nameof(Enabled));
    }
}
