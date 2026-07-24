using System.Text;
using Sonulab.Core.Transport;

/// <summary>Serial-port double with a VIRTUAL clock, for testing paced/overlapped sends.
/// The test owns the clock and SerialSonuLink's injected delay advances it, so timing
/// assertions are exact rather than flaky. Models the three device behaviours that matter:
/// response latency, fragmented delivery, and the drop-if-sent-too-soon cliff
/// (PROTOCOL.md: commands paced under ~25 ms are eaten by the firmware).</summary>
public sealed class ScriptedSerialPort : ISerialPortStream
{
    private readonly Func<long> _now;
    private readonly List<(long At, byte[] Bytes)> _scheduled = new();
    private readonly List<byte> _cmdBuf = new();
    private readonly Queue<byte> _ready = new();
    private long _lastCommandAt = long.MinValue;

    public ScriptedSerialPort(Func<long> now) => _now = now;

    /// <summary>Maps a received command (NUL stripped) to its response text. Include the
    /// device's trailing NUL in the returned string when a terminator is wanted.</summary>
    public Func<string, string> Responder { get; set; } = _ => "";

    /// <summary>Virtual ms between a command arriving and its response starting to arrive.</summary>
    public int FirstByteLatencyMs { get; set; } = 10;

    /// <summary>Virtual ms between successive response fragments. 0 = deliver the whole
    /// response at once.</summary>
    public int FragmentIntervalMs { get; set; }

    /// <summary>Bytes per fragment when FragmentIntervalMs &gt; 0.</summary>
    public int FragmentSize { get; set; } = 64;

    /// <summary>Commands arriving less than this many virtual ms after the previous command are
    /// DROPPED — no response, ever. 0 disables.</summary>
    public int DropIfSentWithinMs { get; set; }

    /// <summary>Targeted drop: any command matching this predicate is eaten. Independent of
    /// DropIfSentWithinMs.</summary>
    public Func<string, bool>? DropWhen { get; set; }

    public List<string> Received { get; } = new();
    public List<long> ReceivedAt { get; } = new();
    public List<string> Dropped { get; } = new();
    public int DiscardCount { get; private set; }

    public bool IsOpen { get; private set; }
    public void Open(string portName, int baudRate) => IsOpen = true;
    public void Close() => IsOpen = false;

    public void DiscardInBuffer()
    {
        DiscardCount++;
        _ready.Clear();
        _scheduled.Clear();
    }

    public int BytesToRead { get { Pump(); return _ready.Count; } }

    public int Read(byte[] buffer, int offset, int count)
    {
        Pump();
        int i = 0;
        while (i < count && _ready.Count > 0) buffer[offset + i++] = _ready.Dequeue();
        return i;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = buffer[offset + i];
            if (b != 0) { _cmdBuf.Add(b); continue; }

            var cmd = Encoding.ASCII.GetString(_cmdBuf.ToArray());
            _cmdBuf.Clear();
            long at = _now();
            bool tooSoon = DropIfSentWithinMs > 0 && _lastCommandAt != long.MinValue
                           && at - _lastCommandAt < DropIfSentWithinMs;
            _lastCommandAt = at;
            if (tooSoon || DropWhen?.Invoke(cmd) == true) { Dropped.Add(cmd); continue; }

            Received.Add(cmd);
            ReceivedAt.Add(at);
            Schedule(at + FirstByteLatencyMs, Encoding.ASCII.GetBytes(Responder(cmd)));
        }
    }

    private void Schedule(long startAt, byte[] bytes)
    {
        if (bytes.Length == 0) return;
        if (FragmentIntervalMs <= 0) { _scheduled.Add((startAt, bytes)); return; }
        for (int off = 0, f = 0; off < bytes.Length; off += FragmentSize, f++)
        {
            int len = Math.Min(FragmentSize, bytes.Length - off);
            var frag = new byte[len];
            Array.Copy(bytes, off, frag, 0, len);
            _scheduled.Add((startAt + (long)f * FragmentIntervalMs, frag));
        }
    }

    /// <summary>Moves everything now due into the readable queue, oldest first. OrderBy is a
    /// stable sort, so fragments scheduled for the same instant keep their order.</summary>
    private void Pump()
    {
        long now = _now();
        var due = _scheduled.Where(s => s.At <= now).OrderBy(s => s.At).ToList();
        if (due.Count == 0) return;
        foreach (var s in due)
            foreach (var b in s.Bytes) _ready.Enqueue(b);
        _scheduled.RemoveAll(s => s.At <= now);
    }

    public void Dispose() { }
}
