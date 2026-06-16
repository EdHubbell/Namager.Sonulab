// Plan 2/3a hardware harness — drives the REAL SystemSerialPort end-to-end.
//   dotnet run --project tools/HwCheck                 # read-only: connect/identify/compat/list
//   dotnet run --project tools/HwCheck -- --write-test # + guarded duplicate to an empty slot, then delete
// Requires VoidX-Control CLOSED (it holds COM6).
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

// Ports: explicit args win; otherwise auto-discover by probing every present COM port
// (whichever answers `read root\sys\_name` is the pedal — no hardcoded COM6 assumption).
var ports = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (ports.Length == 0)
{
    ports = System.IO.Ports.SerialPort.GetPortNames();
    if (ports.Length == 0) { Console.WriteLine("RESULT: no COM ports present. Is the pedal plugged in via USB?"); return 1; }
}
bool writeTest = Array.IndexOf(args, "--write-test") >= 0;
bool reorderTest = Array.IndexOf(args, "--reorder-test") >= 0;

var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
var connector = new SonuConnector(() => new SystemSerialPort(), options);
var checker = new CompatibilityChecker(FirmwareCatalog.Default);

Console.WriteLine($"Connecting on [{string.Join(",", ports)}] @115200 ...");
using var session = new DeviceSession(connector, checker);
var state = await session.ConnectAsync(ports, new[] { 115200 });
if (!state.Connected)
{
    Console.WriteLine($"RESULT: NOT CONNECTED — no StompStation answered on [{string.Join(", ", ports)}].");
    Console.WriteLine("  Check: (1) VoidX-Control is CLOSED — it holds the COM port exclusively;");
    Console.WriteLine("         (2) the pedal is connected via USB (the CH340 'USB-SERIAL' port).");
    return 1;
}

var d = state.Device!; var c = state.Compatibility!;
Console.WriteLine($"CONNECTED  name='{d.Name}'  ver={d.Version}  arch={d.Arch}  license={d.License}");
Console.WriteLine($"Compatibility: {c.Status}  writesAllowed={c.WritesAllowed}  ({c.Message})");

var repo = new DeviceRepository(session.Client!);
var slots = await repo.ListPresetsAsync();
Console.WriteLine($"Presets: {slots.Count(s => !s.IsEmpty)}/30 in use:");
foreach (var s in slots) if (!s.IsEmpty) Console.WriteLine($"   slot {s.Index + 1,2} (idx {s.Index,2}): {s.Name}");

int ri = Array.IndexOf(args, "--restore");
if (ri >= 0 && ri + 3 < args.Length)
{
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); return 3; }
    int idx = int.Parse(args[ri + 1]); var pst = args[ri + 2]; var nm = args[ri + 3];
    var doc = Sonulab.Core.Model.PresetDocument.Parse(System.IO.File.ReadAllBytes(pst));
    Console.WriteLine($"restoring idx {idx} <- '{pst}' as '{nm}'...");
    await repo.WritePresetToSlotAsync(idx, nm, doc);
    var names = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    Console.WriteLine(names[idx] == nm ? $"  OK: idx {idx} now '{nm}'" : "  FAIL");
    session.Disconnect();
    return names[idx] == nm ? 0 : 4;
}

if (reorderTest)
{
    Console.WriteLine("\n--- GUARDED REORDER TEST (small move, then move back) ---");
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); return 3; }
    var svc = new ReorderService(repo);
    var before = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    int rfrom = Array.FindIndex(before, n => !string.IsNullOrEmpty(n));
    int rto = Math.Min(rfrom + 2, 29);                 // small range for speed (each shifted slot replays ~157 params)
    if (rfrom < 0 || rfrom == rto) { Console.WriteLine("need a movable preset; abort."); return 3; }
    Console.WriteLine($"moving idx {rfrom} ('{before[rfrom]}') -> idx {rto}, then back...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await svc.MoveAsync(rfrom, rto, new Progress<ReorderProgress>(p => Console.WriteLine($"   [{p.Done}/{p.Total}] {p.Message}")));
    var moved = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    Console.WriteLine(moved[rto] == before[rfrom] ? $"  OK: '{before[rfrom]}' now at idx {rto}" : "  FAIL: move did not land");
    await svc.MoveAsync(rto, rfrom);                   // move it back
    sw.Stop();
    var restored = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    bool rok = restored.SequenceEqual(before);
    Console.WriteLine(rok ? $"  OK: order restored to original (round trip {sw.ElapsedMilliseconds} ms)" : "  FAIL: not restored");
    session.Disconnect();
    Console.WriteLine(rok ? "RESULT: REORDER-TEST PASS" : "RESULT: REORDER-TEST FAIL");
    return rok ? 0 : 4;
}

if (!writeTest)
{
    Console.WriteLine("RESULT: read-only PASS. (pass --write-test or --reorder-test)");
    return 0;
}

Console.WriteLine("\n--- GUARDED WRITE TEST (empty slot only; restored afterward) ---");
if (!c.WritesAllowed) { Console.WriteLine("writes not allowed on this firmware; abort."); return 3; }

int empty = slots.First(s => s.IsEmpty).Index;
int source = slots.First(s => !s.IsEmpty).Index;
Console.WriteLine($"Duplicating idx {source} ('{slots[source].Name}') -> empty idx {empty} as 'HW Test' (this replays ~157 params)...");
var t0 = System.Diagnostics.Stopwatch.StartNew();
await repo.DuplicateAsync(source, empty, "HW Test");
t0.Stop();
Console.WriteLine($"  duplicate took {t0.ElapsedMilliseconds} ms");

var after = await repo.ListPresetsAsync();
bool named = after[empty].Name == "HW Test";
Console.WriteLine(named ? $"  OK: idx {empty} now 'HW Test'" : "  FAIL: name not set");

var srcDoc = await repo.ReadPresetAsync(source);
var dupDoc = await repo.ReadPresetAsync(empty);
bool match = srcDoc.ToBytes().AsSpan().SequenceEqual(dupDoc.ToBytes());
Console.WriteLine(match ? "  OK: duplicated content == source (byte-identical)" : "  FAIL: content differs");

await repo.DeleteAsync(empty);
var cleaned = await repo.ListPresetsAsync();
bool clean = cleaned[empty].IsEmpty;
Console.WriteLine(clean ? $"  OK: idx {empty} cleaned up (deleted)" : "  FAIL: slot not cleaned");

session.Disconnect();
Console.WriteLine((named && match && clean) ? "RESULT: WRITE-TEST PASS" : "RESULT: WRITE-TEST FAIL");
return (named && match && clean) ? 0 : 4;
