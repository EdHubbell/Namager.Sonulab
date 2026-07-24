namespace Sonulab.Core.Transport;

public interface ISonuLink
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    void Close();
    Task<string> SendAsync(string command, CancellationToken ct = default); // command WITHOUT trailing NUL

    /// <summary>Sends <paramref name="commands"/> with overlapped timing and returns the raw
    /// response windows collected, split at the device's NUL terminators, in arrival order.
    ///
    /// RESPONSE-PRODUCING COMMANDS ONLY (dread). A silent command (write/dwrite) emits no NUL
    /// and would shift every later window.
    ///
    /// The result is NOT positionally aligned with <paramref name="commands"/>: an unsolicited
    /// record or a dropped response shifts it, and the list may be SHORTER than the input when
    /// the deadline hits. Callers MUST identify each response by its own content — for dread,
    /// ResponseParser.ChunkHex(raw, index, chunk) verifies both fields — and never by position.
    ///
    /// The default implementation is a plain lockstep loop, so links with no pipelining support
    /// (TCP, fakes) are correct with no code of their own.</summary>
    async Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        var windows = new List<string>(commands.Count);
        foreach (var c in commands) windows.Add(await SendAsync(c, ct));
        return windows;
    }
}
