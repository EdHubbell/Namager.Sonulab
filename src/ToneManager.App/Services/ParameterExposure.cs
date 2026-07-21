using System.Reflection;
using System.Text.Json;

namespace ToneManager.App.Services;

public sealed class ParameterExposure
{
    private readonly IReadOnlyList<string> _hidden;
    public ParameterExposure(IReadOnlyList<string> hidden) => _hidden = hidden;

    public bool IsHidden(string path)
    {
        foreach (var entry in _hidden)
        {
            if (entry.StartsWith("*", StringComparison.Ordinal))
            {
                if (path.EndsWith(entry[1..], StringComparison.Ordinal)) return true;
            }
            else if (path == entry || path.StartsWith(entry + "\\", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly Lazy<ParameterExposure> _default = new(() =>
    {
        var asm = typeof(ParameterExposure).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("hidden-params.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("hidden-params.json not embedded — check ToneManager.App.csproj.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        var list = JsonSerializer.Deserialize<List<string>>(r.ReadToEnd()) ?? new();
        return new ParameterExposure(list);
    });
    public static ParameterExposure Default => _default.Value;
}
