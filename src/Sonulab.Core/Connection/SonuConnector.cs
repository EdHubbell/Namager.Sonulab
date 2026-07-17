using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

public sealed class SonuConnector
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly Func<ISerialPortStream> _portFactory;
    private readonly SerialLinkOptions? _options;

    public SonuConnector(Func<ISerialPortStream> portFactory, SerialLinkOptions? options = null)
    {
        _portFactory = portFactory; _options = options;
    }

    public async Task<SerialSonuLink?> ConnectAsync(
        IReadOnlyList<string> ports, IReadOnlyList<int> bauds, CancellationToken ct = default)
    {
        foreach (var port in ports)
        foreach (var baud in bauds)
        {
            ct.ThrowIfCancellationRequested();
            var link = new SerialSonuLink(_portFactory(), port, baud, _options);
            int attempts = Math.Max(1, _options?.ProbeAttempts ?? 1);
            int retryDelay = _options?.ProbeRetryDelayMs ?? 300;
            try
            {
                var swOpen = System.Diagnostics.Stopwatch.StartNew();
                await link.OpenAsync(ct);
                swOpen.Stop();
                var swProbe = System.Diagnostics.Stopwatch.StartNew();
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    // First command after open is often lost to the ESP32 reset — retry.
                    bool ok = await LinkProbe.VerifyAsync(link, ct);
                    if (ok)
                    {
                        Log.Info("PERF connect open+settle={0}ms probes={1}ms attempts={2} port={3}",
                            swOpen.ElapsedMilliseconds, swProbe.ElapsedMilliseconds, attempt + 1, port);
                        return link;
                    }
                    if (attempt + 1 < attempts) await Task.Delay(retryDelay, ct);
                }
                link.Close();
            }
            catch (OperationCanceledException) { try { link.Close(); } catch { } throw; }
            catch { try { link.Close(); } catch { /* port busy/denied — try next */ } }
        }
        return null;
    }
}
