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
// Requires VoidX-Control CLOSED (it holds COM6).
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
var providers = new List<ILinkProvider>
{
    new SerialLinkProvider(() => new SystemSerialPort(), options, portNames),
};
var checker = new CompatibilityChecker(FirmwareCatalog.Default);

Console.WriteLine("Connecting (USB serial, auto-discover) ...");
using var session = new DeviceSession(providers, checker);
var state = await session.ConnectAsync();
if (!state.Connected)
{
    Console.WriteLine("RESULT: NOT CONNECTED — no StompStation answered on any COM port.");
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
