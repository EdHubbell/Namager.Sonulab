// Plan 2/3a hardware harness — drives the REAL SystemSerialPort end-to-end.
//   dotnet run --project tools/HwCheck                 # read-only: connect/identify/compat/list
//   dotnet run --project tools/HwCheck -- --write-test # + guarded duplicate to an empty slot, then delete
// Requires VoidX-Control CLOSED (it holds COM6).
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

var ports = args.Where(a => !a.StartsWith("--")).ToArray();
if (ports.Length == 0) ports = new[] { "COM6" };
bool writeTest = Array.IndexOf(args, "--write-test") >= 0;

var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
var connector = new SonuConnector(() => new SystemSerialPort(), options);
var checker = new CompatibilityChecker(FirmwareCatalog.Default);

Console.WriteLine($"Connecting on [{string.Join(",", ports)}] @115200 ...");
using var session = new DeviceSession(connector, checker);
var state = await session.ConnectAsync(ports, new[] { 115200 });
if (!state.Connected) { Console.WriteLine("RESULT: NOT CONNECTED (is VoidX closed?)."); return 1; }

var d = state.Device!; var c = state.Compatibility!;
Console.WriteLine($"CONNECTED  name='{d.Name}'  ver={d.Version}  arch={d.Arch}  license={d.License}");
Console.WriteLine($"Compatibility: {c.Status}  writesAllowed={c.WritesAllowed}  ({c.Message})");

var repo = new DeviceRepository(session.Client!);
var slots = await repo.ListPresetsAsync();
Console.WriteLine($"Presets: {slots.Count(s => !s.IsEmpty)}/30 in use:");
foreach (var s in slots) if (!s.IsEmpty) Console.WriteLine($"   slot {s.Index + 1,2} (idx {s.Index,2}): {s.Name}");

if (!writeTest)
{
    Console.WriteLine("RESULT: read-only PASS. (pass --write-test for the guarded duplicate test)");
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
