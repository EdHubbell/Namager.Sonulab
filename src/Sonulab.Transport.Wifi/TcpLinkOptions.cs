namespace Sonulab.Transport.Wifi;

/// <summary>Response-collection policy — same semantics and defaults as SerialLinkOptions'
/// read loop (identical wire protocol; PROTOCOL.md §WiFi/TCP), plus a connect timeout.</summary>
public sealed class TcpLinkOptions
{
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 2500;
    public int FirstByteTimeoutMs { get; init; } = 300;
    public int ConnectTimeoutMs { get; init; } = 3000;
}
