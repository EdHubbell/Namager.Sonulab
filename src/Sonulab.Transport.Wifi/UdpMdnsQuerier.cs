using System.Net;
using System.Net.Sockets;

namespace Sonulab.Transport.Wifi;

/// <summary>Real multicast mDNS browse. The pedal answers intermittently, so the query is
/// RE-SENT every 2 s within the window (a single-shot query was observed to miss). Thin
/// (socket plumbing only); parsing is the unit-tested MdnsMessages.</summary>
public sealed class UdpMdnsQuerier : IMdnsQuerier
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async Task<MdnsRecord?> DiscoverPedalAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = MdnsMessages.BuildHttpTcpPtrQuery();
            var deadline = DateTime.UtcNow + timeout;
            var nextSend = DateTime.MinValue;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= nextSend)
                {
                    await udp.SendAsync(query, query.Length, Multicast);
                    nextSend = DateTime.UtcNow.AddSeconds(2);
                }

                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recvCts.CancelAfter(300);
                try
                {
                    var result = await udp.ReceiveAsync(recvCts.Token);
                    var rec = MdnsMessages.TryParsePedal(result.Buffer);
                    if (rec is not null)
                    {
                        Log.Info("mDNS found pedal {0} at {1}:{2}", rec.InstanceName, rec.Address, rec.Port);
                        return rec;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // receive window elapsed — loop re-sends / keeps listening
                }
            }
            Log.Info("mDNS browse: no pedal within {0}", timeout);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            Log.Info("mDNS unavailable: {0}", ex.Message);   // no network / multicast blocked
            return null;
        }
    }
}
