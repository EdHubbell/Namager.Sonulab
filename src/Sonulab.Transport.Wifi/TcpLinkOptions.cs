namespace Sonulab.Transport.Wifi;

/// <summary>Response-collection policy — same semantics and defaults as SerialLinkOptions'
/// read loop (identical wire protocol; PROTOCOL.md §WiFi/TCP), plus a connect timeout.</summary>
public sealed class TcpLinkOptions
{
    public int PollMs { get; init; } = 10;
    /// <summary>Retained for API/serial parity but NOT consulted by <see cref="TcpSonuLink"/>: over TCP
    /// a large response can arrive in bursts with gaps longer than this, so treating an idle gap as
    /// end-of-response truncated it (trailing preset slots read as empty). The device's NUL terminator
    /// is authoritative; <see cref="MaxWaitMs"/> is the backstop.</summary>
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 6000;
    /// <summary>Deliberately generous (serial uses 300): the pedal ACKs every command but can answer
    /// late (~300ms+) while doing flash work (rename dwrite, save copy). Serial never notices — the
    /// continuous meter stream keeps its read loop alive — but TCP has no meter stream, so silence
    /// tripped a 300ms timeout, abandoned the response, and desynced the stream off-by-one (live wire
    /// capture 2026-07-21). This timeout only needs to catch a DEAD peer; slow is normal.</summary>
    public int FirstByteTimeoutMs { get; init; } = 2000;
    public int ConnectTimeoutMs { get; init; } = 3000;
    /// <summary>When a late response is still owed at the next send, how long the pipe must stay
    /// silent before we stop waiting for it pre-send (the read loop's stale-skip covers stragglers).</summary>
    public int ResyncQuietMs { get; init; } = 250;
}
