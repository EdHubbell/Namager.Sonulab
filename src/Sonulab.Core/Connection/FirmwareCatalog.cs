using System.Reflection;
using System.Text.Json;

namespace Sonulab.Core.Connection;

public static class FirmwareCatalog
{
    private sealed record Entry(string license, string arch, string version);

    public static IReadOnlyList<TestedFirmware> Load(string json)
    {
        var entries = JsonSerializer.Deserialize<List<Entry>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return entries.Select(e => new TestedFirmware(e.license, e.arch, e.version)).ToList();
    }

    private static readonly Lazy<IReadOnlyList<TestedFirmware>> _default = new(() =>
    {
        var asm = typeof(FirmwareCatalog).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("compatibility.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Load(r.ReadToEnd());
    });

    public static IReadOnlyList<TestedFirmware> Default => _default.Value;
}
