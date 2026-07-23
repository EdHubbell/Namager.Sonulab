using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Namager.App.Services;

public sealed class LabelService
{
    private readonly IReadOnlyDictionary<string, string> _map;
    public LabelService(IReadOnlyDictionary<string, string> map) => _map = map;

    public string Label(string path, string? deviceDesc)
    {
        if (_map.TryGetValue(path, out var mapped) && mapped.Length > 0) return mapped;
        if (!string.IsNullOrEmpty(deviceDesc)) return deviceDesc!;
        return Prettify(LastSegment(path));
    }

    private static string LastSegment(string path)
    {
        int i = path.LastIndexOf('\\');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string Prettify(string segment)
    {
        var sb = new StringBuilder();
        foreach (var word in segment.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1) sb.Append(word[1..]);
        }
        return sb.Length == 0 ? segment : sb.ToString();
    }

    private static readonly Lazy<LabelService> _default = new(() =>
    {
        var asm = typeof(LabelService).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("labels.en.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("labels.en.json not embedded — check Namager.App.csproj.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd()) ?? new();
        return new LabelService(map);
    });
    public static LabelService Default => _default.Value;
}
