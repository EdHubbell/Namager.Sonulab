namespace Sonulab.Core.Connection;

public sealed record SessionState(bool Connected, DeviceInfo? Device, CompatibilityResult? Compatibility);

public sealed class DeviceSession
{
    private readonly SonuConnector _connector;
    private readonly CompatibilityChecker _checker;

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

        Client = new SonuClient(link);
        var compat = await _checker.CheckAsync(Client, ct);
        return new SessionState(true, compat.Device, compat);
    }
}
