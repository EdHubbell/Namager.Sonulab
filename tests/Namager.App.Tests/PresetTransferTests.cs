using System.Collections.Generic;
using System.ComponentModel;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class PresetTransferTests
{
    static (ParameterEditorViewModel vm, FakePresetDevice dev) Editor()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(3, "Clean Verb", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var client = new SonuClient(dev);
        var vm = new ParameterEditorViewModel(client, repo: new DeviceRepository(client));
        return (vm, dev);
    }

    [Fact] public void CanDownload_is_false_before_a_preset_is_loaded()
    {
        var (vm, _) = Editor();
        Assert.False(vm.CanDownload);
    }

    [Fact] public async Task SuggestedFileName_uses_the_zero_based_backup_convention()
    {
        var (vm, _) = Editor();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(3, "Clean Verb"));
        Assert.Equal("03 - Clean Verb.pst", vm.SuggestedFileName);
        Assert.True(vm.CanDownload);
    }

    [Fact] public async Task ReadLoadedPresetBytes_returns_a_full_blob_matching_the_slot()
    {
        var (vm, dev) = Editor();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(3, "Clean Verb"));
        var bytes = await vm.ReadLoadedPresetBytesAsync();
        Assert.NotNull(bytes);
        Assert.Equal(PresetDocument.BlobSize, bytes!.Length);
        Assert.Contains(@"root\app\amp\amp", PresetDocument.Parse(bytes).Lines[0]);
    }

    [Fact] public async Task ReadLoadedPresetBytes_surfaces_a_read_failure_instead_of_throwing()
    {
        var (vm, dev) = Editor();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(3, "Clean Verb"));
        dev.Close();
        var bytes = await vm.ReadLoadedPresetBytesAsync();
        Assert.Null(bytes);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact] public async Task CanDownload_change_notification_fires_across_a_load()
    {
        var (vm, _) = Editor();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(3, "Clean Verb"));

        Assert.Contains(nameof(ParameterEditorViewModel.CanDownload), raised);
    }

    static (PresetListViewModel vm, FakePresetDevice dev, FakePresetUsageService usage) List(int occupied)
    {
        var dev = new FakePresetDevice();
        for (int i = 0; i < occupied; i++)
            dev.SeedSlot(i, $"P{i}", new[] { $@"root\app\amp\amp:{{""value"":""m{i}""}}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        var usage = new FakePresetUsageService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, usage: usage);
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (vm, dev, usage);
    }

    static string WritePst(string dir, string fileName, params string[] lines)
    {
        System.IO.Directory.CreateDirectory(dir);
        var blob = new byte[PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n", lines)).CopyTo(blob, 0);
        var path = System.IO.Path.Combine(dir, fileName);
        System.IO.File.WriteAllBytes(path, blob);
        return path;
    }

    [Fact] public async Task Upload_lands_in_the_first_empty_slot_with_the_name_from_the_file()
    {
        var (vm, _, usage) = List(occupied: 2);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        var file = WritePst(dir, "09 - Imported.pst", @"root\app\amp\amp:{""value"":""mZ""}");

        await vm.UploadAsync(file, () => Task.FromResult<int?>(null));

        Assert.Equal("Imported", vm.Items[2].Name);
        Assert.Equal(1, usage.ContentWrittenCount);
        Assert.Equal((2, "Imported"), usage.LastContentChanged);
        System.IO.Directory.Delete(dir, true);
    }

    [Fact] public async Task Upload_renames_on_a_name_clash()
    {
        var (vm, _, _) = List(occupied: 2);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        var file = WritePst(dir, "00 - P0.pst", @"root\app\amp\amp:{""value"":""mZ""}");

        await vm.UploadAsync(file, () => Task.FromResult<int?>(null));

        Assert.Equal("P0 #2", vm.Items[2].Name);
        System.IO.Directory.Delete(dir, true);
    }

    [Fact] public async Task Upload_asks_for_a_slot_only_when_the_pedal_is_full()
    {
        var (vm, _, _) = List(occupied: 30);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        var file = WritePst(dir, "00 - Imported.pst", @"root\app\amp\amp:{""value"":""mZ""}");

        bool asked = false;
        await vm.UploadAsync(file, () => { asked = true; return Task.FromResult<int?>(5); });

        Assert.True(asked);
        Assert.Equal("Imported", vm.Items[5].Name);
        System.IO.Directory.Delete(dir, true);
    }

    [Fact] public async Task Upload_writes_nothing_when_the_slot_prompt_is_cancelled()
    {
        var (vm, _, usage) = List(occupied: 30);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        var file = WritePst(dir, "00 - Imported.pst", @"root\app\amp\amp:{""value"":""mZ""}");

        await vm.UploadAsync(file, () => Task.FromResult<int?>(null));

        Assert.DoesNotContain(vm.Items, i => i.Name == "Imported");
        Assert.Equal(0, usage.ContentWrittenCount);
        System.IO.Directory.Delete(dir, true);
    }

    [Fact] public async Task Upload_rejects_an_empty_file_without_touching_the_device()
    {
        var (vm, _, usage) = List(occupied: 2);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "00 - Bad.pst");
        System.IO.File.WriteAllBytes(file, new byte[PresetDocument.BlobSize]);   // all zeros -> no lines

        await vm.UploadAsync(file, () => Task.FromResult<int?>(null));

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, usage.ContentWrittenCount);
        Assert.True(vm.Items[2].IsEmpty);
        System.IO.Directory.Delete(dir, true);
    }

    [Fact] public async Task Upload_rejects_a_missing_file_without_throwing()
    {
        var (vm, _, usage) = List(occupied: 2);
        await vm.UploadAsync(@"C:\definitely\not\here.pst", () => Task.FromResult<int?>(null));
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(0, usage.ContentWrittenCount);
    }
}
