namespace Sonulab.Transport.Wifi;

/// <summary>The pedal's parsed mDNS advertisement (PROTOCOL.md §WiFi/TCP).</summary>
public sealed record MdnsRecord(string InstanceName, string Host, string Address, int Port, string? DeviceName);
