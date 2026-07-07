using System.Text.RegularExpressions;

namespace Sonulab.Core.Protocol;

public static class ResponseParser
{
    public static IEnumerable<string> Records(string raw) =>
        RemoveSpacesOutsideQuotes(raw).Split('\n')
           .Select(l => l.TrimEnd('\r').Replace("\0", ""))
           .Where(l => l.Length > 0);

    private static string RemoveSpacesOutsideQuotes(string s)
    {
        var result = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') inQuotes = !inQuotes;
            if (c != ' ' || inQuotes)
                result.Append(c);
        }
        return result.ToString();
    }

    public static bool IsMeter(string record) =>
        record.Contains(@"root\sys\_meters\") || record.Contains(@"root\usb\_status");

    public static IEnumerable<string> NonMeterRecords(string raw) =>
        Records(raw).Where(r => !IsMeter(r));

    public static string? ChunkHex(string raw, int chunk)
    {
        var pattern = "\"chunk\":" + chunk + @"\b.*?""value"":""([0-9a-fA-F]*)""";
        foreach (var rec in Records(raw))
        {
            var m = Regex.Match(rec, pattern);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Index-checked variant: a stale response for the SAME chunk number of a
    /// DIFFERENT slot (orphaned in the pipeline by a cancelled read) must not be accepted
    /// as this slot's data (slot-26 incident hardening, 2026-07-06).</summary>
    public static string? ChunkHex(string raw, int index, int chunk)
    {
        var pattern = "\"index\":" + index + @"\b.*?""chunk"":" + chunk + @"\b.*?""value"":""([0-9a-fA-F]*)""";
        foreach (var rec in Records(raw))
        {
            var m = Regex.Match(rec, pattern);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
}
