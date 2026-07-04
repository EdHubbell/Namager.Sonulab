using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sonulab.App.ViewModels;

public sealed partial class BlockSectionViewModel : ObservableObject
{
    public string Header { get; }
    [ObservableProperty] private bool _isExpanded;   // collapsed by default (editor-polish spec)
    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    public ObservableCollection<SubGroupViewModel> SubGroups { get; } = new();
    public BlockSectionViewModel(string header) => Header = header;

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
