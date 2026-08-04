using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;

namespace Namager.App.ViewModels;

public partial class ParameterFieldViewModel : ObservableObject
{
    public string Path { get; }
    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string Kind { get; private set; }
    public double Min { get; }
    public double Max { get; }

    /// <summary>The firmware's default for this node (`def` in the browse schema), or null when the
    /// schema omits one. Neutral-value comparisons (the EQ activity icon) use this rather than
    /// assuming 0 — a knob's neutral is not always zero.</summary>
    public double? Default { get; }

    /// <summary>The node's display unit ("dB", "ms", "%", "Hz", "deg"), or null. Used by the reset
    /// tooltip and the slider readout.</summary>
    public string? Unit { get; }

    /// <summary>Decimal places the firmware suggests for this node, or null. Used by the reset
    /// tooltip and the slider readout so a value reads the way the device would print it.</summary>
    public int? Dec { get; }

    /// <summary>Show a reset-to-default button beside this field's slider. Set for every float:
    /// fw 2.5.1 publishes `def` for all 86 of them, and 58 of those defaults are NOT zero
    /// (gate threshold -60 dB, comp release 400 ms, ir lo_cut 20 Hz), so "back to factory" is
    /// something you cannot do by hand from memory.</summary>
    [ObservableProperty] private bool _showReset;

    /// <summary>Snap back to the firmware default (0 where the schema publishes none — never the
    /// case on fw 2.5.1, kept as a guard for future firmware). For the EQ this is the same neutral
    /// the activity glyph tests against, so a reset always leaves that glyph muted.</summary>
    [RelayCommand]
    private void Reset() => Number = Default ?? 0.0;

    /// <summary>True when this float sits away from its firmware default. THE single definition of
    /// "changed from default" in the app: it highlights the reset button and, via
    /// <see cref="ParameterGroupViewModel.IsEqActive"/>, lights the equalizer glyph — so a reset can
    /// never leave one of them still claiming the value is modified.</summary>
    public bool IsChangedFromDefault =>
        Kind == "float" && Math.Abs(Number - (Default ?? 0.0)) > 1e-9;

    partial void OnNumberChanged(double value)
    {
        OnPropertyChanged(nameof(IsChangedFromDefault));
        OnPropertyChanged(nameof(Display));
    }

    /// <summary>Names the actual default, e.g. "Reset to default (400 ms)". A fixed "(0)" would be
    /// a lie on two thirds of the pedal's sliders.</summary>
    public string ResetTooltip => $"Reset to default ({Format(Default ?? 0.0)})";

    /// <summary>The current value as the device would print it — the slider readout. Shares its
    /// formatter with <see cref="ResetTooltip"/> so "18000 Hz" and "Reset to default (18000 Hz)"
    /// can never disagree about units or precision.</summary>
    public string Display => Format(Number);

    private string Format(double v)
    {
        // "0.##" rather than a fixed precision when the schema omits `dec`: shows 5 as "5", not "5.00".
        string num = Dec is int d and >= 0
            ? v.ToString("F" + d, CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
        return Unit switch
        {
            null or "" => num,
            "%" => num + "%",          // "50%", not "50 %"
            _ => $"{num} {Unit}",
        };
    }

    public IReadOnlyList<string> Options { get; private set; }

    /// <summary>The device list this field's options were fetched from (e.g. <c>root\amp</c>), or
    /// null when the options are the node's own schema enum. Only ref-sourced fields are refreshed
    /// when an amp/IR is added or deleted.</summary>
    public string? RefSource { get; }

    [ObservableProperty] private double _number;
    [ObservableProperty] private string? _text;

    private string _originalJson = "";
    public bool IsDirty => ToJsonValue() != _originalJson;
    public void MarkClean() => _originalJson = ToJsonValue();

    public ParameterFieldViewModel(NodeSchema schema, string currentValueJson,
        IReadOnlyList<string>? refOptions = null)
    {
        Path = schema.Path;
        _label = string.IsNullOrEmpty(schema.Desc) ? schema.Path : schema.Desc;
        Min = schema.Min ?? 0; Max = schema.Max ?? 1; Default = schema.Def;
        Unit = schema.Unit; Dec = schema.Dec;

        Kind = schema.Type switch
        {
            "float" => "float",
            "enum" => "enum",
            "plist" => "plist",
            "item" => "string",
            _ => "string",
        };

        // Remember the device list behind this field even if the fetch came back empty: a later
        // refresh (after the catalog changes) can still populate it.
        RefSource = Kind != "float" && schema.Options.Count == 0 && schema.Ref is { Length: > 0 } refPath
            ? refPath : null;

        // Options priority: the schema's own options; else externally fetched ref-list names
        // (amp/IR pickers — see editor-polish spec). Never for floats.
        if (schema.Options.Count > 0 || Kind == "float" || refOptions is not { Count: > 0 })
        {
            Options = schema.Options;
        }
        else
        {
            Options = refOptions;
            if (Kind == "string") Kind = "plist";           // item-typed ref field -> ComboBox template
        }

        var trimmed = currentValueJson.Trim();
        if (Kind == "float" && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            _number = n;
        else
            _text = trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2 ? trimmed[1..^1] : trimmed;

        // A ref-listed field whose current value vanished from the device list (e.g. deleted amp)
        // still shows its value: prepend it so the ComboBox can display the selection.
        if (!ReferenceEquals(Options, schema.Options))
            Options = UnionCurrentValue(Options);

        _originalJson = ToJsonValue();
    }

    /// <summary>Prepend the field's current text to <paramref name="options"/> when the device list
    /// no longer offers it, so a deleted/renamed reference still shows as the selection rather than
    /// blanking the field — a blank would read as a user edit and could be saved back as an empty
    /// reference. No-op (returns <paramref name="options"/> unchanged) when the value is already
    /// present or there is no current text. Shared by the constructor's initial load and
    /// <see cref="SetRefOptions"/>'s later refresh so the two stay in lockstep.</summary>
    private IReadOnlyList<string> UnionCurrentValue(IReadOnlyList<string> options) =>
        Text is { Length: > 0 } t && !options.Contains(t)
            ? new[] { t }.Concat(options).ToArray()
            : options;

    /// <summary>Replace the device-supplied option list in place (an amp/IR was added or deleted).
    /// The current value is unioned in when the device no longer offers it, so a deleted amp shows
    /// as the selection rather than blanking the ComboBox — a blank would read as a user edit and
    /// could be saved back as an empty reference. No-op for schema-enum fields.</summary>
    public void SetRefOptions(IReadOnlyList<string> names)
    {
        if (RefSource is null) return;
        Options = UnionCurrentValue(names);
        if (Kind == "string" && Options.Count > 0) { Kind = "plist"; OnPropertyChanged(nameof(Kind)); }
        OnPropertyChanged(nameof(Options));
    }

    public string ToJsonValue() => Kind == "float"
        ? Number.ToString(CultureInfo.InvariantCulture)
        : JsonString.Quote(Text);
}
