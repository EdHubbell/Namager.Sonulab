// Plan 2 hardware validation harness — drives the REAL SystemSerialPort end-to-end.
// Usage: dotnet run --project tools/HwCheck            (defaults to COM6)
// Read-only: connects, identifies, checks compatibility, lists presets. No writes.
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
var connector = new SonuConnector(() => new SystemSerialPort(), options);
var checker = new CompatibilityChecker(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });

var ports = args.Length > 0 ? args : new[] { "COM6" };
Console.WriteLine($"Connecting on [{string.Join(",", ports)}] @115200 (settle {options.OpenSettleMs}ms, {options.ProbeAttempts} attempts, idleGap {options.IdleGapMs}, maxWait {options.MaxWaitMs})...");

int rc;
using (var session = new DeviceSession(connector, checker))
{
    var state = await session.ConnectAsync(ports, new[] { 115200 });
    if (!state.Connected) { Console.WriteLine("RESULT: NOT CONNECTED."); return 1; }
    var d = state.Device!; var c = state.Compatibility!;
    Console.WriteLine($"CONNECTED  name='{d.Name}'  ver={d.Version}  arch={d.Arch}  license={d.License}");
    Console.WriteLine($"Compatibility: {c.Status}  writesAllowed={c.WritesAllowed}  ({c.Message})");

    var presets = await session.Client!.ReadListAsync(@"root\presets");
    Console.WriteLine($"ReadListAsync(root\\presets): {presets.Count(p => !string.IsNullOrEmpty(p))}/{presets.Count} in use");
    for (int i = 0; i < presets.Count; i++)
        if (!string.IsNullOrEmpty(presets[i])) Console.WriteLine($"   slot {i + 1,2}: {presets[i]}");
    rc = presets.Count == 0 ? 2 : 0;
    session.Disconnect();
}

// --- Raw diagnostic: re-open directly and dump the exact bytes for `read root\presets` ---
Console.WriteLine("\n--- raw diagnostic ---");
var link = new SerialSonuLink(new SystemSerialPort(), ports[0], 115200, options);
await link.OpenAsync();
var raw = await link.SendAsync(@"read root\presets");
Console.WriteLine($"raw length: {raw.Length}");
Console.WriteLine("raw (NUL->|, first 400): " + raw.Replace("\0", "|").Replace("\r\n", "\\n ").Substring(0, Math.Min(400, raw.Length)));
var recs = ResponseParser.NonMeterRecords(raw).ToList();
Console.WriteLine($"parsed non-meter records: {recs.Count}");
foreach (var r in recs.Take(3)) Console.WriteLine("   rec: " + r.Substring(0, Math.Min(80, r.Length)));
link.Close();
return rc;
