using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Namager.App.ViewModels;

/// <summary>One expandable group of parameters — a top-level block (Amp, Modulation, Delay) OR a
/// folder nested inside one (Tremolo, Tone and Character, Rate), at any depth. Blocks and folders
/// were separate types (BlockSectionViewModel / SubGroupViewModel) until the Modulation block
/// arrived three levels deep: keeping two types meant the expansion, enable-toggle and activity
/// rules existed twice and could drift. There is one type now, so a folder behaves exactly like a
/// block by construction.</summary>
public sealed partial class ParameterGroupViewModel : ObservableObject
{
    public string Header { get; }

    /// <summary>The node path this group represents (<c>root\app\mod\trfolder</c>). The STABLE key
    /// for per-session expansion memory — headers are relabeled, paths are not — and the reason
    /// two groups both headed "Rate" are remembered independently.</summary>
    public string Path { get; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Fields and nested groups in ONE ordered collection, because the firmware interleaves
    /// them: `mod` publishes on_off, mode, rate(folder), dpth, mix, tcfolder, trfolder — Rate sits
    /// third, between Mode and Depth, and the editor renders it there. <see cref="Fields"/> and
    /// <see cref="Groups"/> are filtered views over this, never separate storage, so display order
    /// and logic cannot disagree.</summary>
    public ObservableCollection<object> Items { get; } = new();

    public IEnumerable<ParameterFieldViewModel> Fields => Items.OfType<ParameterFieldViewModel>();
    public IEnumerable<ParameterGroupViewModel> Groups => Items.OfType<ParameterGroupViewModel>();

    public ParameterGroupViewModel(string header, string path)
    {
        Header = header;
        Path = path;
        Items.CollectionChanged += OnItemsChanged;
    }

    public void Add(ParameterFieldViewModel field) => Items.Add(field);
    public void Add(ParameterGroupViewModel group) => Items.Add(group);

    /// <summary>Put a field at the top of this group. Used for a container node that is itself
    /// editable: its own value belongs above its children, not adrift in the parent's list.</summary>
    public void InsertFirst(ParameterFieldViewModel field) => Items.Insert(0, field);

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var f in e.NewItems?.OfType<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
            f.PropertyChanged += OnFieldValueChanged;
        foreach (var f in e.OldItems?.OfType<ParameterFieldViewModel>() ?? Enumerable.Empty<ParameterFieldViewModel>())
            f.PropertyChanged -= OnFieldValueChanged;
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(IsEqActive));
    }

    /// <summary>True for the `eq` block: show the equalizer glyph in the header. EQ is the one block
    /// with no `on_off` field (see <see cref="Enabled"/>), so that header slot is otherwise empty.</summary>
    [ObservableProperty] private bool _showEqIcon;

    /// <summary>True for the synthetic `Level` block: show the volume glyph in the header. That
    /// block has no `on_off` field either, so the same header slot is free.</summary>
    [ObservableProperty] private bool _showLevelIcon;

    /// <summary>True when any float field IN THIS GROUP sits away from its firmware default (where
    /// the schema omits one, 0). Drives the equalizer glyph's highlight so a non-flat EQ is visible
    /// without expanding the block. Deliberately does NOT recurse into nested groups: it is only
    /// consumed by the EQ and Level blocks, neither of which has any, and rolling nested state up
    /// would make the glyph mean something different per block. Delegates to the field's own
    /// <see cref="ParameterFieldViewModel.IsChangedFromDefault"/> — the same rule that highlights
    /// each reset button, so the glyph and its sliders always agree.</summary>
    public bool IsEqActive => Fields.Any(f => f.IsChangedFromDefault);

    private void OnFieldValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ParameterFieldViewModel.Number) or nameof(ParameterFieldViewModel.Text))
            OnPropertyChanged(nameof(IsEqActive));
    }

    private ParameterFieldViewModel? _enableField;

    /// <summary>The group's `on_off` field if it has one; drives <see cref="Enabled"/>.</summary>
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

    /// <summary>Adopt this group's OWN `on_off` leaf as its enable toggle. Only this group's direct
    /// fields are considered — <see cref="Fields"/> does not recurse — so Tremolo's on_off can
    /// never be mistaken for Modulation's.</summary>
    public void AttachEnableField() =>
        EnableField = Fields.FirstOrDefault(f => f.Path.EndsWith("\\on_off", StringComparison.Ordinal));

    /// <summary>True/false when the group has an on_off toggle (ON/OFF); null when it has none
    /// (e.g. eq, Tone and Character).</summary>
    public bool? Enabled => _enableField is null
        ? null
        : string.Equals(_enableField.Text, "ON", StringComparison.OrdinalIgnoreCase);

    private void OnEnableFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterFieldViewModel.Text)) OnPropertyChanged(nameof(Enabled));
    }
}
