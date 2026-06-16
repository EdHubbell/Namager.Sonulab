using System.Text.Json;

namespace Sonulab.Core.Model;

public sealed class NodeSchema
{
    public required string Path { get; init; }
    public required string Desc { get; init; }
    public required string Type { get; init; }
    public string? Unit { get; init; }
    public string? Ref { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Def { get; init; }
    public double? Shape { get; init; }
    public int? Dec { get; init; }
    public bool Inv { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public static NodeSchema FromRecord(NodeRecord r)
    {
        var j = r.Json;
        string? Str(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var options = new List<string>();
        if (j.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Array)
            foreach (var e in opt.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) options.Add(e.GetString()!);

        return new NodeSchema
        {
            Path = r.Path,
            Desc = Str("desc") ?? "",
            Type = Str("type") ?? "item",
            Unit = Str("unit"),
            Ref = Str("ref"),
            Min = Num("min"),
            Max = Num("max"),
            Def = Num("def"),
            Shape = Num("shape"),
            Dec = (int?)Num("dec"),
            Inv = Num("inv") is > 0,
            Options = options,
        };
    }
}
