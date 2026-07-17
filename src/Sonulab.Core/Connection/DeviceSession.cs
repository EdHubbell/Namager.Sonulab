using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

public sealed record SessionState(
    bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility, string? Transport = null);

public sealed class DeviceSession : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IReadOnlyList<ILinkProvider> _providers;
    private readonly CompatibilityChecker _checker;
    private ISonuLink? _link;

    public DeviceSession(IReadOnlyList<ILinkProvider> providers, CompatibilityChecker checker)
    {
        _providers = providers; _checker = checker;
    }

    public SonuClient? Client { get; private set; }

    public async Task<SessionState> ConnectAsync(CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            ISonuLink? link;
            try { link = await provider.TryConnectAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A broken transport (no network stack, no radio) must not abort the whole scan.
                Log.Info("transport {0} unavailable: {1}", provider.Name, ex.Message);
                continue;
            }
            if (link is null) continue;

            try
            {
                _link = link;
                Client = new SonuClient(link);
                var swCompat = System.Diagnostics.Stopwatch.StartNew();
                var compat = await _checker.CheckAsync(Client, ct);
                Log.Info("PERF connect compat={0}ms transport={1}", swCompat.ElapsedMilliseconds, provider.Name);
                return new SessionState(true, compat.Device, compat, provider.Name);
            }
            catch
            {
                link.Close(); _link = null; Client = null;
                throw;
            }
        }
        Client = null;
        return new SessionState(false, null, null);
    }

    public void Disconnect() { _link?.Close(); _link = null; Client = null; }
    public void Dispose() => Disconnect();
}
