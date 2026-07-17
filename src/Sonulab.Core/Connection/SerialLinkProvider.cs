using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>USB-serial transport provider: wraps the existing SonuConnector scan.
/// Port names are enumerated FRESH on every attempt so a pedal replugged onto a new
/// COM number (COM6 -> COM9) is found without restarting the app.</summary>
public sealed class SerialLinkProvider : ILinkProvider
{
    private readonly Func<ISerialPortStream> _portFactory;
    private readonly SerialLinkOptions? _options;
    private readonly Func<IReadOnlyList<string>> _portNames;
    private readonly IReadOnlyList<int> _bauds;

    public SerialLinkProvider(
        Func<ISerialPortStream> portFactory,
        SerialLinkOptions? options = null,
        Func<IReadOnlyList<string>>? portNames = null,
        IReadOnlyList<int>? bauds = null)
    {
        _portFactory = portFactory;
        _options = options;
        _portNames = portNames ?? (() => System.IO.Ports.SerialPort.GetPortNames());
        _bauds = bauds ?? new[] { 115200 };
    }

    public string Name => "USB";

    public async Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default)
    {
        var ports = _portNames();
        if (ports.Count == 0) return null;
        return await new SonuConnector(_portFactory, _options).ConnectAsync(ports, _bauds, ct);
    }
}
