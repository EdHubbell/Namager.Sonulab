using Sonulab.Core.Connection;
using Sonulab.Core.Transport;

namespace Sonulab.Transport.Wifi;

/// <summary>WiFi transport provider: mDNS-discover the pedal, open a persistent TCP link,
/// verify identity via the shared LinkProbe (with retries — the first command on a fresh
/// socket can return an empty record; PROTOCOL.md §WiFi/TCP).</summary>
public sealed class WifiLinkProvider : ILinkProvider
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IMdnsQuerier _querier;
    private readonly TimeSpan _discoveryTimeout;
    private readonly Func<ITcpConn> _connFactory;
    private readonly TcpLinkOptions? _options;
    private readonly int _probeAttempts;
    private readonly int _probeRetryDelayMs;

    public WifiLinkProvider(
        IMdnsQuerier querier,
        TimeSpan discoveryTimeout,
        Func<ITcpConn>? connFactory = null,
        TcpLinkOptions? options = null,
        int probeAttempts = 3,
        int probeRetryDelayMs = 200)
    {
        _querier = querier;
        _discoveryTimeout = discoveryTimeout;
        _connFactory = connFactory ?? (() => new SystemTcpConn());
        _options = options;
        _probeAttempts = Math.Max(1, probeAttempts);
        _probeRetryDelayMs = probeRetryDelayMs;
    }

    /// <summary>Bench/diagnostic path (HwCheck --ip): pin a known endpoint, skip mDNS.</summary>
    public static WifiLinkProvider ForKnownEndpoint(
        string host, int port = 8080, Func<ITcpConn>? connFactory = null, TcpLinkOptions? options = null)
        => new(new FixedQuerier(new MdnsRecord("pinned", host, host, port, null)),
               TimeSpan.Zero, connFactory, options);

    private sealed class FixedQuerier(MdnsRecord rec) : IMdnsQuerier
    {
        public Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult<MdnsRecord?>(rec);
    }

    public string Name => "WiFi";

    public async Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default)
    {
        var rec = await _querier.DiscoverPedalAsync(_discoveryTimeout, ct);
        if (rec is null) return null;

        var link = new TcpSonuLink(_connFactory(), rec.Address, rec.Port, _options);
        try
        {
            await link.OpenAsync(ct);
            for (int attempt = 0; attempt < _probeAttempts; attempt++)
            {
                if (await LinkProbe.VerifyAsync(link, ct))
                {
                    Log.Info("PERF connect transport=WiFi endpoint={0}:{1} attempts={2}",
                        rec.Address, rec.Port, attempt + 1);
                    return link;
                }
                if (attempt + 1 < _probeAttempts) await Task.Delay(_probeRetryDelayMs, ct);
            }
        }
        catch (OperationCanceledException) { link.Close(); throw; }
        catch { /* fall through to close */ }
        link.Close();
        return null;
    }
}
