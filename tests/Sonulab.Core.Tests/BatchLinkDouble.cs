using System.Text.RegularExpressions;
using Sonulab.Core.Transport;

/// <summary>ISonuLink double that answers dreads from an in-memory blob and lets a test distort
/// the BATCH window list — omit a response, tear a value, or shift every position with an
/// unsolicited record — while the lockstep SendAsync repair path answers correctly. This is how
/// we prove the client identifies responses by content and never by position.</summary>
public sealed class BatchLinkDouble : ISonuLink
{
    private readonly byte[] _blob;
    private readonly int _index;

    public BatchLinkDouble(byte[] blob, int index) { _blob = blob; _index = index; }

    public List<string> BatchCommands { get; } = new();
    public List<string> LockstepCommands { get; } = new();

    /// <summary>Number of SendBatchAsync invocations — distinguishes ONE batch of N commands
    /// from N separate one-command batches, which the flat BatchCommands list cannot.</summary>
    public int BatchCalls { get; private set; }

    /// <summary>Chunks the BATCH omits entirely (the firmware ate the command). The lockstep
    /// repair still recovers them.</summary>
    public HashSet<int> DropInBatch { get; } = new();

    /// <summary>Chunks whose batch response carries an odd-length (torn) hex value.</summary>
    public HashSet<int> TearInBatch { get; } = new();

    /// <summary>Chunks that never answer on ANY lane — batch or repair.</summary>
    public HashSet<int> DropEverywhere { get; } = new();

    /// <summary>Prepend an unsolicited record, shifting every batch window position by one.</summary>
    public bool InjectExtraWindow { get; set; }

    public bool IsOpen => true;
    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Close() { }

    public Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        LockstepCommands.Add(command);
        int chunk = ChunkOf(command);
        return Task.FromResult(DropEverywhere.Contains(chunk) ? "" : Window(chunk, tear: false));
    }

    public Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        BatchCalls++;
        BatchCommands.AddRange(commands);
        var windows = new List<string>();
        if (InjectExtraWindow) windows.Add("root\\sys\\_meters\\out:{\"value\":-42}\r\n");
        foreach (var c in commands)
        {
            int chunk = ChunkOf(c);
            if (DropInBatch.Contains(chunk) || DropEverywhere.Contains(chunk)) continue;
            windows.Add(Window(chunk, TearInBatch.Contains(chunk)));
        }
        return Task.FromResult<IReadOnlyList<string>>(windows);
    }

    private static int ChunkOf(string command) =>
        int.Parse(Regex.Match(command, @"""chunk"":(-?\d+)").Groups[1].Value);

    private string Window(int chunk, bool tear)
    {
        var hex = Convert.ToHexStringLower(_blob, (chunk - 1) * 128, 128);
        if (tear) hex = hex[..^1];              // odd length = torn record
        return $"root\\presets:{{\"index\":{_index},\"chunk\":{chunk},\"value\":\"{hex}\"}}\r\n";
    }

    /// <summary>Deterministic 128-byte-per-chunk test blob: chunk C is filled with byte C.</summary>
    public static byte[] MakeBlob(int chunks)
    {
        var blob = new byte[chunks * 128];
        for (int c = 1; c <= chunks; c++)
            for (int i = 0; i < 128; i++) blob[(c - 1) * 128 + i] = (byte)c;
        return blob;
    }
}
