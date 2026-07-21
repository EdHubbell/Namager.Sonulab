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
    private readonly int _readRetryAttempts;
    private readonly int _readRetryDelayMs;

    /// <param name="readRetryAttempts">How many times a read (ReadValue/ReadList/BrowseRecords) is
    /// attempted while the response lacks the record the caller expects. The WiFi/TCP pedal
    /// intermittently returns an empty record ("\r\n\0") or a late PREVIOUS response instead of the
    /// real one; reads are idempotent so retrying is safe, and serial answers correctly on attempt 1
    /// so this never triggers there. CAVEAT: reading a node that legitimately does not exist now
    /// costs the full retry budget before returning null/empty — probe optional nodes sparingly.</param>
    public SonuClient(ISonuLink link, int readRetryAttempts = 4, int readRetryDelayMs = 120)
    {
        _link = link;
        _readRetryAttempts = Math.Max(1, readRetryAttempts);
        _readRetryDelayMs = readRetryDelayMs;
    }

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

    /// <summary>Sends a read command, retrying while the response does not contain the record the
    /// caller expects. Two WiFi quirks make this necessary (see the ctor's <c>readRetryAttempts</c>):
    /// the pedal intermittently answers with an empty record ("\r\n\0"), and a late response to a
    /// PREVIOUS command can arrive in its place (e.g. a rename ACK — a record for the right path with
    /// the wrong shape), so "any parseable record" is not proof the answer arrived. Returns the last
    /// raw response (unexpected/empty if the budget is exhausted). Only for idempotent reads;
    /// writes/dwrites do NOT go through here.</summary>
    private async Task<string> SendReadAsync(string command, Func<string, bool> hasExpected, CancellationToken ct)
    {
        string raw = "";
        for (int attempt = 1; attempt <= _readRetryAttempts; attempt++)
        {
            raw = await SendAsync(command, ct);
            if (hasExpected(raw)) return raw;
            if (attempt < _readRetryAttempts)
            {
                Log.Debug("no expected record for '{0}' (attempt {1}/{2}) — retrying (WiFi empty/late-response quirk)",
                    command, attempt, _readRetryAttempts);
                await Task.Delay(_readRetryDelayMs, ct);
            }
        }
        return raw;
    }

    public async Task<string?> ReadValueAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendReadAsync(SonuCommands.Read(path),
            r => ResponseParser.NonMeterRecords(r).Any(rec => NodeRecord.TryParse(rec, out var nr) && nr.Path == path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path)
                return r.ValueString ?? r.ValueNumber?.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    public async Task<IReadOnlyList<string>> ReadListAsync(string path, CancellationToken ct = default)
    {
        static bool IsList(NodeRecord r) =>
            r.Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array;
        var raw = await SendReadAsync(SonuCommands.Read(path),
            r => ResponseParser.NonMeterRecords(r).Any(rec => NodeRecord.TryParse(rec, out var nr) && nr.Path == path && IsList(nr)), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path && IsList(r))
                return r.Json.GetProperty("value").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<NodeRecord>> BrowseRecordsAsync(string path, CancellationToken ct = default)
    {
        // Browse answers with many records of unpredictable paths — "at least one parseable record"
        // is the strongest safe expectation here.
        var raw = await SendReadAsync(SonuCommands.Browse(path),
            r => ResponseParser.NonMeterRecords(r).Any(rec => NodeRecord.TryParse(rec, out _)), ct);
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

    public Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default) =>
        DReadChunkRangeAsync(path, index, 1, chunkCount, ct);

    /// <summary>Dread chunks [firstChunk .. firstChunk+count-1] (1-based). PERMISSIVE like
    /// DReadBlobAsync: a missing/torn chunk contributes 0 bytes, shortening the result —
    /// callers that need integrity use SlotBlobService's validated wrappers.</summary>
    public async Task<byte[]> DReadChunkRangeAsync(string path, int index, int firstChunk, int count, CancellationToken ct = default)
    {
        var bytes = new List<byte>(count * 128);
        for (int c = firstChunk; c < firstChunk + count; c++)
        {
            var raw = await SendAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, index, c) ?? "";
            // A torn record can carry an odd-length hex value; Convert.FromHexString would
            // throw FormatException past every caller. Treat it as a missing chunk instead —
            // the resulting short buffer fails loudly at the validated-read layer.
            if ((hex.Length & 1) == 1) hex = "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
}
