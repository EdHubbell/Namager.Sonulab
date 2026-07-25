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
}
