using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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
        Min = schema.Min ?? 0; Max = schema.Max ?? 1;

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
