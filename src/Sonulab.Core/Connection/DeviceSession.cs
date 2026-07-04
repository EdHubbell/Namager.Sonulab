namespace Sonulab.Core.Connection;

public sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility);

public sealed class DeviceSession : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly SonuConnector _connector;
    private readonly CompatibilityChecker _checker;
    private Sonulab.Core.Transport.SerialSonuLink? _link;

    public DeviceSession(SonuConnector connector, CompatibilityChecker checker)
    {
        _connector = connector; _checker = checker;
    }

    public SonuClient? Client { get; private set; }

    public async Task<SessionState> ConnectAsync(
        IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default)
    {
        var link = await _connector.ConnectAsync(ports, bauds, ct);
        if (link is null) { Client = null; return new SessionState(false, null, null); }
        try
        {
            _link = link;
            Client = new SonuClient(link);
            var swCompat = System.Diagnostics.Stopwatch.StartNew();
            var compat = await _checker.CheckAsync(Client, ct);
            Log.Info("PERF connect compat={0}ms", swCompat.ElapsedMilliseconds);
            return new SessionState(true, compat.Device, compat);
        }
        catch
        {
            link.Close(); _link = null; Client = null;
            throw;
        }
    }

    public void Disconnect() { _link?.Close(); _link = null; Client = null; }
    public void Dispose() => Disconnect();
}
