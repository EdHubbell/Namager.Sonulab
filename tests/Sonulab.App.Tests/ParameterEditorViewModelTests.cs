using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class ParameterEditorViewModelTests
{
    static FakeSonuLink Dev()
    {
        var d = new FakeSonuLink();
        d.SeedScalar(@"root\app\amp\on_off", "\"ON\"");
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"unit\":\"dB\"}");
        return d;
    }

    [Fact] public async Task Load_builds_editable_fields_from_browse()
    {
        var link = Dev(); await link.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(link));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Fields.Count);
        Assert.Contains(vm.Fields, f => f.Path == @"root\app\amp\gain" && f.Kind == "float");
    }

    [Fact] public async Task Save_writes_changed_fields_then_save_command()
    {
        var link = Dev(); await link.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(link)) { PresetName = "MyPreset" };
        await vm.LoadCommand.ExecuteAsync(null);
        var gain = vm.Fields.First(f => f.Path == @"root\app\amp\gain");
        gain.Number = -6.0;
        await vm.SaveCommand.ExecuteAsync(null);
        // FakeSonuLink stored the write; reading it back reflects the change
        Assert.Equal("-6", await new SonuClient(link).ReadValueAsync(@"root\app\amp\gain"));
    }
}
