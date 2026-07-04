namespace Sonulab.Core.Protocol;

/// <summary>Minimal JSON string quoting for device commands. Escapes backslash and
/// double-quote only — device names are validated printable ASCII and enum tokens
/// carry no control characters, so full JSON escaping is intentionally out of scope.</summary>
public static class JsonString
{
    public static string Quote(string? s) =>
        "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
