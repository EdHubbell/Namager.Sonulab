using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core;

public sealed class SonuClient
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly ISonuLink _link;
    private readonly SemaphoreSlim _gate = new(1, 1); // one command in flight

    public SonuClient(ISonuLink link) => _link = link;

    private async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try { return await _link.SendAsync(command, ct); }
        finally
        {
            sw.Stop();
            _gate.Release();
            // Per-command device timing — Trace level (off by default; ~130 lines per reorder).
            // Raise the file target to Trace in Logging.cs to capture it for perf diagnosis.
            // The command head (verb + path) identifies it; long dwrite hex payloads are truncated.
            if (Log.IsTraceEnabled)
                Log.Trace("cmd {0,5}ms  {1}", sw.ElapsedMilliseconds,
                    command.Length > 70 ? command[..70] + "…" : command);
        }
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

    public async Task<IReadOnlyList<NodeRecord>> BrowseRecordsAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Browse(path), ct);
        var list = new List<NodeRecord>();
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r))
                list.Add(r);
        return list;
    }

    public async Task<IReadOnlyList<NodeSchema>> BrowseAsync(string path, CancellationToken ct = default) =>
        (await BrowseRecordsAsync(path, ct)).Select(NodeSchema.FromRecord).ToList();

    public Task WriteAsync(string path, string jsonValue, CancellationToken ct = default) =>
        SendAsync(SonuCommands.WriteValue(path, jsonValue), ct);

    public Task SaveAsync(string presetNodePath, string name, CancellationToken ct = default) =>
        SendAsync(SonuCommands.Save(presetNodePath, name), ct);

    /// <summary>Writes one 128-byte blob chunk. Returns the raw response window so callers can
    /// inspect the device's per-chunk ACK record (<c>dwrite &lt;path&gt;:{"index":N,"chunk":&lt;next&gt;}</c>).</summary>
    public Task<string> DWriteChunkAsync(string path, int index, int chunk, byte[] data128, CancellationToken ct = default)
    {
        var hex = Convert.ToHexStringLower(data128);
        return SendAsync(SonuCommands.DWrite(path, index, chunk, hex), ct);
    }

    /// <summary>Sends a raw protocol command and returns the raw response window (diagnostics).</summary>
    public Task<string> SendRawAsync(string command, CancellationToken ct = default) => SendAsync(command, ct);

    public async Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default)
    {
        var bytes = new List<byte>(chunkCount * 128);
        for (int c = 1; c <= chunkCount; c++)
        {
            var raw = await SendAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, index, c) ?? "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
}
