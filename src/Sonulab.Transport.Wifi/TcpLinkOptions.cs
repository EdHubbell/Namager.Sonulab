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
    public int MaxWaitMs { get; init; } = 2500;
    public int FirstByteTimeoutMs { get; init; } = 300;
    public int ConnectTimeoutMs { get; init; } = 3000;
}
