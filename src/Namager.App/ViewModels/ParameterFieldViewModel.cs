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
        if (!ReferenceEquals(Options, schema.Options) && _text is { Length: > 0 } t && !Options.Contains(t))
            Options = new[] { t }.Concat(Options).ToArray();

        _originalJson = ToJsonValue();
    }

    public string ToJsonValue() => Kind == "float"
        ? Number.ToString(CultureInfo.InvariantCulture)
        : JsonString.Quote(Text);
}
