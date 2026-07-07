using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Distill;
using Xunit;

public class AmpListViewModelTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"amp-vm-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }

    /// <summary>A realistic device slot: constant vxamp header, fill-byte body, ZERO padding
    /// (like every real VoidX-written slot). The metadata-save integrity guards reject blobs
    /// without the header, so fixtures must carry it.</summary>
    private static byte[] RealisticBlob(byte fill)
    {
        var blob = Enumerable.Repeat(fill, 12288).ToArray();
        VxampFormat.HeaderBytes.CopyTo(blob, 0);
        Array.Clear(blob, VxampMetadata.Offset, 12288 - VxampMetadata.Offset);
        return blob;
    }

    private (AmpListViewModel vm, FakeAmpDevice dev) Make(bool writes = true)
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", RealisticBlob(1));
        dev.SeedAmp(1, "Crunch", RealisticBlob(2));
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new AmpListViewModel(svc, writes), dev);
    }

    [Fact]
    public async Task Refresh_loads_30_items()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Items.Count);
        Assert.Equal("Clean", vm.Items[0].Name);
        Assert.Equal(1, vm.Items[0].DisplaySlot);
        Assert.True(vm.Items[5].IsEmpty);
    }

    [Fact]
    public async Task Delete_selected_clears_slot_and_reloads()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(dev.SlotNames[1]);
        Assert.True(vm.Items[1].IsEmpty);
    }

    [Fact]
    public async Task Write_op_drains_an_in_flight_details_read()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];          // starts an in-flight details read — not awaited
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(dev.SlotNames[0]);
        Assert.True(vm.Items[0].IsEmpty);
    }

    [Fact]
    public async Task Delete_is_gated_when_writes_not_allowed()
    {
        var (vm, dev) = Make(writes: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Clean", dev.SlotNames[0]);            // untouched
    }

    [Fact]
    public async Task CommitRename_renames_and_reloads()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Cleaner";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("Cleaner", dev.SlotNames[0]);
        Assert.Equal("Cleaner", vm.Items[0].Name);
    }

    [Fact]
    public async Task Service_error_lands_in_ErrorMessage_not_a_crash()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "naïve";                            // non-ASCII -> AmpServiceException
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("ASCII", vm.ErrorMessage);
    }

    // ---- upload flow (Task 6) ----

    private sealed record UploadHarness(AmpListViewModel Vm, FakeAmpDevice Dev, List<string> DistillCalls, string DistilledDir);

    private UploadHarness MakeUpload(
        AmpListViewModel.DistillRunner? distill = null, bool writes = true, int seedCount = 2)
    {
        var dev = new FakeAmpDevice();
        for (int i = 0; i < seedCount; i++)
            dev.SeedAmp(i, $"Amp{i}", Enumerable.Repeat((byte)(i + 1), 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var calls = new List<string>();
        var distilledDir = Path.Combine(Path.GetTempPath(), $"distilled-{Guid.NewGuid():N}");
        AmpListViewModel.DistillRunner runner = distill ?? ((nam, outPath, p, ct) =>
        {
            calls.Add($"{nam}|{outPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, Enumerable.Repeat((byte)0xD1, 12288).ToArray());
            return Task.FromResult(0.25);
        });
        var vm = new AmpListViewModel(svc, writes, runner, distilledDir, dispatch: a => a());
        return new UploadHarness(vm, dev, calls, distilledDir);
    }

    private static string TempFile(string name)
    {
        var p = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(p, Enumerable.Repeat((byte)0xEE, 12288).ToArray());
        return p;
    }

    /// <summary>Seed a slot whose blob carries an SSMD block (on a realistic slot layout —
    /// valid header, so the save-path integrity guards accept it).</summary>
    private static byte[] BlobWithMeta(AmpMetadata meta, byte fill = 3)
    {
        var blob = RealisticBlob(fill);
        VxampMetadata.Write(blob, meta);
        return blob;
    }

    private static int DreadCount(FakeAmpDevice dev, int index) =>
        dev.CommandLog.Count(c => c.StartsWith($"dread root\\amp:{{\"index\":{index},"));

    [Fact]
    public async Task BeginUpload_prefills_name_and_empty_slots()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile("My Very Long Amp Model Name Overflowing.nam");
        h.Vm.BeginUploadCommand.Execute(nam);
        Assert.True(h.Vm.IsUploadPanelOpen);
        Assert.Equal(31, h.Vm.UploadName.Length);           // stem truncated to the device cap
        Assert.Equal(28, h.Vm.EmptySlots.Count);            // 30 - 2 seeded
        Assert.Equal(2, h.Vm.SelectedEmptySlot);            // first empty index
    }

    [Fact]
    public async Task BeginUpload_with_no_empty_slots_blocks_with_message()
    {
        var h = MakeUpload(seedCount: 30);
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("x.nam"));
        Assert.False(h.Vm.IsUploadPanelOpen);
        Assert.NotNull(h.Vm.UploadBlockedMessage);
    }

    [Fact]
    public async Task StartUpload_nam_distills_to_library_then_uploads_and_selects()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Plexi.nam"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Single(h.DistillCalls);
        Assert.EndsWith(Path.Combine(h.DistilledDir, "Plexi.vxamp"), h.DistillCalls[0].Split('|')[1]);
        Assert.Equal("Plexi", h.Dev.SlotNames[2]);          // first empty slot
        Assert.Equal(0xD1, h.Dev.SlotBlobs[2]![0]);         // distilled bytes, not source bytes
        Assert.True(h.Vm.IsUploadPanelOpen);                // stays open to show the Done state
        Assert.StartsWith("Done", h.Vm.UploadStatus);
        Assert.Equal(2, h.Vm.Selected?.Index);              // new amp selected
        Assert.Null(h.Vm.UploadError);
    }

    [Fact]
    public async Task StartUpload_vxamp_skips_distillation()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Backup.vxamp"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Empty(h.DistillCalls);
        Assert.Equal("Backup", h.Dev.SlotNames[2]);
        Assert.Equal(0xEE, h.Dev.SlotBlobs[2]![0]);         // the file's own bytes
    }

    [Fact]
    public async Task StartUpload_rejects_duplicate_name()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Amp0.nam"));   // collides with seeded slot 0
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.NotNull(h.Vm.UploadError);
        Assert.Empty(h.DistillCalls);                        // failed before any work
        Assert.Null(h.Dev.SlotNames[2]);
    }

    [Fact]
    public async Task Distill_failure_surfaces_in_UploadError_and_device_is_untouched()
    {
        var h = MakeUpload(distill: (n, o, p, ct) =>
            throw new Sonulab.Distill.DistillException("numeric explosion"));
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Bad.nam"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Contains("numeric explosion", h.Vm.UploadError);
        Assert.True(h.Vm.IsUploadPanelOpen);                 // stays open to show the error
        Assert.Null(h.Dev.SlotNames[2]);
        Assert.DoesNotContain(h.Dev.CommandLog, c => c.StartsWith("dwrite"));
    }

    [Fact]
    public async Task Cancel_during_distill_cancels_cleanly()
    {
        var tcs = new TaskCompletionSource();
        var h = MakeUpload(distill: async (n, o, p, ct) =>
        {
            tcs.SetResult();                                 // signal: distill has started
            await Task.Delay(Timeout.Infinite, ct);          // parks until cancelled
            return 0.0;
        });
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Slow.nam"));
        var run = h.Vm.StartUploadCommand.ExecuteAsync(null);
        await tcs.Task;
        Assert.True(h.Vm.CanCancelUpload);                   // cancellable while distilling
        h.Vm.CancelUploadCommand.Execute(null);
        await run;
        Assert.Contains("ancel", h.Vm.UploadError);          // "Cancelled."
        Assert.False(h.Vm.IsUploading);
        Assert.Null(h.Dev.SlotNames[2]);
    }

    [Fact]
    public async Task List_ops_are_gated_while_uploading()
    {
        var tcs = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var h = MakeUpload(distill: async (n, o, p, ct) =>
        {
            tcs.SetResult();
            await release.Task;
            Directory.CreateDirectory(Path.GetDirectoryName(o)!);
            File.WriteAllBytes(o, Enumerable.Repeat((byte)0xD1, 12288).ToArray());
            return 0.0;
        });
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Slow2.nam"));
        var run = h.Vm.StartUploadCommand.ExecuteAsync(null);
        await tcs.Task;
        Assert.False(h.Vm.CanMutate);
        Assert.False(h.Vm.CanRefresh);
        h.Vm.Selected = h.Vm.Items[0];
        await h.Vm.DeleteCommand.ExecuteAsync(null);          // must no-op
        Assert.Equal("Amp0", h.Dev.SlotNames[0]);
        release.SetResult();
        await run;
        Assert.True(h.Vm.CanMutate);
    }

    // ---- SSMD metadata stamping (Task 3) ----

    [Fact]
    public async Task Upload_nam_stamps_ssmd_metadata()
    {
        var h = MakeUpload();                              // fake distiller returns ShapeErr 0.25
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = Path.Combine(Path.GetTempPath(), $"Tweed Deluxe-{Guid.NewGuid():N}.nam");
        File.WriteAllText(nam, """{"architecture":"WaveNet","metadata":{"name":"Tweed Deluxe","modeled_by":"ed"}}""");
        try
        {
            h.Vm.BeginUploadCommand.Execute(nam);
            h.Vm.UploadName = "Tweed";
            h.Vm.UploadNotes = "bright, edge of breakup";
            h.Vm.UploadUrl = "https://tonehunt.org/x";
            await h.Vm.StartUploadCommand.ExecuteAsync(null);

            Assert.Null(h.Vm.UploadError);
            int slot = h.Vm.Items.First(i => i.Name == "Tweed").Index;
            var meta = VxampMetadata.TryRead(h.Dev.SlotBlobs[slot]!);
            Assert.NotNull(meta);
            Assert.Equal(Path.GetFileName(nam), meta!.Source!.File);
            Assert.Equal(new FileInfo(nam).Length, meta.Source.Size);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nam))), meta.Source.Sha256);
            Assert.NotNull(meta.Uploaded);
            Assert.Equal("ed", (string?)meta.Nam!["modeled_by"]);
            Assert.Equal(0.25, meta.Distill!.ShapeErr!.Value, 12);
            Assert.NotNull(meta.Distill.Version);
            Assert.Equal("bright, edge of breakup", meta.Notes);
            Assert.Equal("https://tonehunt.org/x", meta.Url);
        }
        finally { File.Delete(nam); }
    }

    [Fact]
    public async Task Upload_nam_persists_stamped_bytes_to_the_distilled_file()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile($"Persist-{Guid.NewGuid():N}.nam");
        try
        {
            h.Vm.BeginUploadCommand.Execute(nam);
            h.Vm.UploadName = "Persist";
            h.Vm.UploadNotes = "note";
            await h.Vm.StartUploadCommand.ExecuteAsync(null);
            var onDisk = File.ReadAllBytes(Path.Combine(h.DistilledDir, "Persist.vxamp"));
            Assert.Equal("note", VxampMetadata.TryRead(onDisk)!.Notes);
        }
        finally { File.Delete(nam); }
    }

    [Fact]
    public async Task Upload_vxamp_preserves_existing_block_and_overlays_user_fields()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var existing = new byte[12288];
        VxampMetadata.Write(existing, new AmpMetadata(
            Nam: new System.Text.Json.Nodes.JsonObject { ["name"] = "orig" },
            Notes: "old notes", Url: "https://old"));
        var vx = Path.Combine(Path.GetTempPath(), $"pre-{Guid.NewGuid():N}.vxamp");
        File.WriteAllBytes(vx, existing);
        try
        {
            h.Vm.BeginUploadCommand.Execute(vx);
            Assert.Equal("old notes", h.Vm.UploadNotes);       // prefilled from the block
            Assert.Equal("https://old", h.Vm.UploadUrl);
            h.Vm.UploadName = "Pre";
            h.Vm.UploadUrl = "https://new";                    // user overwrites one field
            await h.Vm.StartUploadCommand.ExecuteAsync(null);
            int slot = h.Vm.Items.First(i => i.Name == "Pre").Index;
            var meta = VxampMetadata.TryRead(h.Dev.SlotBlobs[slot]!)!;
            Assert.Equal("orig", (string?)meta.Nam!["name"]);  // passthrough kept
            Assert.Equal("old notes", meta.Notes);
            Assert.Equal("https://new", meta.Url);
            Assert.Equal(Path.GetFileName(vx), meta.Source!.File);
            Assert.NotNull(meta.Uploaded);
        }
        finally { File.Delete(vx); }
    }

    [Fact]
    public async Task Upload_payload_bytes_reach_the_device_unchanged()
    {
        var h = MakeUpload();                              // fake distiller writes 0xD1 * 12288
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile($"Payload-{Guid.NewGuid():N}.nam");
        try
        {
            h.Vm.BeginUploadCommand.Execute(nam);
            h.Vm.UploadName = "Payload";
            h.Vm.UploadNotes = "anything";
            await h.Vm.StartUploadCommand.ExecuteAsync(null);
            int slot = h.Vm.Items.First(i => i.Name == "Payload").Index;
            Assert.All(h.Dev.SlotBlobs[slot]![..VxampMetadata.Offset], b => Assert.Equal(0xD1, b));
        }
        finally { File.Delete(nam); }
    }

    [Fact]
    public void NotesBudgetWarning_appears_when_metadata_would_truncate()
    {
        var h = MakeUpload();
        h.Vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var nam = TempFile($"Budget-{Guid.NewGuid():N}.nam");
        try
        {
            h.Vm.BeginUploadCommand.Execute(nam);
            Assert.Null(h.Vm.NotesBudgetWarning);
            h.Vm.UploadNotes = new string('a', 4500);
            Assert.NotNull(h.Vm.NotesBudgetWarning);
        }
        finally { File.Delete(nam); }
    }

    // ---- Tone3000 handoff seam (spec 2026-07-07 §3) ----

    [Fact]
    public async Task BeginUploadPrefilled_lands_notes_and_url_in_the_panel()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile($"T3k-{Guid.NewGuid():N}.nam");
        try
        {
            h.Vm.BeginUploadPrefilled(nam, "65 Deluxe Reverb by fabiossousa (Tone3000)",
                                      "https://www.tone3000.com/tones/42");
            Assert.True(h.Vm.IsUploadPanelOpen);
            Assert.Equal("65 Deluxe Reverb by fabiossousa (Tone3000)", h.Vm.UploadNotes);
            Assert.Equal("https://www.tone3000.com/tones/42", h.Vm.UploadUrl);
        }
        finally { File.Delete(nam); }
    }

    [Fact]
    public async Task BeginUploadPrefilled_with_nulls_matches_plain_BeginUpload()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile($"Plain-{Guid.NewGuid():N}.nam");
        try
        {
            h.Vm.BeginUploadPrefilled(nam, null, null);
            Assert.True(h.Vm.IsUploadPanelOpen);
            Assert.Equal("", h.Vm.UploadNotes);              // exactly today's cleared state
            Assert.Equal("", h.Vm.UploadUrl);
        }
        finally { File.Delete(nam); }
    }

    // ---- details pane (Task 4) ----

    [Fact]
    public async Task Selecting_an_amp_loads_its_metadata()
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("Clean.nam", 1000, "2026-01-01T00:00:00Z", "aa"),
            Notes: "hi", Url: "https://x")));
        dev.OpenAsync().GetAwaiter().GetResult();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;

        Assert.True(vm.IsDetailsVisible);
        Assert.False(vm.ShowNoMetadata);
        Assert.Equal("hi", vm.DetailsNotes);
        Assert.Equal("https://x", vm.DetailsUrl);
        Assert.Contains(vm.DetailsFields, f => f.Label == "Source file" && f.Value == "Clean.nam");
    }

    [Fact]
    public async Task Slot_without_block_shows_no_metadata_state()
    {
        var (vm, _) = Make();                               // seeded blobs have no SSMD block
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.True(vm.IsDetailsVisible);
        Assert.True(vm.ShowNoMetadata);
        Assert.Empty(vm.DetailsFields);
    }

    [Fact]
    public async Task Selecting_an_empty_slot_hides_the_pane()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[5];                          // empty
        if (vm.DetailsLoadTask is not null) await vm.DetailsLoadTask;
        Assert.False(vm.IsDetailsVisible);
    }

    [Fact]
    public async Task Reselecting_hits_the_cache_not_the_device()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(1, DreadCount(dev, 0));                // one region probe (no block: single chunk)
        vm.Selected = vm.Items[1];
        await vm.DetailsLoadTask!;
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(1, DreadCount(dev, 0));                // still one — cache hit
    }

    [Fact]
    public async Task Rename_invalidates_the_details_cache()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(1, DreadCount(dev, 0));

        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Cleaner";
        await vm.CommitRenameCommand.ExecuteAsync(item);    // RunAsync -> ReloadAsync clears cache

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(2, DreadCount(dev, 0));                // re-read after invalidation
    }

    [Fact]
    public async Task Refresh_invalidates_the_details_cache()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(1, DreadCount(dev, 0));

        await vm.RefreshCommand.ExecuteAsync(null);         // name unchanged ("Clean") but cache cleared

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal(2, DreadCount(dev, 0));                // re-read after invalidation
    }

    // ---- region-only read: prove the dread counts (spec 2026-07-07) ----

    [Fact]
    public async Task Details_read_of_an_amp_with_metadata_fetches_only_the_block_chunks()
    {
        var dev = new FakeAmpDevice();
        var blob = BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("a.nam", 1000, "2026-01-01T00:00:00Z", "aa"),
            Notes: "hello"));
        dev.SeedAmp(0, "A", blob);
        await dev.OpenAsync();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;

        Assert.Equal("hello", vm.DetailsNotes);              // metadata fully loaded...
        int blockLen = VxampMetadata.BlockLength(blob.AsSpan(VxampMetadata.Offset))!.Value;
        int expected = 1 + (VxampMetadata.LastRegionChunk(blockLen) - VxampMetadata.FirstRegionChunk);
        Assert.Equal(expected, DreadCount(dev, 0));          // ...from exactly the block's chunks
        Assert.True(expected < 8, $"small block should span few chunks, got {expected}");
    }

    [Fact]
    public async Task Details_read_of_a_no_metadata_amp_is_a_single_chunk()
    {
        var (vm, dev) = Make();                              // RealisticBlob seeds: no SSMD block
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.True(vm.ShowNoMetadata);
        Assert.Equal(1, DreadCount(dev, 0));
    }

    // ---- edit notes/URL on the pedal (Task 5) ----

    [Fact]
    public async Task Edit_metadata_rewrites_padding_only_and_preserves_other_fields()
    {
        var dev = new FakeAmpDevice();
        var original = BlobWithMeta(new AmpMetadata(
            Nam: new System.Text.Json.Nodes.JsonObject { ["name"] = "keepme" },
            Uploaded: "2026-01-01T00:00:00Z", Notes: "old", Url: "https://old"), fill: 9);
        dev.SeedAmp(0, "Clean", original);
        dev.OpenAsync().GetAwaiter().GetResult();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        vm.BeginEditMetadataCommand.Execute(null);
        Assert.Equal("old", vm.EditNotes);                  // prefilled
        vm.EditNotes = "new notes";
        vm.EditUrl = "https://new";
        await vm.SaveMetadataCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        var blob = dev.SlotBlobs[0]!;
        Assert.Equal(original[..VxampMetadata.Offset], blob[..VxampMetadata.Offset]);   // DSP untouched
        var meta = VxampMetadata.TryRead(blob)!;
        Assert.Equal("new notes", meta.Notes);
        Assert.Equal("https://new", meta.Url);
        Assert.Equal("keepme", (string?)meta.Nam!["name"]); // preserved
        Assert.Equal("2026-01-01T00:00:00Z", meta.Uploaded); // NOT re-stamped on edit
        Assert.False(vm.IsEditingMetadata);
        Assert.Equal("new notes", vm.DetailsNotes);          // pane refreshed
    }

    [Fact]
    public async Task Edit_creates_a_block_on_a_voidx_era_slot()
    {
        var (vm, dev) = Make();                              // no SSMD blocks in seeds
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditNotes = "annotated later";
        await vm.SaveMetadataCommand.ExecuteAsync(null);
        Assert.Equal("annotated later", VxampMetadata.TryRead(dev.SlotBlobs[0]!)!.Notes);
        // payload preserved (header + fill body, exactly as seeded):
        Assert.Equal(RealisticBlob(1)[..VxampMetadata.Offset], dev.SlotBlobs[0]![..VxampMetadata.Offset]);
    }

    [Fact]
    public async Task Edit_is_gated_when_writes_not_allowed()
    {
        var (vm, dev) = Make(writes: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditNotes = "x";
        await vm.SaveMetadataCommand.ExecuteAsync(null);
        Assert.Null(VxampMetadata.TryRead(dev.SlotBlobs[0]!));   // device untouched
    }

    [Fact]
    public async Task Save_with_oversized_url_sets_error_not_crash()
    {
        var dev = new FakeAmpDevice();
        var original = BlobWithMeta(new AmpMetadata(Notes: "old", Url: "https://old"));
        dev.SeedAmp(0, "Clean", original);
        dev.OpenAsync().GetAwaiter().GetResult();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditUrl = "https://" + new string('x', 5000);   // never trimmed by the codec
        await vm.SaveMetadataCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        var meta = VxampMetadata.TryRead(dev.SlotBlobs[0]!)!;
        Assert.Equal("https://old", meta.Url);
        Assert.Equal("old", meta.Notes);
    }

    [Fact]
    public async Task Changing_selection_cancels_an_open_metadata_edit()
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "AmpA", BlobWithMeta(new AmpMetadata(Notes: "A notes", Url: "https://a")));
        dev.SeedAmp(1, "AmpB", BlobWithMeta(new AmpMetadata(Notes: "B notes", Url: "https://b")));
        dev.OpenAsync().GetAwaiter().GetResult();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditNotes = "for A";

        vm.Selected = vm.Items[1];                          // click amp B while A's edit is open
        await vm.DetailsLoadTask!;

        Assert.False(vm.IsEditingMetadata);                 // edit was cancelled by the selection change

        await vm.SaveMetadataCommand.ExecuteAsync(null);     // stale programmatic save: must no-op

        var metaB = VxampMetadata.TryRead(dev.SlotBlobs[1]!)!;
        Assert.Equal("B notes", metaB.Notes);                // B's own metadata, untouched
        Assert.Equal("https://b", metaB.Url);
    }

    // ---- slot-26 incident (2026-07-06): a glitched details read must never become the
    // merge base or payload source of a metadata save ----

    /// <summary>Fake whose FIRST dread of a given chunk returns valid-hex-but-wrong bytes
    /// (all zeros), simulating a serial glitch. Later reads are clean — like the real
    /// incident, where the save-time backup read was fine but the cached read was not.</summary>
    private sealed class GlitchOnceAmpDevice : FakeAmpDevice
    {
        public int GlitchChunk { get; set; } = 65;   // covers bytes 8192..8319: payload tail + SSMD header
        private bool _glitched;
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            var raw = await base.SendAsync(command, ct);
            if (!_glitched && command.StartsWith("dread") && command.Contains($"\"chunk\":{GlitchChunk}}}"))
            {
                _glitched = true;
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "\"value\":\"[0-9a-fA-F]*\"", "\"value\":\"" + new string('0', 256) + "\"");
            }
            return raw;
        }
    }

    [Fact]
    public async Task Save_merges_against_fresh_device_read_not_a_poisoned_cache()
    {
        var dev = new GlitchOnceAmpDevice();
        var full = BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("Vibroverb.nam", 295018, "2026-07-06T12:27:46Z", "ae55"),
            Uploaded: "2026-07-06T21:19:43Z",
            Nam: new JsonObject { ["name"] = "VibroverbJMDefault" },
            Distill: new AmpDistillInfo("1.0.0", 0.287)), fill: 7);
        dev.SeedAmp(0, "Vibroverb", full);
        await dev.OpenAsync();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.True(vm.ShowNoMetadata);                     // precondition: the glitched read poisoned the pane

        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditNotes = "bright, edge of breakup";
        await vm.SaveMetadataCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);

        var blob = dev.SlotBlobs[0]!;
        Assert.Equal(full[..VxampMetadata.Offset], blob[..VxampMetadata.Offset]);   // DSP payload never corrupted
        var meta = VxampMetadata.TryRead(blob)!;
        Assert.Equal("bright, edge of breakup", meta.Notes);
        Assert.Equal("Vibroverb.nam", meta.Source?.File);    // pre-existing metadata survives the save
        Assert.Equal("VibroverbJMDefault", (string?)meta.Nam?["name"]);
        Assert.Equal("2026-07-06T21:19:43Z", meta.Uploaded);
        Assert.NotNull(meta.Distill);
    }

    /// <summary>Fake that corrupts the Nth dread of a given chunk (1-based), leaving earlier
    /// reads clean — lets a test poison the SAVE-TIME fresh read while the details read is fine.</summary>
    private sealed class GlitchNthAmpDevice : FakeAmpDevice
    {
        public int GlitchChunk { get; set; } = 65;
        public int GlitchOnOccurrence { get; set; } = 2;
        private int _seen;
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            var raw = await base.SendAsync(command, ct);
            if (command.StartsWith("dread") && command.Contains($"\"chunk\":{GlitchChunk}}}")
                && ++_seen == GlitchOnOccurrence)
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "\"value\":\"[0-9a-fA-F]*\"", "\"value\":\"" + new string('0', 256) + "\"");
            return raw;
        }
    }

    [Fact]
    public async Task Save_aborts_when_the_fresh_read_itself_looks_corrupt()
    {
        var dev = new GlitchNthAmpDevice();                  // details read clean; SAVE-time read corrupt
        var full = BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("Vibroverb.nam", 295018, "2026-07-06T12:27:46Z", "ae55"),
            Notes: "keep me"), fill: 7);
        dev.SeedAmp(0, "Vibroverb", full);
        await dev.OpenAsync();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.Equal("keep me", vm.DetailsNotes);            // clean details read

        vm.BeginEditMetadataCommand.Execute(null);
        vm.EditNotes = "new notes";
        await vm.SaveMetadataCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);                     // save refused, loudly
        Assert.Equal(full, dev.SlotBlobs[0]);                // device slot completely untouched
    }

    /// <summary>Fake that blocks dreads of one slot index until released — lets a test observe
    /// the pane state while a details read is genuinely in flight.</summary>
    private sealed class GatedAmpDevice : FakeAmpDevice
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GateIndex { get; set; } = -1;
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (GateIndex >= 0 && command.StartsWith("dread") && command.Contains($"\"index\":{GateIndex},"))
                await Gate.Task.WaitAsync(ct);
            return await base.SendAsync(command, ct);
        }
    }

    [Fact]
    public async Task Selection_change_clears_previous_details_immediately()
    {
        var dev = new GatedAmpDevice();
        dev.SeedAmp(0, "A", BlobWithMeta(new AmpMetadata(
            Source: new AmpSourceInfo("a.nam", 100, "2026-01-01T00:00:00Z", "aa"), Notes: "a-notes")));
        dev.SeedAmp(1, "B", BlobWithMeta(new AmpMetadata(Notes: "b-notes")));
        await dev.OpenAsync();
        var vm = new AmpListViewModel(new AmpService(new SonuClient(dev), _backupDir, 0, 0), true);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Selected = vm.Items[0];
        await vm.DetailsLoadTask!;
        Assert.NotEmpty(vm.DetailsFields);
        Assert.Equal("a-notes", vm.DetailsNotes);

        dev.GateIndex = 1;                                   // B's read will hang until released
        vm.Selected = vm.Items[1];                           // select B; do NOT await the load yet

        Assert.Empty(vm.DetailsFields);                      // pane must clear IMMEDIATELY, not on data arrival
        Assert.Null(vm.DetailsNotes);
        Assert.Null(vm.DetailsUrl);

        dev.Gate.SetResult();
        await vm.DetailsLoadTask!;
        Assert.Equal("b-notes", vm.DetailsNotes);            // and then fill with B's data
    }
}
