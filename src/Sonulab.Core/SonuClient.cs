using System.Globalization;
using System.Text.Json;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core;

public sealed class SonuClient
{
    private readonly ISonuLink _link;
    private readonly SemaphoreSlim _gate = new(1, 1); // one command in flight

    public SonuClient(ISonuLink link) => _link = link;

    private async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await _link.SendAsync(command, ct); }
        finally { _gate.Release(); }
    }

    public async Task<string?> ReadValueAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Read(path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path)
                return r.ValueString ?? r.ValueNumber?.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    public async Task<IReadOnlyList<string>> ReadListAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Read(path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path &&
                r.Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<NodeSchema>> BrowseAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Browse(path), ct);
        var list = new List<NodeSchema>();
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r))
                list.Add(NodeSchema.FromRecord(r));
        return list;
    }

    public Task WriteAsync(string path, string jsonValue, CancellationToken ct = default) =>
        SendAsync(SonuCommands.WriteValue(path, jsonValue), ct);

    public Task SaveAsync(string presetNodePath, string name, CancellationToken ct = default) =>
        SendAsync(SonuCommands.Save(presetNodePath, name), ct);

    public Task DWriteChunkAsync(string path, int index, int chunk, byte[] data128, CancellationToken ct = default)
    {
        var hex = Convert.ToHexStringLower(data128);
        return SendAsync(SonuCommands.DWrite(path, index, chunk, hex), ct);
    }

    public async Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default)
    {
        var bytes = new List<byte>(chunkCount * 128);
        for (int c = 1; c <= chunkCount; c++)
        {
            var raw = await SendAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, c) ?? "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
}
