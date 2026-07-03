// Plan 2/3a hardware harness — drives the REAL SystemSerialPort end-to-end.
//   dotnet run --project tools/HwCheck                 # read-only: connect/identify/compat/list
//   dotnet run --project tools/HwCheck -- --browse     # read-only dump of root\app (or --browse <path>)
//   dotnet run --project tools/HwCheck -- --dump-amps  # read-only: pull every amp slot's converted .vxamp blob
//   dotnet run --project tools/HwCheck -- --write-test # + guarded duplicate to an empty slot, then delete
// Requires VoidX-Control CLOSED (it holds COM6).
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

// Ports: `--port COMx` pins a port; otherwise auto-discover by probing every present COM port
// (whichever answers `read root\sys\_name` is the pedal). Command flags like --restore carry their
// own positional args, so we must NOT treat bare args as port names.
string[] ports;
int portFlag = Array.IndexOf(args, "--port");
if (portFlag >= 0 && portFlag + 1 < args.Length)
    ports = new[] { args[portFlag + 1] };
else
    ports = System.IO.Ports.SerialPort.GetPortNames();
if (ports.Length == 0) { Console.WriteLine("RESULT: no COM ports present. Is the pedal plugged in via USB?"); return 1; }
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

// --browse [path]  : read-only dump of a browse subtree (default root\app). Safe; no writes.
int bi = Array.IndexOf(args, "--browse");
if (bi >= 0)
{
    var bpath = (bi + 1 < args.Length && !args[bi + 1].StartsWith("--", StringComparison.Ordinal)) ? args[bi + 1] : @"root\app";
    Console.WriteLine($"\n--- BROWSE {bpath} (read-only) ---");
    var recs = await session.Client!.BrowseRecordsAsync(bpath);
    foreach (var rec in recs) Console.WriteLine($"{rec.Path}: {rec.Json.GetRawText()}");
    Console.WriteLine($"RESULT: BROWSE COMPLETE ({recs.Count} records)");
    session.Disconnect();
    return 0;
}

// --dump-amps : read-only. Pull every occupied amp slot's CONVERTED blob (root\amp payload,
// chunks 1..96 = 12288 B) to NAMFiles/VxampDump/. Pairs with the source .nam corpus so we can
// reverse-engineer VoidX's .nam -> vxamp conversion. No writes.
if (Array.IndexOf(args, "--dump-amps") >= 0)
{
    const string AmpList = @"root\amp";
    const int AmpChunks = 96;                    // 12288 / 128
    var client = session.Client!;
    var ampNames = await client.ReadListAsync(AmpList);
    var outDir = System.IO.Path.GetFullPath(System.IO.Path.Combine("NAMFiles", "VxampDump"));
    System.IO.Directory.CreateDirectory(outDir);
    Console.WriteLine($"\n--- DUMP AMPS (read-only) -> {outDir} ---");
    var invalid = System.IO.Path.GetInvalidFileNameChars();
    int dumped = 0;
    for (int idx = 0; idx < ampNames.Count; idx++)
    {
        var name = ampNames[idx];
        if (string.IsNullOrEmpty(name)) continue;
        var blob = await client.DReadBlobAsync(AmpList, idx, AmpChunks);
        // Real payload length = the fixed 12288-byte slot minus trailing zero padding. This is the
        // single most useful RE diagnostic: it tells us how big the converted model actually is.
        int payload = blob.Length; while (payload > 0 && blob[payload - 1] == 0) payload--;
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        var path = System.IO.Path.Combine(outDir, $"{idx:D2} - {safe}.vxamp");
        await System.IO.File.WriteAllBytesAsync(path, blob);
        var head = Convert.ToHexString(blob, 0, Math.Min(32, blob.Length));
        Console.WriteLine($"  slot {idx + 1,2} (idx {idx,2}): '{name}'  blob={blob.Length}B payload={payload}B  head={head}");
        dumped++;
    }
    Console.WriteLine($"RESULT: DUMP-AMPS COMPLETE ({dumped} amps -> {outDir})");
    session.Disconnect();
    return 0;
}

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

