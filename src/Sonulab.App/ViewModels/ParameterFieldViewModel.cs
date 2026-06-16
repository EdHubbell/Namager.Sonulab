using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class ParameterFieldViewModel : ObservableObject
{
    public string Path { get; }
    public string Label { get; set; }
    public string Kind { get; }
    public double Min { get; }
    public double Max { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private double _number;
    [ObservableProperty] private string? _text;

    private string _originalJson = "";
    public bool IsDirty => ToJsonValue() != _originalJson;
    public void MarkClean() => _originalJson = ToJsonValue();

    public ParameterFieldViewModel(NodeSchema schema, string currentValueJson)
    {
        Path = schema.Path;
        Label = string.IsNullOrEmpty(schema.Desc) ? schema.Path : schema.Desc;
        Options = schema.Options;
        Min = schema.Min ?? 0; Max = schema.Max ?? 1;

        Kind = schema.Type switch
        {
            "float" => "float",
            "enum" => "enum",
            "plist" => "plist",
            "item" => "string",
            _ => "string",
        };

        var trimmed = currentValueJson.Trim();
        if (Kind == "float" && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            _number = n;
        else
            _text = trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2 ? trimmed[1..^1] : trimmed;

        _originalJson = ToJsonValue();
    }

    public string ToJsonValue() => Kind == "float"
        ? Number.ToString(CultureInfo.InvariantCulture)
        : "\"" + (Text ?? "") + "\"";
}
