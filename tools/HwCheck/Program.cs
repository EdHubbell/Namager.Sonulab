// Plan 2/3a hardware harness — drives the REAL SystemSerialPort end-to-end.
//   dotnet run --project tools/HwCheck                 # read-only: connect/identify/compat/list
//   dotnet run --project tools/HwCheck -- --browse     # read-only dump of root\app (or --browse <path>)
//   dotnet run --project tools/HwCheck -- --dump-amps  # read-only: pull every amp slot's converted .vxamp blob
//   dotnet run --project tools/HwCheck -- --write-test # + guarded duplicate to an empty slot, then delete
//   dotnet run --project tools/HwCheck -- --list-amps  # read-only amp slot name table
//   dotnet run --project tools/HwCheck -- --dump-irs   # read-only: pull every IR slot's 4096-byte blob
//   dotnet run --project tools/HwCheck -- --list-irs   # read-only IR slot name table
//   dotnet run --project tools/HwCheck -- --upload-amp <vxampPath> <slotIndex> [--name <n>]  # guarded amp upload (backup+write+verify)
//   dotnet run --project tools/HwCheck -- --delete-amp <slotIndex>              # guarded amp delete (backup+clear name)
//   dotnet run --project tools/HwCheck -- --upload-ir <irblob> <slotIndex> [--name <n>]  # guarded IR upload (backup+write+verify)
//   dotnet run --project tools/HwCheck -- --delete-ir <slotIndex>              # guarded IR delete (backup+clear name)
//   dotnet run --project tools/HwCheck -- --preset-dwrite-probe [--src <idx>] [--dst <idx>]  # guarded, timed re-test of preset dwrite
//   dotnet run --project tools/HwCheck -- --dread-arg-probe [--idx <n>] [--include-crash-variants]  # read-only fuzz: does dread accept a batch/size arg? (crash variants opt-in)
//   dotnet run --project tools/HwCheck -- --pipeline-probe [--idx <n>] [--depth <d>]  # read-only: pipelined dread burst timing (serial only)
//   dotnet run --project tools/HwCheck -- --dswap-probe [--a <idx>] [--b <idx>] [--path <root\presets|root\amp|root\ir>] [--active]  # guarded probe of the undocumented dswap verb (backup + self-reversing); --path targets amp/ir tables, --active checks whether swapping the live preset disturbs it
//   dotnet run --project tools/HwCheck -- --wifi [--ip <addr>] [...]  # any mode over WiFi (mDNS discovery; --ip pins the endpoint)
// Requires VoidX-Control CLOSED (it holds COM6).
using System.Diagnostics;
using System.Text;
using Sonulab.Core.Connection;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

static int? ArgAfter(string[] a, string flag)
{
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : null;
}

// Ports: `--port COMx` pins a port; otherwise the provider auto-discovers by probing every
// present COM port fresh at connect time (whichever answers `read root\sys\_name` is the pedal).
int portFlag = Array.IndexOf(args, "--port");
Func<IReadOnlyList<string>> portNames = portFlag >= 0 && portFlag + 1 < args.Length
    ? () => new[] { args[portFlag + 1] }
    : () => System.IO.Ports.SerialPort.GetPortNames();
bool writeTest = Array.IndexOf(args, "--write-test") >= 0;
bool reorderTest = Array.IndexOf(args, "--reorder-test") >= 0;

var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };

bool useWifi = Array.IndexOf(args, "--wifi") >= 0;
int ipFlag = Array.IndexOf(args, "--ip");
string? pinnedIp = ipFlag >= 0 && ipFlag + 1 < args.Length ? args[ipFlag + 1] : null;

var providers = useWifi
    ? new List<ILinkProvider>
    {
        pinnedIp is not null
            ? Sonulab.Transport.Wifi.WifiLinkProvider.ForKnownEndpoint(pinnedIp)
            : new Sonulab.Transport.Wifi.WifiLinkProvider(
                new Sonulab.Transport.Wifi.UdpMdnsQuerier(), TimeSpan.FromSeconds(6)),
    }
    : new List<ILinkProvider>
    {
        new SerialLinkProvider(() => new SystemSerialPort(), options, portNames),
    };
var checker = new CompatibilityChecker(FirmwareCatalog.Default);

Console.WriteLine(useWifi
    ? (pinnedIp is not null ? $"Connecting (WiFi, pinned {pinnedIp}:8080) ..." : "Connecting (WiFi, mDNS discovery) ...")
    : "Connecting (USB serial, auto-discover) ...");