if (Array.IndexOf(args, "--reorder-probe") >= 0)
{
    Console.WriteLine("\n--- GUARDED REORDER PROBE (backup -> test list-write reorder -> restore -> time select+save) ---");
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); return 3; }
    var client = session.Client!;
    var backup = new BackupService(repo);
    var bdir = System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups", "probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    int nb = await backup.SnapshotAllAsync(bdir);
    Console.WriteLine($"[backup] {nb} presets -> {bdir}");

    static string Json(string[] a) => "[" + string.Join(",", a.Select(x => "\"" + x + "\"")) + "]";
    var names0 = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();

    int i = -1;
    for (int k = 0; k + 1 < names0.Length; k++) if (names0[k].Length > 0 && names0[k + 1].Length > 0) { i = k; break; }
    if (i < 0) { Console.WriteLine("need two adjacent presets; abort."); return 3; }
    Console.WriteLine($"[exp A] swap names[{i}]='{names0[i]}' <-> names[{i + 1}]='{names0[i + 1]}' via a root\\presets list write");

    var cI = (await repo.ReadPresetAsync(i)).ToBytes();
    var cJ = (await repo.ReadPresetAsync(i + 1)).ToBytes();

    var swapped = names0.ToArray(); (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
    await client.WriteAsync(@"root\presets", Json(swapped));
    await Task.Delay(800);

    var names1 = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    bool namesSwapped = names1[i] == names0[i + 1] && names1[i + 1] == names0[i];
    var aI = (await repo.ReadPresetAsync(i)).ToBytes();
    var aJ = (await repo.ReadPresetAsync(i + 1)).ToBytes();
    bool contentMoved = aI.AsSpan().SequenceEqual(cJ) && aJ.AsSpan().SequenceEqual(cI);
    bool contentStayed = aI.AsSpan().SequenceEqual(cI) && aJ.AsSpan().SequenceEqual(cJ);
    Console.WriteLine($"   names after: [{i}]='{names1[i]}' [{i + 1}]='{names1[i + 1]}'  (namesSwapped={namesSwapped})");
    Console.WriteLine($"   content: movedWithNames={contentMoved}  stayedPut={contentStayed}");
    Console.WriteLine(
        (namesSwapped && contentMoved) ? "   => FINDING: list-write REORDERS content — near-free one-command reorder!" :
        (namesSwapped && contentStayed) ? "   => FINDING: list-write changes NAMES ONLY (desyncs name/content) — NOT a safe reorder" :
        (!namesSwapped) ? "   => FINDING: list-write had NO effect on order (not supported)" :
        "   => FINDING: ambiguous");

    // restore original order, then verify; fall back to per-slot restore from backup
    await client.WriteAsync(@"root\presets", Json(names0));
    await Task.Delay(800);
    var namesR = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    var rI = (await repo.ReadPresetAsync(i)).ToBytes();
    var rJ = (await repo.ReadPresetAsync(i + 1)).ToBytes();
    bool restored = namesR.SequenceEqual(names0) && rI.AsSpan().SequenceEqual(cI) && rJ.AsSpan().SequenceEqual(cJ);
    if (restored) Console.WriteLine("[restore] original order + content verified");
    else
    {
        Console.WriteLine("[restore] mismatch — rewriting slots from backup");
        foreach (var idx in new[] { i, i + 1 })
        {
            var f = System.IO.Directory.GetFiles(bdir, $"{idx:D2} - *.pst").FirstOrDefault();
            if (f != null) await backup.RestoreSlotAsync(idx, f);
        }
        var ok = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray().SequenceEqual(names0);
        Console.WriteLine(ok ? "[restore] backup rewrite OK" : "[restore] STILL OFF — check docs/backups manually");
    }

    // exp B: time select-by-name + save-to-slot (device copies content internally)
    int e = (await repo.ListPresetsAsync()).First(s => s.IsEmpty).Index;
    await repo.RenameAsync(e, "ProbeTmp");
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    await repo.SelectPresetAsync(names0[i]);
    await repo.SaveCurrentAsAsync("ProbeTmp");
    sw2.Stop();
    bool selSaveOk = (await repo.ReadPresetAsync(e)).ToBytes().AsSpan().SequenceEqual(cI);
    Console.WriteLine($"[exp B] select+save took {sw2.ElapsedMilliseconds} ms; content matches source={selSaveOk}  (vs ~12000 ms for 157-param replay)");
    await repo.DeleteAsync(e);

    session.Disconnect();
    Console.WriteLine("RESULT: REORDER-PROBE COMPLETE");
    return 0;
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
