using System.Text;
using System.Text.RegularExpressions;
using Sonulab.Core.Transport;

/// <summary>Faithful in-memory StompStation amp-slot model (root\amp) for service tests.
/// Semantics from PROTOCOL.md + the hardware-verified HwCheck upload path:
/// chunk 0 = staged name, chunks 1..96 = staged 12288-byte payload, chunk -1 = COMMIT
/// (non-zero name commits staged content + name; all-zeros deletes the slot and discards
/// staging). Every accepted dwrite chunk is ACKed with the NEXT expected chunk number.
/// Dread of an EMPTY slot returns no record (the real device just times out).</summary>
public class FakeAmpDevice : ISonuLink
{
    private sealed class Slot
    {
        public string? Name;                 // null/empty = empty slot
        public byte[]? Blob;                 // 12288 bytes when occupied
        public byte[] StagedName = new byte[128];
        public byte[] StagedPayload = new byte[12288];
        public int StagedChunks;             // payload chunks staged since last commit
    }

    private readonly Slot[] _slots = Enumerable.Range(0, 30).Select(_ => new Slot()).ToArray();
    private readonly List<string> _log = new();

    public IReadOnlyList<string> CommandLog => _log;
    public string?[] SlotNames => _slots.Select(s => s.Name).ToArray();
    public byte[]?[] SlotBlobs => _slots.Select(s => s.Blob).ToArray();
    public int? CorruptAckAtChunk { get; set; }
    public bool CommitSeen { get; private set; }

    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public void Close() => IsOpen = false;

    public void SeedAmp(int index, string name, byte[] blob12288)
    { _slots[index].Name = name; _slots[index].Blob = (byte[])blob12288.Clone(); }

    private static readonly Regex DWriteRx = new(@"^dwrite (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+),""value"":""([0-9a-fA-F]*)""\}$");
    private static readonly Regex DReadRx = new(@"^dread (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+)\}$");

    private static byte[] FromHex(string h)
    { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return b; }

    public virtual Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("not open");
        _log.Add(command);
        Match m;

        if ((m = DWriteRx.Match(command)).Success && m.Groups[1].Value == @"root\amp")
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            var data = FromHex(m.Groups[4].Value);
            var s = _slots[idx];
            int nextExpected;
            if (chunk == 0)
            {
                Array.Copy(data, s.StagedName, Math.Min(data.Length, 128));
                nextExpected = 1;
            }
            else if (chunk >= 1 && chunk <= 96)
            {
                Array.Copy(data, 0, s.StagedPayload, (chunk - 1) * 128, Math.Min(data.Length, 128));
                s.StagedChunks++;
                nextExpected = chunk < 96 ? chunk + 1 : -1;
            }
            else // chunk == -1: the commit / delete / rename write
            {
                var name = Encoding.ASCII.GetString(data).TrimEnd('\0');
                if (name.Length == 0)
                {
                    s.Name = null; s.Blob = null;          // delete; staged content discarded
                }
                else
                {
                    s.Name = name;
                    if (s.StagedChunks > 0) s.Blob = (byte[])s.StagedPayload.Clone();
                    CommitSeen = true;                     // rename (no staged chunks) keeps Blob
                }
                s.StagedChunks = 0;
                s.StagedPayload = new byte[12288];
                nextExpected = -1;
            }
            if (CorruptAckAtChunk == chunk) nextExpected += 7;   // wrong next -> caller must abort
            return Task.FromResult($"dwrite root\\amp:{{\"index\":{idx},\"chunk\":{nextExpected}}}\r\n");
        }
        if ((m = DReadRx.Match(command)).Success && m.Groups[1].Value == @"root\amp")
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            var s = _slots[idx];
            if (string.IsNullOrEmpty(s.Name) || s.Blob is null || chunk < 1)
                return Task.FromResult("");                // empty slot: no record (device times out)
            var seg = s.Blob.Skip((chunk - 1) * 128).Take(128).ToArray();
            return Task.FromResult($"root\\amp:{{\"index\":{idx},\"chunk\":{chunk},\"value\":\"{Convert.ToHexStringLower(seg)}\"}}\r\n");
        }
        if (command == @"read root\amp")
        {
            var arr = string.Join(",", _slots.Select(s => "\"" + (s.Name ?? "") + "\""));
            return Task.FromResult($"root\\amp:{{\"value\":[{arr}]}}\r\n");
        }
        return Task.FromResult("");
    }
}
