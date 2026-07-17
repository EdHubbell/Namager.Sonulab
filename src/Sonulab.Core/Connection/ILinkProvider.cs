using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>One way of reaching the pedal (USB serial, WiFi, ...). Providers are tried in
/// order by DeviceSession; a provider returns an OPEN, identity-verified link or null.</summary>
public interface ILinkProvider
{
    /// <summary>Short transport label shown in the connection status ("USB", "WiFi").</summary>
    string Name { get; }

    Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default);
}