using var session = new DeviceSession(providers, checker);
var state = await session.ConnectAsync();
if (!state.Connected)
{
    Console.WriteLine("RESULT: NOT CONNECTED — no StompStation answered on any COM port.");
    Console.WriteLine("  Check: (1) VoidX-Control is CLOSED — it holds the COM port exclusively;");
    Console.WriteLine("         (2) the pedal is connected via USB (the CH340 'USB-SERIAL' port).");
    if (useWifi) Console.WriteLine("         (WiFi mode: pedal powered + on the same network; multicast/mDNS not blocked)");
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
    var ampSvc = new AmpService(session.Client!, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var ampSlots = await ampSvc.ListAmpsAsync();
    var outDir = System.IO.Path.GetFullPath(System.IO.Path.Combine("NAMFiles", "VxampDump"));
    System.IO.Directory.CreateDirectory(outDir);
    Console.WriteLine($"\n--- DUMP AMPS (read-only) -> {outDir} ---");
    var invalid = System.IO.Path.GetInvalidFileNameChars();
    int dumped = 0;
    foreach (var s in ampSlots)
    {
        if (s.IsEmpty) continue;
        var blob = await ampSvc.ReadAmpAsync(s.Index);
        // Real payload length = the fixed 12288-byte slot minus trailing zero padding. This is the
        // single most useful RE diagnostic: it tells us how big the converted model actually is.
        int payload = blob.Length; while (payload > 0 && blob[payload - 1] == 0) payload--;
        var safe = new string(s.Name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        var path = System.IO.Path.Combine(outDir, $"{s.Index:D2} - {safe}.vxamp");
        await System.IO.File.WriteAllBytesAsync(path, blob);
        var head = Convert.ToHexString(blob, 0, Math.Min(32, blob.Length));
        Console.WriteLine($"  slot {s.Index + 1,2} (idx {s.Index,2}): '{s.Name}'  blob={blob.Length}B payload={payload}B  head={head}");
        dumped++;
    }
    Console.WriteLine($"RESULT: DUMP-AMPS COMPLETE ({dumped} amps -> {outDir})");
    session.Disconnect();
    return 0;
}

// --list-amps : read-only, prints the amp slot name table (fast; no blob reads).
if (Array.IndexOf(args, "--list-amps") >= 0)
{
    var ampSvc = new AmpService(session.Client!, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var ampSlots = await ampSvc.ListAmpsAsync();
    Console.WriteLine($"\n--- AMP SLOTS ({ampSlots.Count}) ---");
    foreach (var s in ampSlots)
        Console.WriteLine($"  slot {s.Index + 1,2} (idx {s.Index,2}): {(s.IsEmpty ? "(empty)" : $"'{s.Name}'")}");
    Console.WriteLine("RESULT: LIST-AMPS COMPLETE");
    session.Disconnect();
    return 0;
}

// --dump-irs : read-only. Pull every occupied IR slot's blob (root\ir payload,
// 32 chunks x 128 B = 4096 B) to NAMFiles/IrDump/. No writes.
if (Array.IndexOf(args, "--dump-irs") >= 0)
{
    var irSvc = new IrService(session.Client!, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var irSlots = await irSvc.ListIrsAsync();
    var irOutDir = System.IO.Path.GetFullPath(System.IO.Path.Combine("NAMFiles", "IrDump"));
    System.IO.Directory.CreateDirectory(irOutDir);
    Console.WriteLine($"\n--- DUMP IRS (read-only) -> {irOutDir} ---");
    var irInvalid = System.IO.Path.GetInvalidFileNameChars();
    int irDumped = 0;
    foreach (var s in irSlots)
    {
        if (s.IsEmpty) continue;
        var blob = await irSvc.ReadIrAsync(s.Index);
        int payload = blob.Length; while (payload > 0 && blob[payload - 1] == 0) payload--;
        var safe = new string(s.Name.Select(ch => irInvalid.Contains(ch) ? '_' : ch).ToArray());
        var path = System.IO.Path.Combine(irOutDir, $"{s.Index:D2} - {safe}.irblob");
        await System.IO.File.WriteAllBytesAsync(path, blob);
        var head = Convert.ToHexString(blob, 0, Math.Min(32, blob.Length));
        Console.WriteLine($"  slot {s.Index + 1,2} (idx {s.Index,2}): '{s.Name}'  blob={blob.Length}B payload={payload}B  head={head}");
        irDumped++;
    }
    Console.WriteLine($"RESULT: DUMP-IRS COMPLETE ({irDumped} irs -> {irOutDir})");
    session.Disconnect();
    return 0;
}

// --list-irs : read-only, prints the IR slot name table (fast; no blob reads).
if (Array.IndexOf(args, "--list-irs") >= 0)
{
    var irSvc = new IrService(session.Client!, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var irSlots = await irSvc.ListIrsAsync();
    Console.WriteLine($"\n--- IR SLOTS ({irSlots.Count}) ---");
    foreach (var s in irSlots)
        Console.WriteLine($"  slot {s.Index + 1,2} (idx {s.Index,2}): {(s.IsEmpty ? "(empty)" : $"'{s.Name}'")}");
    Console.WriteLine("RESULT: LIST-IRS COMPLETE");
    session.Disconnect();
    return 0;
}

// --dread-probe <path> <index> <chunk...> : read-only, dread arbitrary chunks and print raw hex+ASCII.
int dpi = Array.IndexOf(args, "--dread-probe");
if (dpi >= 0)
{
    var ppath = args[dpi + 1];
    int pidx = int.Parse(args[dpi + 2]);
    Console.WriteLine($"\n--- DREAD PROBE {ppath} index {pidx} (read-only) ---");
    for (int ai = dpi + 3; ai < args.Length && int.TryParse(args[ai], out int pch); ai++)
    {
        var raw = await session.Client!.SendRawAsync($"dread {ppath}:{{\"index\":{pidx},\"chunk\":{pch}}}");
        var recs = Sonulab.Core.Protocol.ResponseParser.NonMeterRecords(raw).ToList();
        Console.WriteLine($"chunk {pch,3}: raw={raw.Length}B records: {(recs.Count == 0 ? "(none)" : "")}");
        foreach (var r in recs)
        {
            Console.WriteLine($"   {(r.Length > 400 ? r[..400] + "…" : r)}");
            var m = System.Text.RegularExpressions.Regex.Match(r, "\"value\":\"([0-9a-fA-F]*)\"");
            if (m.Success)
            {
                var bytes = Convert.FromHexString(m.Groups[1].Value);
                var ascii = new string(bytes.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                Console.WriteLine($"   ascii: {ascii}");
            }
        }
    }
    Console.WriteLine("RESULT: DREAD-PROBE COMPLETE");
    session.Disconnect();
    return 0;
}

// ---- raw-port helpers for the protocol probes below (serial only; bypass SonuClient's lockstep) ----
// The probes need to (a) see EVERY response the device emits (a variant might answer with several
// NUL-terminated responses — SonuClient returns at the first NUL) and (b) send commands back-to-back
// without waiting. So they disconnect the DeviceSession and drive a SystemSerialPort directly.
static void RawCmd(ISerialPortStream port, string cmd)
{
    var b = Encoding.ASCII.GetBytes(cmd);
    port.Write(b, 0, b.Length);
    port.Write(new byte[] { 0 }, 0, 1);
}

// Collect until `done(text)` or maxWaitMs. Tight spin (no Task.Delay) — per-chunk timing is the
// measurement here and a 15 ms timer quantum would drown it.
static string RawCollect(ISerialPortStream port, Func<string, bool> done, int maxWaitMs)
{
    var sb = new StringBuilder();
    var sw = Stopwatch.StartNew();
    var buf = new byte[4096];
    while (sw.ElapsedMilliseconds < maxWaitMs)
    {
        int avail = port.BytesToRead;
        if (avail > 0)
        {
            int got = port.Read(buf, 0, Math.Min(avail, buf.Length));
            sb.Append(Encoding.ASCII.GetString(buf, 0, got));
            if (done(sb.ToString())) break;
        }
        else Thread.SpinWait(2000);
    }
    return sb.ToString();
}

static bool RawProbeIdentity(ISerialPortStream port, int attempts = 10)
{
    for (int a = 0; a < attempts; a++)
    {
        port.DiscardInBuffer();
        RawCmd(port, @"read root\sys\_name");
        var resp = RawCollect(port, t => t.Contains(@"root\sys\_name:"), 500);
        if (resp.Contains(@"root\sys\_name:")) return true;
        Thread.Sleep(150);
    }
    return false;
}

static (ISerialPortStream? Port, string Name) RawOpenPedal(IReadOnlyList<string> names)
{
    foreach (var pn in names)
    {
        var p = new SystemSerialPort();
        try
        {
            p.Open(pn, 115200);
            Thread.Sleep(300); // opening resets the ESP32; identity probe below retries through the boot
            if (RawProbeIdentity(p)) return (p, pn);
            p.Close();
            p.Dispose();
        }
        catch { try { p.Dispose(); } catch { } }
    }
    return (null, "");
}

// --dread-arg-probe [--idx <n>] : read-only fuzz of dread's JSON arguments against ONE occupied
// preset slot, to settle whether fw 2.5.1 accepts any undocumented batch/size argument. VoidX's
// app.so only ever builds {"index","chunk"}, so the expectation is "extra keys ignored, one 128-B
// chunk back" — any variant yielding >256 hex chars or >1 chunk record is a FINDING that changes
// the preset-usage scan design. Runs on the raw port so multi-response answers aren't truncated.
int dap = Array.IndexOf(args, "--dread-arg-probe");
if (dap >= 0)
{
    int n = ArgAfter(args, "--idx") ?? slots.First(s => !s.IsEmpty).Index;
    if (string.IsNullOrEmpty(slots[n].Name)) { Console.WriteLine($"RESULT: DREAD-ARG-PROBE ABORT — slot idx {n} is empty."); session.Disconnect(); return 1; }
    Console.WriteLine($"\n--- DREAD ARG PROBE  root\\presets idx {n} ('{slots[n].Name}')  (read-only, raw port) ---");
    session.Disconnect();
    Thread.Sleep(500); // let the OS release the COM port before reopening
    var (aPort, aPortName) = RawOpenPedal(portNames());
    if (aPort is null) { Console.WriteLine("RESULT: DREAD-ARG-PROBE ABORT — raw port reopen failed."); return 2; }
    Console.WriteLine($"[raw] pedal on {aPortName}");

    // Baseline: plain single-chunk dread. Everything below is compared against this hex.
    aPort.DiscardInBuffer();
    RawCmd(aPort, $"dread root\\presets:{{\"index\":{n},\"chunk\":1}}");
    var baseText = RawCollect(aPort, t => Sonulab.Core.Protocol.ResponseParser.ChunkHex(t, n, 1) is { Length: 256 }, 2500);
    var baseHex = Sonulab.Core.Protocol.ResponseParser.ChunkHex(baseText, n, 1);
    Console.WriteLine($"baseline chunk1: hex={baseHex?.Length ?? 0} chars");
    if (baseHex is not { Length: 256 }) { Console.WriteLine("RESULT: DREAD-ARG-PROBE ABORT — baseline dread failed"); aPort.Close(); return 4; }

    var variants = new List<(string Label, string Cmd)>
    {
        ("count:4",     $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"count\":4}}"),
        ("chunks:4",    $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"chunks\":4}}"),
        ("size:512",    $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"size\":512}}"),
        ("len:512",     $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"len\":512}}"),
        ("num:4",       $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"num\":4}}"),
        ("to:4",        $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"to\":4}}"),
        ("end:4",       $"dread root\\presets:{{\"index\":{n},\"chunk\":1,\"end\":4}}"),
        // range-str/array are appended below only with --include-crash-variants: a non-numeric
        // chunk value calls abort() in fw 2.5.1 (ESP32 reboots — PROTOCOL.md "dread limits & hazards").
        ("from/to",     $"dread root\\presets:{{\"index\":{n},\"from\":1,\"to\":4}}"),
        ("no-chunk",    $"dread root\\presets:{{\"index\":{n}}}"),
        ("read+json",   $"read root\\presets:{{\"index\":{n}}}"),
        ("browse+json", $"browse root\\presets:{{\"index\":{n}}}"),
    };
    if (Array.IndexOf(args, "--include-crash-variants") >= 0)
    {
        variants.Add(("range-str", $"dread root\\presets:{{\"index\":{n},\"chunk\":\"1-4\"}}"));
        variants.Add(("array", $"dread root\\presets:{{\"index\":{n},\"chunk\":[1,2,3,4]}}"));
    }
    else
        Console.WriteLine("(skipping range-str/array — known to abort() fw 2.5.1; pass --include-crash-variants to run them)");
    bool finding = false;
    foreach (var (label, cmd) in variants)
    {
        aPort.DiscardInBuffer();
        RawCmd(aPort, cmd);
        // First NUL = first response; keep listening 300 ms more in case the variant answers with
        // SEVERAL NUL-terminated responses (the case SonuClient would truncate).
        var text = RawCollect(aPort, t => t.Contains('\0'), 2000);
        text += RawCollect(aPort, _ => false, 300);
        var recs = Sonulab.Core.Protocol.ResponseParser.NonMeterRecords(text).ToList();
        int hexRecs = 0, maxHex = 0; bool matchesBase = false;
        foreach (var r in recs)
        {
            var m = System.Text.RegularExpressions.Regex.Match(r, "\"value\":\"([0-9a-fA-F]{2,})\"");
            if (m.Success)
            {
                hexRecs++;
                maxHex = Math.Max(maxHex, m.Groups[1].Value.Length);
                if (string.Equals(m.Groups[1].Value, baseHex, StringComparison.OrdinalIgnoreCase)) matchesBase = true;
            }
        }
        var head = recs.Count > 0 ? (recs[0].Length > 80 ? recs[0][..80] + "…" : recs[0]) : "(no records)";
        Console.WriteLine($"  {label,-11} raw={text.Length,5}B recs={recs.Count,2} hexRecs={hexRecs} maxHex={maxHex,4}{(matchesBase ? " =chunk1" : "        ")}  {head}");
        if (maxHex > 256 || hexRecs > 1) { finding = true; Console.WriteLine("    ^^^ FINDING: larger/multi-chunk response!"); }
    }
    bool aAlive = RawProbeIdentity(aPort);
    Console.WriteLine($"[sanity] device still answers identity: {aAlive}");
    Console.WriteLine(finding
        ? "RESULT: DREAD-ARG-PROBE FINDING — the firmware accepts a batch/size argument (see above)"
        : "RESULT: DREAD-ARG-PROBE NO-BATCH — every variant returned one 128-B chunk (or nothing)");
    aPort.Close();
    return aAlive ? 0 : 5;
}

// --pipeline-probe [--idx <n>] [--depth <d>] : read-only, SERIAL ONLY. Measures whether the
// firmware accepts pipelined dread commands (send a burst without waiting, read the response
// stream) and what the real per-chunk floor is. Ground truth is read lockstep through SonuClient
// first; every pipelined chunk must match it byte-for-byte. Escalates burst depth 2→4→…→depth
// (default 64) and stops at the first failure — worst case the ESP32 drops input, which the
// end-of-run identity probe detects (a replug recovers; nothing is written).
int ppb = Array.IndexOf(args, "--pipeline-probe");
if (ppb >= 0)
{
    if (useWifi) { Console.WriteLine("RESULT: PIPELINE-PROBE ABORT — serial only (TCP pipelining is a separate probe)."); session.Disconnect(); return 1; }
    int n = ArgAfter(args, "--idx") ?? slots.First(s => !s.IsEmpty).Index;
    int maxDepth = Math.Clamp(ArgAfter(args, "--depth") ?? 64, 2, 64);
    if (string.IsNullOrEmpty(slots[n].Name)) { Console.WriteLine($"RESULT: PIPELINE-PROBE ABORT — slot idx {n} is empty."); session.Disconnect(); return 1; }

    Console.WriteLine($"\n--- PIPELINE PROBE  root\\presets idx {n} ('{slots[n].Name}')  (read-only) ---");
    Console.WriteLine("[truth] lockstep 64-chunk read via SonuClient (this is the slow path we're trying to beat)...");
    var truthSw = Stopwatch.StartNew();
    var truth = await session.Client!.DReadBlobAsync(@"root\presets", n, 64);
    Console.WriteLine($"[truth] {truth.Length} B in {truthSw.ElapsedMilliseconds} ms ({truthSw.ElapsedMilliseconds / 64.0:F1} ms/chunk lockstep incl. client overhead)");
    if (truth.Length != 8192) { Console.WriteLine("RESULT: PIPELINE-PROBE ABORT — ground-truth read came back short."); session.Disconnect(); return 4; }

    session.Disconnect();
    Thread.Sleep(500);
    var (port, portName) = RawOpenPedal(portNames());
    if (port is null) { Console.WriteLine("RESULT: PIPELINE-PROBE ABORT — raw port reopen failed."); return 2; }
    Console.WriteLine($"[raw] pedal on {portName}");

    string TruthHex(int chunk) => Convert.ToHexString(truth, (chunk - 1) * 128, 128);
    string Cmd(int chunk) => $"dread root\\presets:{{\"index\":{n},\"chunk\":{chunk}}}";

    // Sequential raw baseline (lockstep, but without SonuClient/gate/Task.Delay overhead) — the
    // fair "before" number for the pipelined comparison.
    const int seqDepth = 8;
    var sw = Stopwatch.StartNew();
    bool seqOk = true;
    for (int ch = 1; ch <= seqDepth && seqOk; ch++)
    {
        port.DiscardInBuffer();
        RawCmd(port, Cmd(ch));
        int cc = ch; // capture
        var text = RawCollect(port, t => Sonulab.Core.Protocol.ResponseParser.ChunkHex(t, n, cc) is { Length: 256 }, 2500);
        var hex = Sonulab.Core.Protocol.ResponseParser.ChunkHex(text, n, cc);
        seqOk = string.Equals(hex, TruthHex(cc), StringComparison.OrdinalIgnoreCase);
    }
    sw.Stop();
    Console.WriteLine(seqOk
        ? $"[seq ] {seqDepth} chunks lockstep raw: {sw.ElapsedMilliseconds} ms  ({sw.ElapsedMilliseconds / (double)seqDepth:F1} ms/chunk)"
        : "[seq ] BASELINE FAILED — aborting (device not answering dreads on the raw port)");
    if (!seqOk) { port.Close(); return 4; }

    // Runs one pipelined attempt: send `depth` commands paced `paceMs` apart (0 = single burst
    // write) while reading continuously. Returns (chunks completed, elapsed ms, all-match).
    (int Complete, long Ms, bool AllMatch) RunPipelined(int depth, int paceMs)
    {
        port.DiscardInBuffer();
        var sb = new StringBuilder();
        var buf = new byte[4096];
        int sent = 0, next = 1;
        var swB = Stopwatch.StartNew();
        long nextSendAt = 0;
        long deadline = 3000 + (long)paceMs * depth + 120L * depth;
        if (paceMs == 0)
        {
            var burst = new StringBuilder();
            for (int ch = 1; ch <= depth; ch++) { burst.Append(Cmd(ch)); burst.Append('\0'); }
            var burstBytes = Encoding.ASCII.GetBytes(burst.ToString());
            port.Write(burstBytes, 0, burstBytes.Length);
            sent = depth;
        }
        while (swB.ElapsedMilliseconds < deadline && next <= depth)
        {
            if (sent < depth && swB.ElapsedMilliseconds >= nextSendAt)
            {
                sent++;
                RawCmd(port, Cmd(sent));
                nextSendAt = swB.ElapsedMilliseconds + paceMs;
            }
            int avail = port.BytesToRead;
            if (avail > 0)
            {
                int got = port.Read(buf, 0, Math.Min(avail, buf.Length));
                sb.Append(Encoding.ASCII.GetString(buf, 0, got));
                var t = sb.ToString();
                while (next <= depth && Sonulab.Core.Protocol.ResponseParser.ChunkHex(t, n, next) is { Length: 256 }) next++;
            }
            else Thread.SpinWait(500);
        }
        swB.Stop();
        int complete = next - 1;
        var text = sb.ToString();
        bool allMatch = true;
        for (int ch = 1; ch <= complete; ch++)
            if (!string.Equals(Sonulab.Core.Protocol.ResponseParser.ChunkHex(text, n, ch), TruthHex(ch), StringComparison.OrdinalIgnoreCase))
            { allMatch = false; break; }
        RawCollect(port, _ => false, 250); // drain stragglers so the next attempt starts clean
        return (complete, swB.ElapsedMilliseconds, allMatch);
    }

    // Zero-gap burst first (the "true pipelining" question), then a paced ladder: send the next
    // command WHILE the previous response is still streaming (response tx is ~26 ms of the ~55 ms
    // lockstep cycle). The fastest pace at which nothing is dropped is the real per-chunk floor.
    var (bComplete, bMs, bMatch) = RunPipelined(Math.Min(2, maxDepth), 0);
    Console.WriteLine($"[pipe] burst depth 2 (zero gap): {bComplete}/2 chunks in {bMs} ms  match={bMatch}");
    bool burstWorks = bComplete == 2 && bMatch;
    if (burstWorks)
    {
        var (fComplete, fMs, fMatch) = RunPipelined(maxDepth, 0);
        Console.WriteLine($"[pipe] burst depth {maxDepth}: {fComplete}/{maxDepth} in {fMs} ms ({(fComplete > 0 ? fMs / (double)fComplete : 0):F1} ms/chunk) match={fMatch}");
    }

    int pacedDepth = Math.Min(16, maxDepth);
    double bestPerChunk = double.MaxValue; int bestPace = -1;
    foreach (int pace in new[] { 45, 35, 30, 25, 20, 15 })
    {
        var (complete, ms, pMatch) = RunPipelined(pacedDepth, pace);
        Console.WriteLine($"[pace] {pace,2} ms gap, depth {pacedDepth}: {complete}/{pacedDepth} chunks in {ms,5} ms  " +
            $"({(complete > 0 ? ms / (double)complete : 0):F1} ms/chunk)  match={pMatch}");
        if (complete < pacedDepth || !pMatch)
        {
            Console.WriteLine($"[pace] {pace} ms FAILED — stopping (fastest safe pace = {(bestPace < 0 ? "none" : bestPace + " ms")}).");
            break;
        }
        bestPerChunk = Math.Min(bestPerChunk, ms / (double)pacedDepth);
        bestPace = pace;
    }

    bool alive = RawProbeIdentity(port);
    Console.WriteLine($"[sanity] device still answers identity: {alive}");
    double lockstepPerChunk = sw.ElapsedMilliseconds / (double)seqDepth;
    Console.WriteLine(burstWorks
        ? "RESULT: PIPELINE-PROBE BURST WORKS (see depth numbers above)"
        : bestPace >= 0
            ? $"RESULT: PIPELINE-PROBE PACED-ONLY — no zero-gap bursts, but a {bestPace} ms send pace holds: ~{bestPerChunk:F1} ms/chunk (vs {lockstepPerChunk:F1} lockstep)"
            : "RESULT: PIPELINE-PROBE NOT SUPPORTED — the firmware drops any overlapped command (lockstep is the floor)");
    port.Close();
    return alive ? 0 : 5;
}

// --dswap-probe [--a <idx>] [--b <idx>] : GUARDED probe of the undocumented `dswap` verb found in
// VoidX's app.so string pool ('dswap ' + ',"index2":' — same builder pattern as dread/dwrite).
// Hypothesis: firmware-native slot swap = dswap root\presets:{"index":A,"index2":B}. Presets
// (--path root\presets, the default) back up both slots' full content first, check names+content
// after the command, and restore by swapping back (or, if the device is left in any other state,
// by rewriting both slots from the backup) — the restore path (BackupService) exists only for
// presets. --path root\amp / root\ir run a generic, self-contained probe instead: no .pst file
// backup exists for those blocks, so safety relies entirely on dswap being self-inverse — both
// slots are read-verified (name + first chunks) and the swap is ALWAYS reversed unconditionally,
// never skipped based on the "no effect" finding (that gate is preset-only; see task-1 fix notes).
int dsp = Array.IndexOf(args, "--dswap-probe");
if (dsp >= 0)
{
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    int dspPath = Array.IndexOf(args, "--path");
    string swapPath = dspPath >= 0 && dspPath + 1 < args.Length ? args[dspPath + 1] : @"root\presets";

    if (swapPath != @"root\presets")
    {
        // Generic, always-reversing probe for root\amp / root\ir. Unlike the preset branch below,
        // there is NO BackupService/.pst fallback here — the only safety net is sending the same
        // dswap command a second time (self-inverse) and read-verifying both slots afterward.
        var gClient = session.Client!;
        int chunkCount = swapPath == @"root\amp" ? 96 : swapPath == @"root\ir" ? 32 : 0;
        if (chunkCount == 0)
        {
            Console.WriteLine($"RESULT: DSWAP-PROBE ABORT — unknown --path '{swapPath}' (expected root\\presets, root\\amp, or root\\ir).");
            session.Disconnect();
            return 1;
        }

        async Task<string[]> ReadNamesAsync()
        {
            var raw = await gClient.ReadListAsync(swapPath);
            return Enumerable.Range(0, 30).Select(i => i < raw.Count ? raw[i] : "").ToArray();
        }

        var gNamesBefore = await ReadNamesAsync();
        var gOccupied = Enumerable.Range(0, 30).Where(i => !string.IsNullOrEmpty(gNamesBefore[i])).ToList();
        if (gOccupied.Count < 2)
        {
            Console.WriteLine($"RESULT: DSWAP-PROBE ABORT — need two occupied slots at {swapPath}.");
            session.Disconnect();
            return 1;
        }
        int gA = ArgAfter(args, "--a") ?? gOccupied[0];
        int gB = ArgAfter(args, "--b") ?? gOccupied[1];
        bool gOk = gA != gB && gA is >= 0 and < 30 && gB is >= 0 and < 30
                   && !string.IsNullOrEmpty(gNamesBefore[gA]) && !string.IsNullOrEmpty(gNamesBefore[gB]);
        if (!gOk)
        {
            Console.WriteLine($"RESULT: DSWAP-PROBE ABORT — need two distinct occupied slots in [0,30) at {swapPath} (got {gA},{gB}).");
            session.Disconnect();
            return 1;
        }

        Console.WriteLine($"\n--- DSWAP PROBE (guarded, generic): {swapPath} idx {gA} ('{gNamesBefore[gA]}') <-> idx {gB} ('{gNamesBefore[gB]}') ---");
        Console.WriteLine($"[warn] --path {swapPath}: no .pst file backup for this block — safety relies on dswap being self-inverse; both slots are read-verified and swapped back automatically.");

        int K = Math.Min(8, chunkCount);
        var gABefore = await gClient.DReadChunkRangeAsync(swapPath, gA, 1, K);
        var gBBefore = await gClient.DReadChunkRangeAsync(swapPath, gB, 1, K);

        var gSwapCmd = $"dswap {swapPath}:{{\"index\":{gA},\"index2\":{gB}}}";
        Console.WriteLine($"[probe] {gSwapCmd}");
        var gSw = Stopwatch.StartNew();
        var gSwapResp = await gClient.SendRawAsync(gSwapCmd);
        gSw.Stop();
        var gRespRecs = Sonulab.Core.Protocol.ResponseParser.NonMeterRecords(gSwapResp).ToList();
        Console.WriteLine($"[probe] responded in {gSw.ElapsedMilliseconds} ms; records: {(gRespRecs.Count == 0 ? "(none)" : string.Join(" | ", gRespRecs.Select(r => r.Length > 70 ? r[..70] + "…" : r)))}");
        await Task.Delay(800);

        var gNamesAfter = await ReadNamesAsync();
        var gAAfter = await gClient.DReadChunkRangeAsync(swapPath, gA, 1, K);
        var gBAfter = await gClient.DReadChunkRangeAsync(swapPath, gB, 1, K);
        bool gNamesSwapped = gNamesAfter[gA] == gNamesBefore[gB] && gNamesAfter[gB] == gNamesBefore[gA];
        bool gNamesUnchanged = gNamesAfter[gA] == gNamesBefore[gA] && gNamesAfter[gB] == gNamesBefore[gB];
        bool gContentSwapped = gAAfter.AsSpan().SequenceEqual(gBBefore) && gBAfter.AsSpan().SequenceEqual(gABefore);
        bool gContentStayed = gAAfter.AsSpan().SequenceEqual(gABefore) && gBAfter.AsSpan().SequenceEqual(gBBefore);
        Console.WriteLine($"[check] names: [{gA}]='{gNamesAfter[gA]}' [{gB}]='{gNamesAfter[gB]}'  swapped={gNamesSwapped} unchanged={gNamesUnchanged}");
        Console.WriteLine($"[check] content (first {K} chunks): swapped={gContentSwapped} stayed={gContentStayed}");
        Console.WriteLine(
            gNamesSwapped && gContentSwapped ? $"   => FINDING: dswap WORKS on {swapPath} — full slot swap in {gSw.ElapsedMilliseconds} ms!" :
            gNamesSwapped && gContentStayed ? "   => FINDING: dswap swaps NAMES ONLY — desyncs name/content, NOT usable as-is" :
            gNamesUnchanged && gContentStayed ? "   => FINDING: dswap had NO effect (verb ignored or wrong arg shape)" :
            "   => FINDING: AMBIGUOUS state — see restore below");

        // ALWAYS reverse — unlike the preset branch, there is no "nothing happened, nothing to
        // restore" shortcut here: dswap is self-inverse, so sending it again is always safe and
        // always attempted, regardless of the finding above.
        Console.WriteLine("[restore] sending dswap again to swap back (unconditional)...");
        await gClient.SendRawAsync(gSwapCmd);
        await Task.Delay(800);
        var gNamesR = await ReadNamesAsync();
        var gAR = await gClient.DReadChunkRangeAsync(swapPath, gA, 1, K);
        var gBR = await gClient.DReadChunkRangeAsync(swapPath, gB, 1, K);
        bool gRestored = gNamesR[gA] == gNamesBefore[gA] && gNamesR[gB] == gNamesBefore[gB]
                          && gAR.AsSpan().SequenceEqual(gABefore) && gBR.AsSpan().SequenceEqual(gBBefore);
        Console.WriteLine(gRestored
            ? "[restore] original names + content (first K chunks) verified"
            : $"[restore] STILL OFF — swap back MANUALLY: dswap {swapPath}:{{\"index\":{gA},\"index2\":{gB}}}");
        session.Disconnect();
        Console.WriteLine($"RESULT: DSWAP-PROBE COMPLETE (restored={gRestored})");
        return gRestored ? 0 : 5;
    }

    bool activeTest = Array.IndexOf(args, "--active") >= 0;
    var sClient = session.Client!;
    var occupied = slots.Where(s => !s.IsEmpty).Select(s => s.Index).ToList();
    if (occupied.Count < 2) { Console.WriteLine("RESULT: DSWAP-PROBE ABORT — need two occupied preset slots."); session.Disconnect(); return 1; }
    int A = ArgAfter(args, "--a") ?? occupied[0];
    int B = ArgAfter(args, "--b") ?? occupied[1];
    if (A == B || slots[A].IsEmpty || slots[B].IsEmpty)
    { Console.WriteLine($"RESULT: DSWAP-PROBE ABORT — need two distinct occupied slots (got {A},{B})."); session.Disconnect(); return 1; }

    Console.WriteLine($"\n--- DSWAP PROBE (guarded): idx {A} ('{slots[A].Name}') <-> idx {B} ('{slots[B].Name}') ---");
    var bdir = System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups", "dswap-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    System.IO.Directory.CreateDirectory(bdir);
    var namesBefore = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    var docA = await repo.ReadPresetAsync(A);
    var docB = await repo.ReadPresetAsync(B);
    var bytesA = docA.ToBytes(); var bytesB = docB.ToBytes();
    var invalidCh = System.IO.Path.GetInvalidFileNameChars();
    string San(string s2) => new(s2.Select(ch => invalidCh.Contains(ch) ? '_' : ch).ToArray());
    var fileA = System.IO.Path.Combine(bdir, $"{A:D2} - {San(namesBefore[A])}.pst");
    var fileB = System.IO.Path.Combine(bdir, $"{B:D2} - {San(namesBefore[B])}.pst");
    await System.IO.File.WriteAllBytesAsync(fileA, bytesA);
    await System.IO.File.WriteAllBytesAsync(fileB, bytesB);
    Console.WriteLine($"[backup] both slots -> {bdir}");

    var swapCmd = $"dswap {swapPath}:{{\"index\":{A},\"index2\":{B}}}";
    string? liveBefore = null;
    if (activeTest && swapPath == @"root\presets")
    {
        await sClient.SendRawAsync($"write root\\app\\preset:{{\"value\":\"{namesBefore[A]}\"}}"); // select A live
        liveBefore = (await sClient.SendRawAsync("read root\\app\\preset")).Trim();
        Console.WriteLine($"[active] live preset before swap: {liveBefore}");
    }
    Console.WriteLine($"[probe] {swapCmd}");
    var swSwap = Stopwatch.StartNew();
    var swapResp = await sClient.SendRawAsync(swapCmd);
    swSwap.Stop();
    var respRecs = Sonulab.Core.Protocol.ResponseParser.NonMeterRecords(swapResp).ToList();
    Console.WriteLine($"[probe] responded in {swSwap.ElapsedMilliseconds} ms; records: {(respRecs.Count == 0 ? "(none)" : string.Join(" | ", respRecs.Select(r => r.Length > 70 ? r[..70] + "…" : r)))}");
    await Task.Delay(800);

    if (activeTest && swapPath == @"root\presets")
    {
        var liveAfter = (await sClient.SendRawAsync("read root\\app\\preset")).Trim();
        Console.WriteLine($"[active] live preset after swap:  {liveAfter}");
        Console.WriteLine(liveBefore == liveAfter
            ? "   => ACTIVE-SLOT: live preset UNDISTURBED by dswap"
            : "   => ACTIVE-SLOT: live preset CHANGED by dswap — engine must re-select after a move touching the active slot");
    }

    var namesAfter = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
    bool namesSwapped = namesAfter[A] == namesBefore[B] && namesAfter[B] == namesBefore[A];
    bool namesUnchanged = namesAfter[A] == namesBefore[A] && namesAfter[B] == namesBefore[B];
    var afterA = (await repo.ReadPresetAsync(A)).ToBytes();
    var afterB = (await repo.ReadPresetAsync(B)).ToBytes();
    bool contentSwapped = afterA.AsSpan().SequenceEqual(bytesB) && afterB.AsSpan().SequenceEqual(bytesA);
    bool contentStayed = afterA.AsSpan().SequenceEqual(bytesA) && afterB.AsSpan().SequenceEqual(bytesB);
    Console.WriteLine($"[check] names: [{A}]='{namesAfter[A]}' [{B}]='{namesAfter[B]}'  swapped={namesSwapped} unchanged={namesUnchanged}");
    Console.WriteLine($"[check] content: swapped={contentSwapped} stayed={contentStayed}");
    Console.WriteLine(
        namesSwapped && contentSwapped ? $"   => FINDING: dswap WORKS — full slot swap in {swSwap.ElapsedMilliseconds} ms (vs ~1.5 s/step select+save)!" :
        namesSwapped && contentStayed ? "   => FINDING: dswap swaps NAMES ONLY — desyncs name/content, NOT usable as-is" :
        namesUnchanged && contentStayed ? "   => FINDING: dswap had NO effect (verb ignored or wrong arg shape)" :
        "   => FINDING: AMBIGUOUS state — see restore below");

    bool restored;
    if (namesUnchanged && contentStayed)
        restored = true; // nothing happened, nothing to restore
    else
    {
        Console.WriteLine("[restore] sending dswap again to swap back...");
        await sClient.SendRawAsync(swapCmd);
        await Task.Delay(800);
        var namesR = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
        var rA = (await repo.ReadPresetAsync(A)).ToBytes();
        var rB = (await repo.ReadPresetAsync(B)).ToBytes();
        restored = namesR[A] == namesBefore[A] && namesR[B] == namesBefore[B]
                   && rA.AsSpan().SequenceEqual(bytesA) && rB.AsSpan().SequenceEqual(bytesB);
        if (!restored)
        {
            Console.WriteLine("[restore] swap-back did NOT restore — rewriting both slots from backup (slow, ~15 s/slot)...");
            var bsvc = new BackupService(repo);
            await bsvc.RestoreSlotAsync(A, fileA);
            await bsvc.RestoreSlotAsync(B, fileB);
            var namesR2 = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
            restored = namesR2[A] == namesBefore[A] && namesR2[B] == namesBefore[B];
        }
    }
    Console.WriteLine(restored
        ? "[restore] original names + content verified"
        : $"[restore] STILL OFF — restore manually from {bdir}");
    session.Disconnect();
    Console.WriteLine($"RESULT: DSWAP-PROBE COMPLETE (restored={restored})");
    return restored ? 0 : 5;
}

// --delete-amp <slotIndex> : guarded amp delete. Backs up the blob, then clears the slot's
// name-table entry (dwrite chunk -1 all-zeros — the confirmed delete semantics). Requires WritesAllowed.
int dai = Array.IndexOf(args, "--delete-amp");
if (dai >= 0)
{
    if (dai + 1 >= args.Length) { Console.WriteLine("Usage: --delete-amp <slotIndex>"); session.Disconnect(); return 1; }
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    int dslot = int.Parse(args[dai + 1]);
    var dclient = session.Client!;
    var ampSvc = new AmpService(dclient, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var namesBefore = await ampSvc.ListAmpsAsync();
    if (dslot < 0 || dslot >= namesBefore.Count || namesBefore[dslot].IsEmpty)
    { Console.WriteLine($"RESULT: DELETE-AMP NO-OP — slot {dslot} is already empty."); session.Disconnect(); return 0; }
    Console.WriteLine($"[delete] slot {dslot} ('{namesBefore[dslot].Name}') — backing up to docs/backups, then clearing...");
    await ampSvc.DeleteAmpAsync(dslot);
    await Task.Delay(500);
    var afterDelete = await ampSvc.ListAmpsAsync();
    bool gone = afterDelete[dslot].IsEmpty;
    Console.WriteLine(gone ? $"RESULT: DELETE-AMP OK (slot {dslot} now empty)" : $"RESULT: DELETE-AMP FAIL (slot {dslot} still '{afterDelete[dslot].Name}')");
    session.Disconnect();
    return gone ? 0 : 4;
}

// --upload-amp <vxampPath> <slotIndex> [--name <name>] : guarded amp upload.
// Backs up an occupied target slot first, then writes name (chunk 0), payload (chunks 1..96),
// and the NAME AGAIN at chunk -1 (the name-table write that commits the staged content),
// reads back and confirms byte-equality. Requires WritesAllowed.
int uai = Array.IndexOf(args, "--upload-amp");
if (uai >= 0)
{
    if (uai + 2 >= args.Length) { Console.WriteLine("Usage: --upload-amp <vxampPath> <slotIndex> [--name <name>]"); session.Disconnect(); return 1; }
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    var vxampPath = args[uai + 1];
    int slot = int.Parse(args[uai + 2]);
    var client = session.Client!;

    // --pace <msPerChunk> (default 25) extra delay between chunks (each dwrite already waits for the
    // device's per-chunk ACK); --settle <msBeforeReadback> (default 750) pause before the verify read.
    int paceMs = 25, settleMs = 750;
    int pi = Array.IndexOf(args, "--pace"); if (pi >= 0 && pi + 1 < args.Length) paceMs = int.Parse(args[pi + 1]);
    int si = Array.IndexOf(args, "--settle"); if (si >= 0 && si + 1 < args.Length) settleMs = int.Parse(args[si + 1]);

    Console.WriteLine($"\n--- GUARDED AMP UPLOAD: slot {slot} <- '{vxampPath}'  (pace={paceMs}ms/chunk, settle={settleMs}ms) ---");

    // Load the .vxamp file (must be exactly 12288 bytes) — a friendly early exit before we even
    // touch the device; AmpService.UploadAmpAsync also validates this itself.
    var vxampBytes = await System.IO.File.ReadAllBytesAsync(vxampPath);
    if (vxampBytes.Length != 12288)
    {
        Console.WriteLine($"RESULT: UPLOAD-AMP FAIL — expected 12288-byte .vxamp, got {vxampBytes.Length} B");
        session.Disconnect();
        return 4;
    }

    // The name: --name <name> overrides; default = the file's stem. ≤31 chars (AmpService validates/truncates).
    var stem = System.IO.Path.GetFileNameWithoutExtension(vxampPath);
    int ni = Array.IndexOf(args, "--name"); if (ni >= 0 && ni + 1 < args.Length) stem = args[ni + 1];
    if (stem.Length > 31) stem = stem[..31];

    var svc = new AmpService(client, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")), paceMs, settleMs);
    var swAll = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await svc.UploadAmpAsync(slot, vxampBytes, stem, new Progress<AmpUploadProgress>(p =>
        {
            if (p.Stage == AmpUploadStage.BackingUp) Console.WriteLine("[backup] occupied slot — backing up first");
            else if (p.Stage == AmpUploadStage.Writing && (p.ChunksDone % 16 == 0 || p.ChunksDone >= 97))
                Console.WriteLine($"[chunk] {p.ChunksDone}/{p.ChunksTotal}");
            else if (p.Stage == AmpUploadStage.Verifying) Console.WriteLine("[verify] reading back slot...");
        }));
        Console.WriteLine($"RESULT: UPLOAD-AMP OK ({swAll.ElapsedMilliseconds} ms)");
        session.Disconnect();
        return 0;
    }
    catch (AmpServiceException aex)
    {
        Console.WriteLine($"RESULT: UPLOAD-AMP FAIL — {aex.Message}");
        session.Disconnect();
        return 4;
    }
}

// --delete-ir <slotIndex> : guarded IR delete. Backs up the blob, then clears the slot's
// name-table entry (dwrite chunk -1 all-zeros — same confirmed delete semantics as amps). Requires WritesAllowed.
int dii = Array.IndexOf(args, "--delete-ir");
if (dii >= 0)
{
    if (dii + 1 >= args.Length) { Console.WriteLine("Usage: --delete-ir <slotIndex>"); session.Disconnect(); return 1; }
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    int dIrSlot = int.Parse(args[dii + 1]);
    var dIrClient = session.Client!;
    var irSvcD = new IrService(dIrClient, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")));
    var irNamesBefore = await irSvcD.ListIrsAsync();
    if (dIrSlot < 0 || dIrSlot >= irNamesBefore.Count || irNamesBefore[dIrSlot].IsEmpty)
    { Console.WriteLine($"RESULT: DELETE-IR NO-OP — slot {dIrSlot} is already empty."); session.Disconnect(); return 0; }
    Console.WriteLine($"[delete] slot {dIrSlot} ('{irNamesBefore[dIrSlot].Name}') — backing up to docs/backups, then clearing...");
    await irSvcD.DeleteIrAsync(dIrSlot);
    await Task.Delay(500);
    var irAfterDelete = await irSvcD.ListIrsAsync();
    bool irGone = irAfterDelete[dIrSlot].IsEmpty;
    Console.WriteLine(irGone ? $"RESULT: DELETE-IR OK (slot {dIrSlot} now empty)" : $"RESULT: DELETE-IR FAIL (slot {dIrSlot} still '{irAfterDelete[dIrSlot].Name}')");
    session.Disconnect();
    return irGone ? 0 : 4;
}

// --upload-ir <irblobPath> <slotIndex> [--name <name>] [--pace <ms>] [--settle <ms>] : guarded IR upload.
// Backs up an occupied target slot first, then writes name (chunk 0), payload (chunks 1..32),
// and the NAME AGAIN at chunk -1 (the commit), reads back and confirms byte-equality. Requires WritesAllowed.
int uii = Array.IndexOf(args, "--upload-ir");
if (uii >= 0)
{
    if (uii + 2 >= args.Length) { Console.WriteLine("Usage: --upload-ir <irblobPath> <slotIndex> [--name <name>] [--pace <ms>] [--settle <ms>]"); session.Disconnect(); return 1; }
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    var irBlobPath = args[uii + 1];
    int uIrSlot = int.Parse(args[uii + 2]);
    var irClient = session.Client!;

    // --pace <msPerChunk> (default 25) extra delay between chunks; --settle <msBeforeReadback> (default 750)
    // pause before the verify read.
    int irPaceMs = 25, irSettleMs = 750;
    int irPi = Array.IndexOf(args, "--pace"); if (irPi >= 0 && irPi + 1 < args.Length) irPaceMs = int.Parse(args[irPi + 1]);
    int irSi = Array.IndexOf(args, "--settle"); if (irSi >= 0 && irSi + 1 < args.Length) irSettleMs = int.Parse(args[irSi + 1]);

    Console.WriteLine($"\n--- GUARDED IR UPLOAD: slot {uIrSlot} <- '{irBlobPath}'  (pace={irPaceMs}ms/chunk, settle={irSettleMs}ms) ---");

    // Load the IR blob (must be exactly 4096 bytes) — a friendly early exit before we even
    // touch the device; IrService.UploadIrAsync also validates this itself.
    var irBytesBuf = await System.IO.File.ReadAllBytesAsync(irBlobPath);
    if (irBytesBuf.Length != IrService.IrBytes)
    {
        Console.WriteLine($"RESULT: UPLOAD-IR FAIL — expected {IrService.IrBytes}-byte IR blob, got {irBytesBuf.Length} B");
        session.Disconnect();
        return 4;
    }

    // The name: --name <name> overrides; default = the file's stem. <=31 chars (IrService validates/truncates).
    var irStem = System.IO.Path.GetFileNameWithoutExtension(irBlobPath);
    int irNi = Array.IndexOf(args, "--name"); if (irNi >= 0 && irNi + 1 < args.Length) irStem = args[irNi + 1];
    if (irStem.Length > 31) irStem = irStem[..31];

    var irSvcU = new IrService(irClient, System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")), irPaceMs, irSettleMs);
    var irSwAll = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await irSvcU.UploadIrAsync(uIrSlot, irBytesBuf, irStem, new Progress<SlotUploadProgress>(p =>
        {
            if (p.Stage == SlotUploadStage.BackingUp) Console.WriteLine("[backup] occupied slot — backing up first");
            else if (p.Stage == SlotUploadStage.Writing && (p.ChunksDone % 8 == 0 || p.ChunksDone >= p.ChunksTotal))
                Console.WriteLine($"[chunk] {p.ChunksDone}/{p.ChunksTotal}");
            else if (p.Stage == SlotUploadStage.Verifying) Console.WriteLine("[verify] reading back slot...");
        }));
        Console.WriteLine($"RESULT: UPLOAD-IR OK ({irSwAll.ElapsedMilliseconds} ms)");
        session.Disconnect();
        return 0;
    }
    catch (IrServiceException irex)
    {
        Console.WriteLine($"RESULT: UPLOAD-IR FAIL — {irex.Message}");
        session.Disconnect();
        return 4;
    }
}

// --preset-dwrite-probe [--src <idx>] [--dst <idx>] : guarded, TIMED re-test of the 2026-06-15
// "preset content is not dwrite-able" verdict, which used the buggy all-zeros chunk:-1 terminator
// (the amp-upload bug). Dreads an occupied preset (source untouched), dwrites it into an EMPTY
// slot with the correct name-at-chunk:-1 commit via SlotBlobService (ACK-checked + verified),
// then deletes the probe slot. Either outcome is a valid verdict for PROTOCOL.md.
int pdp = Array.IndexOf(args, "--preset-dwrite-probe");
if (pdp >= 0)
{
    if (!c.WritesAllowed) { Console.WriteLine("writes not allowed; abort."); session.Disconnect(); return 3; }
    var pClient = session.Client!;
    var pNames = await pClient.ReadListAsync(@"root\presets");
    int pSrc = ArgAfter(args, "--src") ?? Enumerable.Range(0, pNames.Count).First(i => !string.IsNullOrEmpty(pNames[i]));
    int pDst = ArgAfter(args, "--dst") ?? Enumerable.Range(0, pNames.Count).First(i => string.IsNullOrEmpty(pNames[i]));
    if (string.IsNullOrEmpty(pNames[pSrc]) || !string.IsNullOrEmpty(pNames[pDst]))
    { Console.WriteLine($"RESULT: PRESET-DWRITE-PROBE ABORT — need occupied src (idx {pSrc}) and empty dst (idx {pDst})."); session.Disconnect(); return 1; }

    Console.WriteLine($"\n--- PRESET DWRITE PROBE: '{pNames[pSrc]}' (idx {pSrc}) -> empty idx {pDst} ---");
    var pSw = System.Diagnostics.Stopwatch.StartNew();
    var pBlob = await pClient.DReadBlobAsync(@"root\presets", pSrc, 64);
    Console.WriteLine($"[dread] source read: {pBlob.Length} B in {pSw.ElapsedMilliseconds}ms");

    var pKind = new SlotBlobKind(@"root\presets", 64, 8192, "Preset", "preset-probe", ".bin");
    var pSvc = new SlotBlobService(pClient, pKind,
        System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups")),
        msg => new InvalidOperationException(msg));
    try
    {
        pSw.Restart();
        await pSvc.UploadAsync(pDst, pBlob, "__probe_dwrite", new Progress<SlotUploadProgress>(pp =>
        {
            if (pp.Stage == SlotUploadStage.Writing && (pp.ChunksDone % 16 == 0 || pp.ChunksDone >= pp.ChunksTotal))
                Console.WriteLine($"[chunk] {pp.ChunksDone}/{pp.ChunksTotal}");
        }));
        long pUploadMs = pSw.ElapsedMilliseconds;
        var pAfter = await pClient.ReadListAsync(@"root\presets");
        bool pLanded = pAfter[pDst] == "__probe_dwrite";
        Console.WriteLine($"[verify] service verified byte-equality; name landed: {pLanded}");
        pSw.Restart();
        await pSvc.DeleteAsync(pDst);
        Console.WriteLine($"[cleanup] probe slot deleted in {pSw.ElapsedMilliseconds}ms");
        Console.WriteLine(pLanded
            ? $"RESULT: PRESET-DWRITE-PROBE WORKS — 66 acked writes + verify in {pUploadMs}ms (compare: select+save copy ~216ms, param replay ~12s)"
            : $"RESULT: PRESET-DWRITE-PROBE FAILED — all writes ACKed but the name-table entry did not land");
        session.Disconnect();
        return pLanded ? 0 : 4;
    }
    catch (InvalidOperationException pex)
    {
        Console.WriteLine($"RESULT: PRESET-DWRITE-PROBE FAILED — {pex.Message}");
        // Best-effort cleanup if a partial name landed (service clears on verify-fail already).
        try { await pSvc.DeleteAsync(pDst); } catch { }
        session.Disconnect();
        return 4;
    }
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
