using System.Text;

namespace Sonulab.Core.Model;

public sealed class PresetDocument
{
    public const int BlobSize = 8192;
    private readonly List<string> _lines;

    public IReadOnlyList<string> Lines => _lines;

    private PresetDocument(List<string> lines) => _lines = lines;

    public static PresetDocument Parse(byte[] blob)
    {
        // Content is the ASCII text before the first 0x00; the rest is zero padding.
        int end = Array.IndexOf(blob, (byte)0);
        if (end < 0) end = blob.Length;
        var text = Encoding.ASCII.GetString(blob, 0, end);
        var lines = text.Length == 0 ? new List<string>() : text.Split("\r\n").ToList();
        return new PresetDocument(lines);
    }

    public byte[] ToBytes()
    {
        var text = string.Join("\r\n", _lines);
        var content = Encoding.ASCII.GetBytes(text);
        if (content.Length > BlobSize)
            throw new InvalidOperationException($"Preset content {content.Length} exceeds {BlobSize} bytes.");
        var blob = new byte[BlobSize];            // .NET zero-initializes -> 0x00 padding
        Array.Copy(content, blob, content.Length);
        return blob;
    }

    private int IndexOf(string path)
    {
        var prefix = path + ":{";
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
        return -1;
    }

    public string? GetValueJson(string path)
    {
        int i = IndexOf(path);
        if (i < 0) return null;
        return NodeRecord.TryParse(_lines[i], out var r) && r.Json.TryGetProperty("value", out var v)
            ? v.GetRawText() : null;
    }

    public void SetValueJson(string path, string jsonValue)
    {
        int i = IndexOf(path);
        if (i < 0) throw new KeyNotFoundException(path);
        _lines[i] = $"{path}:{{\"value\":{jsonValue}}}";
    }
}
