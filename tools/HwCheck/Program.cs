// Plan 2/3a hardware harness — drives the REAL SystemSerialPort end-to-end.
//   dotnet run --project tools/HwCheck                 # read-only: connect/identify/compat/list
//   dotnet run --project tools/HwCheck -- --browse     # read-only dump of root\app (or --browse <path>)
//   dotnet run --project tools/HwCheck -- --dump-amps  # read-only: pull every amp slot's converted .vxamp blob
//   dotnet run --project tools/HwCheck -- --write-test # + guarded duplicate to an empty slot, then delete
//   dotnet run --project tools/HwCheck -- --list-amps  # read-only amp slot name table
//   dotnet run --project tools/HwCheck -- --upload-amp <vxampPath> <slotIndex> [--name <n>]  # guarded amp upload (backup+write+verify)
//   dotnet run --project tools/HwCheck -- --delete-amp <slotIndex>              # guarded amp delete (backup+clear name)
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

// --list-amps : read-only, prints the amp slot name table (fast; no blob reads).
if (Array.IndexOf(args, "--list-amps") >= 0)
{
    var ampNames = await session.Client!.ReadListAsync(@"root\amp");
    Console.WriteLine($"\n--- AMP SLOTS ({ampNames.Count}) ---");
    for (int idx = 0; idx < ampNames.Count; idx++)
        Console.WriteLine($"  slot {idx + 1,2} (idx {idx,2}): {(string.IsNullOrEmpty(ampNames[idx]) ? "(empty)" : $"'{ampNames[idx]}'")}");
    Console.WriteLine("RESULT: LIST-AMPS COMPLETE");
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
    var dnames = await dclient.ReadListAsync(@"root\amp");
    if (dslot < 0 || dslot >= dnames.Count || string.IsNullOrEmpty(dnames[dslot]))
    {
        Console.WriteLine($"RESULT: DELETE-AMP NO-OP — slot {dslot} is already empty.");
        session.Disconnect();
        return 0;
    }
    var ddir = System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups"));
    System.IO.Directory.CreateDirectory(ddir);
    var dpath = System.IO.Path.Combine(ddir, $"amp-{dslot}-{DateTime.Now:yyyyMMdd-HHmmss}-deleted.vxamp");
    Console.WriteLine($"[backup] slot {dslot} ('{dnames[dslot]}') -> {dpath}");
    var dblob = await dclient.DReadBlobAsync(@"root\amp", dslot, 96);
    await System.IO.File.WriteAllBytesAsync(dpath, dblob);
    Console.WriteLine($"[backup] {dblob.Length} B saved; deleting name-table entry...");
    await dclient.DWriteChunkAsync(@"root\amp", dslot, -1, new byte[128]);
    await Task.Delay(500);
    var dafter = await dclient.ReadListAsync(@"root\amp");
    bool gone = dslot < dafter.Count && string.IsNullOrEmpty(dafter[dslot]);
    Console.WriteLine(gone ? $"RESULT: DELETE-AMP OK (slot {dslot} now empty)" : $"RESULT: DELETE-AMP FAIL (slot {dslot} still '{dafter[dslot]}')");
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

    // 1. Back up the current slot before touching it — but ONLY if it is occupied. Skipping the
    // backup for empty slots is not just an optimization: a 96-chunk dread of the target slot on
    // the same connection right before the dwrite burst is the main suspect for the commit being
    // silently discarded (VoidX's working upload flow has no preceding dread).
    var bdir = System.IO.Path.GetFullPath(System.IO.Path.Combine("docs", "backups"));
    System.IO.Directory.CreateDirectory(bdir);
    var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var preNames = await client.ReadListAsync(@"root\amp");
    bool slotOccupied = slot >= 0 && slot < preNames.Count && !string.IsNullOrEmpty(preNames[slot]);
    var currentBlob = Array.Empty<byte>();
    if (slotOccupied)
    {
        var backupPath = System.IO.Path.Combine(bdir, $"amp-{slot}-{ts}.vxamp");
        Console.WriteLine($"[backup] reading current amp slot {slot} ('{preNames[slot]}')...");
        currentBlob = await client.DReadBlobAsync(@"root\amp", slot, 96);
        await System.IO.File.WriteAllBytesAsync(backupPath, currentBlob);
        Console.WriteLine($"[backup] slot {slot} -> {backupPath} ({currentBlob.Length} B)");
    }
    else
    {
        Console.WriteLine($"[backup] slot {slot} is empty per the amp name table — no backup needed (and no pre-write dread).");
    }

    // 2. Load the .vxamp file (must be exactly 12288 bytes)
    var vxampBytes = await System.IO.File.ReadAllBytesAsync(vxampPath);
    if (vxampBytes.Length != 12288)
    {
        Console.WriteLine($"RESULT: UPLOAD-AMP FAIL — expected 12288-byte .vxamp, got {vxampBytes.Length} B");
        session.Disconnect();
        return 4;
    }

    // 3. The name: --name <name> overrides; default = the file's stem. ≤31 chars, zero-padded to 128 B.
    var stem = System.IO.Path.GetFileNameWithoutExtension(vxampPath);
    int ni = Array.IndexOf(args, "--name"); if (ni >= 0 && ni + 1 < args.Length) stem = args[ni + 1];
    if (stem.Length > 31) stem = stem[..31];
    var nameBuf = new byte[128];
    var nameBytes = System.Text.Encoding.ASCII.GetBytes(stem);
    Array.Copy(nameBytes, nameBuf, Math.Min(nameBytes.Length, 128));

    // The device ACKs every accepted chunk with `dwrite root\amp:{"index":N,"chunk":<nextExpected>}`
    // (SendAsync's NUL-stop naturally waits for it). Verify each ACK and abort on any mismatch.
    var swChunk = new System.Diagnostics.Stopwatch();
    async Task<bool> WriteChunkAcked(int chk, byte[] data, int expectNext)
    {
        swChunk.Restart();
        var raw = await client.DWriteChunkAsync(@"root\amp", slot, chk, data);
        var m = System.Text.RegularExpressions.Regex.Match(raw, "\"chunk\":(-?\\d+)}");
        bool ok = m.Success && int.Parse(m.Groups[1].Value) == expectNext;
        if (!ok || chk % 16 == 0 || chk >= 96 || chk <= 0)
            Console.WriteLine($"[chunk {chk,3}] {swChunk.ElapsedMilliseconds,4}ms  ack={(m.Success ? m.Groups[1].Value : "NONE")} (expected {expectNext}){(ok ? "" : "  <-- MISMATCH, aborting")}");
        if (paceMs > 0) await Task.Delay(paceMs);
        return ok;
    }

    // 4. The upload sequence (verified live 2026-07-03, fw 2.5.1, serial): chunk 0 = name,
    // chunks 1..96 = payload, then chunk -1 = NAME AGAIN — the name-table write that COMMITS the
    // staged content. NOTE: the VoidX capture sent all-zeros at chunk -1; on this firmware over
    // serial that verifiably DELETES the slot's name-table entry and the staged content is
    // discarded (that was the whole bug). chunk -1 must carry the padded name.
    Console.WriteLine($"[upload] chunk 0 (name '{stem}') + chunks 1..96 (payload) + chunk -1 (name = commit)");
    bool acked = await WriteChunkAcked(0, nameBuf, 1);
    var chunk128 = new byte[128];
    for (int chk = 1; acked && chk <= 96; chk++)
    {
        Array.Copy(vxampBytes, (chk - 1) * 128, chunk128, 0, 128);
        acked = await WriteChunkAcked(chk, chunk128, chk < 96 ? chk + 1 : -1);
    }
    if (acked) acked = await WriteChunkAcked(-1, nameBuf, -1);
    if (!acked)
    {
        Console.WriteLine("RESULT: UPLOAD-AMP FAIL — device ACK missing/mismatched (see last [chunk] line)");
        session.Disconnect();
        return 4;
    }

    // 6. Let the device settle (flash commit) before reading back
    if (settleMs > 0) { Console.WriteLine($"[verify] settling {settleMs}ms before readback..."); await Task.Delay(settleMs); }
    Console.WriteLine("[verify] reading back slot...");
    var readBack = await client.DReadBlobAsync(@"root\amp", slot, 96);
    bool ok = readBack.AsSpan().SequenceEqual(vxampBytes.AsSpan());
    if (!ok)
    {
        // --- DIAGNOSTICS: characterize the failure mode (length-safe: readback may be short) ---
        try
        {
            int n = Math.Min(readBack.Length, vxampBytes.Length);
            int matchBytes = 0, firstDiff = -1, matchChunks = 0;
            bool readAllZero = readBack.Length > 0;
            for (int i = 0; i < readBack.Length; i++) if (readBack[i] != 0) { readAllZero = false; break; }
            for (int i = 0; i < n; i++)
            {
                if (readBack[i] == vxampBytes[i]) matchBytes++;
                else if (firstDiff < 0) firstDiff = i;
            }
            int fullChunks = readBack.Length / 128;
            for (int chk = 0; chk < 96 && (chk + 1) * 128 <= readBack.Length; chk++)
            {
                bool cok = true;
                for (int j = 0; j < 128; j++) if (readBack[chk * 128 + j] != vxampBytes[chk * 128 + j]) { cok = false; break; }
                if (cok) matchChunks++;
            }
            bool equalsBackup = readBack.Length == currentBlob.Length && readBack.AsSpan().SequenceEqual(currentBlob.AsSpan());
            Console.WriteLine($"[diag] readback length = {readBack.Length} B (expected 12288)   |   backup read = {currentBlob.Length} B");
            Console.WriteLine($"[diag] {matchBytes}/{n} compared bytes and {matchChunks}/{Math.Min(96, fullChunks)} full chunks match intended payload");
            Console.WriteLine($"[diag] readback all-zero? {readAllZero}   |   equals pre-write backup (write had NO effect)? {equalsBackup}");
            Console.WriteLine($"[diag] first differing byte (within compared range): offset {firstDiff}" + (firstDiff >= 0 ? $" (chunk {firstDiff / 128 + 1}); intended=0x{vxampBytes[firstDiff]:X2} readback=0x{readBack[firstDiff]:X2}" : ""));
            try
            {
                var names = await client.ReadListAsync(@"root\amp");
                string slotName = slot >= 0 && slot < names.Count ? names[slot] : "(out of range)";
                Console.WriteLine($"[diag] amp slot {slot} name after write: '{slotName}'  (attempted name: '{stem}')");
            }
            catch (Exception nex) { Console.WriteLine($"[diag] amp name-list read failed: {nex.Message}"); }
            var rbPath = System.IO.Path.Combine(bdir, $"amp-{slot}-{ts}-readback.bin");
            await System.IO.File.WriteAllBytesAsync(rbPath, readBack);
            Console.WriteLine($"[diag] readback saved -> {rbPath} ({readBack.Length} B)");
        }
        catch (Exception dex) { Console.WriteLine($"[diag] diagnostics error: {dex.Message}"); }
    }
    Console.WriteLine(ok ? "RESULT: UPLOAD-AMP OK" : "RESULT: UPLOAD-AMP FAIL — readback mismatch");
    session.Disconnect();
    return ok ? 0 : 4;
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
