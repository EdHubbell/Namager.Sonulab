using System.Text.RegularExpressions;

namespace Sonulab.Core.Transport;

public sealed class FakeSonuLink : ISonuLink
{
    private readonly Dictionary<string, string> _scalars = new();          // path -> json value (e.g. "\"ON\"")
    private readonly Dictionary<string, string[]> _lists = new();          // path -> 30 names
    private readonly Dictionary<(string, int, int), string> _chunks = new(); // (path,index,chunk) -> hex

    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public void Close() => IsOpen = false;

    public void SeedScalar(string path, string jsonValue) => _scalars[path] = jsonValue;
    public void SeedList(string path, string[] names) => _lists[path] = names;

    private static readonly Regex ReadRx = new(@"^read (.+)$");
    private static readonly Regex WriteRx = new(@"^write (\S+):(\{.*\})$");
    private static readonly Regex DReadRx = new(@"^dread (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+)\}$");
    private static readonly Regex DWriteRx = new(@"^dwrite (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+),""value"":""([0-9a-fA-F]*)""\}$");

    public Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("link not open");

        Match m;
        if ((m = DWriteRx.Match(command)).Success)
        {
            _chunks[(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))] = m.Groups[4].Value;
            return Task.FromResult("");
        }
        if ((m = DReadRx.Match(command)).Success)
        {
            var key = (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var hex = _chunks.TryGetValue(key, out var h) ? h : "";
            return Task.FromResult($"{key.Item1}:{{\"index\":{key.Item2},\"chunk\":{key.Item3},\"value\":\"{hex}\"}}\r\n");
        }
        if ((m = WriteRx.Match(command)).Success)
        {
            // Minimal: capture {"value":X} into the scalar store.
            var vm = Regex.Match(m.Groups[2].Value, @"^\{""value"":(.*?)(,|\})");
            if (vm.Success) _scalars[m.Groups[1].Value] = vm.Groups[1].Value;
            return Task.FromResult("");
        }
        if ((m = ReadRx.Match(command)).Success)
        {
            var path = m.Groups[1].Value;
            if (_lists.TryGetValue(path, out var names))
            {
                var arr = string.Join(",", names.Select(n => "\"" + n + "\""));
                return Task.FromResult($"{path}:{{\"value\":[{arr}]}}\r\n");
            }
            if (_scalars.TryGetValue(path, out var v))
                return Task.FromResult($"{path}:{{\"value\":{v}}}\r\n");
            return Task.FromResult("");
        }
        return Task.FromResult("");
    }
}
