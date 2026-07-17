using System.Text;
using System.Text.RegularExpressions;
using Sonulab.Core.Model;
using Sonulab.Core.Transport;

/// <summary>Faithful in-memory StompStation preset model for service tests.</summary>
public class FakePresetDevice : ISonuLink
{
    private sealed class Slot { public string Name = ""; public List<string> Lines = new(); }
    private readonly Slot[] _slots = Enumerable.Range(0, 30).Select(_ => new Slot()).ToArray();
    private readonly List<string> _live = new();
    private readonly Dictionary<string, string> _scalars = new();

    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public void Close() => IsOpen = false;

    public void SeedSlot(int index, string name, IEnumerable<string> lines)
    { _slots[index].Name = name; _slots[index].Lines = lines.ToList(); }
    public void SeedScalar(string path, string jsonValue) => _scalars[path] = jsonValue;

    static readonly Regex DReadRx = new(@"^dread (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+)\}$");
    static readonly Regex DWriteRx = new(@"^dwrite (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+),""value"":""([0-9a-fA-F]*)""\}$");
    static readonly Regex SaveRx = new(@"^write root\\app\\preset:\{""value"":""([^""]*)"",""save"":""save""\}$");
    static readonly Regex SelectRx = new(@"^write root\\app\\preset:\{""value"":""([^""]*)""\}$");
    static readonly Regex WriteRx = new(@"^write (root\\app\\\S+):(\{.*\})$");
    static readonly Regex ReadRx = new(@"^read (\S+)$");

    static byte[] FromHex(string h) { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return b; }
    static byte[] PresetDocumentFrom(List<string> lines)
    {
        var text = string.Join("\r\n", lines);
        var bytes = new byte[8192];
        Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
        return bytes;
    }

    public virtual Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("not open");
        Match m;

        if ((m = DWriteRx.Match(command)).Success)
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            if (m.Groups[1].Value == @"root\presets" && chunk == -1)
            {
                var raw = FromHex(m.Groups[4].Value);
                var name = Encoding.ASCII.GetString(raw).TrimEnd('\0');
                _slots[idx].Name = name;
                if (name.Length == 0) _slots[idx].Lines = new();   // empty name = delete
            }
            // content chunks (>=1) to presets: ignored (matches firmware)
            return Task.FromResult("");
        }
        if ((m = DReadRx.Match(command)).Success)
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            var bytes = PresetDocumentFrom(_slots[idx].Lines);
            string hex = "";
            if (chunk >= 1) { var seg = bytes.Skip((chunk - 1) * 128).Take(128).ToArray(); hex = Convert.ToHexStringLower(seg); }
            return Task.FromResult($"{m.Groups[1].Value}:{{\"index\":{idx},\"chunk\":{chunk},\"value\":\"{hex}\"}}\r\n");
        }
        if ((m = SaveRx.Match(command)).Success)
        {
            var name = m.Groups[1].Value;
            var slot = _slots.FirstOrDefault(s => s.Name == name);
            if (slot != null) slot.Lines = new List<string>(_live);
            return Task.FromResult("");
        }
        if ((m = SelectRx.Match(command)).Success)
        {
            var slot = _slots.FirstOrDefault(s => s.Name == m.Groups[1].Value);
            _live.Clear(); if (slot != null) _live.AddRange(slot.Lines);
            return Task.FromResult("");
        }
        if ((m = WriteRx.Match(command)).Success)
        {
            var path = m.Groups[1].Value; var line = $"{path}:{m.Groups[2].Value}";
            int i = _live.FindIndex(l => l.StartsWith(path + ":", StringComparison.Ordinal));
            if (i >= 0) _live[i] = line; else _live.Add(line);
            return Task.FromResult("");
        }
        if ((m = ReadRx.Match(command)).Success)
        {
            var path = m.Groups[1].Value;
            if (path == @"root\presets")
            {
                var arr = string.Join(",", _slots.Select(s => "\"" + s.Name + "\""));
                return Task.FromResult($"root\\presets:{{\"value\":[{arr}]}}\r\n");
            }
            if (path == @"root\app\preset")
            {
                // current live name = name of the slot matching live content, else ""
                var match = _slots.FirstOrDefault(s => s.Lines.SequenceEqual(_live));
                return Task.FromResult($"root\\app\\preset:{{\"value\":\"{match?.Name ?? ""}\"}}\r\n");
            }
            if (_scalars.TryGetValue(path, out var v)) return Task.FromResult($"{path}:{{\"value\":{v}}}\r\n");
            return Task.FromResult("");
        }
        return Task.FromResult("");
    }
}
