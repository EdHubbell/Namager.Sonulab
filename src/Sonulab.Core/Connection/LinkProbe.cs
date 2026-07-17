using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core.Connection;

/// <summary>Identity probe shared by all transports: the pedal is the thing that answers
/// read root\sys\_name with a matching record (meter stream filtered out).</summary>
public static class LinkProbe
{
    public static async Task<bool> VerifyAsync(ISonuLink link, CancellationToken ct = default)
    {
        var resp = await link.SendAsync(@"read root\sys\_name", ct);
        return ResponseParser.NonMeterRecords(resp)
            .Any(r => NodeRecord.TryParse(r, out var nr) && nr.Path == @"root\sys\_name");
    }
}
