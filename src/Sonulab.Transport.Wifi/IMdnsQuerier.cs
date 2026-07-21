namespace Sonulab.Transport.Wifi;

public interface IMdnsQuerier
{
    /// <summary>Browse for the pedal; null when nothing valid answered within the timeout.
    /// Implementations must not throw for network-level failures.</summary>
    Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default);
}
