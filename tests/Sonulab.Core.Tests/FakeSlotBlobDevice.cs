using System.Text;
using System.Text.RegularExpressions;
using Sonulab.Core.Transport;

/// <summary>Faithful in-memory slot-blob device (root\amp, root\ir, ...) for service tests.
/// Semantics per PROTOCOL.md + hardware-verified HwCheck paths: chunk 0 stages the name,
/// chunks 1..N stage the payload IN ORDER (out-of-order chunks are rejected and the ACK
/// reports the true next-expected — matches real firmware's next-expected ACK), chunk -1
/// commits (non-zero name; staged payload lands only if chunks were staged) or deletes
/// (zeros). chunk 0 / chunk -1 are accepted at any time (rename/delete are standalone).
/// Dread of an empty slot returns no record.</summary>
public class FakeSlotBlobDevice : ISonuLink
{
    private sealed class Slot
    {
        public string? Name;
        public byte[]? Blob;
        public byte[] StagedName = new byte[128];
        public byte[] StagedPayload;
        public int StagedChunks;
        public int NextExpected = 1;             // strict payload ordering
        public Slot(int slotBytes) { StagedPayload = new byte[slotBytes]; }
    }

    private readonly string _listPath;           // e.g. root\amp — used in command matching + ACKs
    private readonly int _chunks;                // payload chunk count (96 amp, 32 ir)
    private readonly int _slotBytes;             // payload size (12288 amp, 4096 ir)
    private readonly Slot[] _slots;
    private readonly List<string> _log = new();

    public FakeSlotBlobDevice(string listPath, int chunks, int slotBytes)
    {
        _listPath = listPath; _chunks = chunks; _slotBytes = slotBytes;
        _slots = Enumerable.Range(0, 30).Select(_ => new Slot(slotBytes)).ToArray();
    }

    public IReadOnlyList<string> CommandLog => _log;
    public string?[] SlotNames => _slots.Select(s => s.Name).ToArray();
    public byte[]?[] SlotBlobs => _slots.Select(s => s.Blob).ToArray();
    public int? CorruptAckAtChunk { get; set; }
    public bool CommitSeen { get; private set; }

    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public void Close() => IsOpen = false;

    public void SeedSlot(int index, string name, byte[] blob)
    { _slots[index].Name = name; _slots[index].Blob = (byte[])blob.Clone(); }

    private static readonly Regex DWriteRx = new(@"^dwrite (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+),""value"":""([0-9a-fA-F]*)""\}$");
    private static readonly Regex DReadRx = new(@"^dread (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+)\}$");

    private static byte[] FromHex(string h)
    { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); return b; }

    public virtual Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("not open");
        _log.Add(command);
        Match m;

        if ((m = DWriteRx.Match(command)).Success && m.Groups[1].Value == _listPath)
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            var data = FromHex(m.Groups[4].Value);
            var s = _slots[idx];
            int nextExpected;
            if (chunk == 0)
            {
                Array.Copy(data, s.StagedName, Math.Min(data.Length, 128));
                s.NextExpected = 1;                          // name write (re)starts the sequence
                nextExpected = 1;
            }
            else if (chunk >= 1 && chunk <= _chunks)
            {
                if (chunk == s.NextExpected)
                {
                    Array.Copy(data, 0, s.StagedPayload, (chunk - 1) * 128, Math.Min(data.Length, 128));
                    s.StagedChunks++;
                    s.NextExpected = chunk < _chunks ? chunk + 1 : -1;
                    nextExpected = s.NextExpected;
                }
                else
                {
                    nextExpected = s.NextExpected;           // rejected: not staged, ACK says the truth
                }
            }
            else // chunk == -1: commit / rename / delete — accepted any time
            {
                var name = Encoding.ASCII.GetString(data).TrimEnd('\0');
                if (name.Length == 0) { s.Name = null; s.Blob = null; }
                else
                {
                    s.Name = name;
                    if (s.StagedChunks > 0) s.Blob = (byte[])s.StagedPayload.Clone();
                    CommitSeen = true;
                }
                s.StagedChunks = 0;
                s.StagedPayload = new byte[_slotBytes];
                s.NextExpected = 1;
                nextExpected = -1;
            }
            if (CorruptAckAtChunk == chunk) nextExpected += 7;
            return Task.FromResult($"dwrite {_listPath}:{{\"index\":{idx},\"chunk\":{nextExpected}}}\r\n");
        }
        if ((m = DReadRx.Match(command)).Success && m.Groups[1].Value == _listPath)
        {
            int idx = int.Parse(m.Groups[2].Value), chunk = int.Parse(m.Groups[3].Value);
            var s = _slots[idx];
            if (string.IsNullOrEmpty(s.Name) || s.Blob is null || chunk < 1)
                return Task.FromResult("");
            var seg = s.Blob.Skip((chunk - 1) * 128).Take(128).ToArray();
            return Task.FromResult($"{_listPath}:{{\"index\":{idx},\"chunk\":{chunk},\"value\":\"{Convert.ToHexStringLower(seg)}\"}}\r\n");
        }
        if (command == "read " + _listPath)
        {
            var arr = string.Join(",", _slots.Select(s => "\"" + (s.Name ?? "") + "\""));
            return Task.FromResult($"{_listPath}:{{\"value\":[{arr}]}}\r\n");
        }
        return Task.FromResult("");
    }
}
