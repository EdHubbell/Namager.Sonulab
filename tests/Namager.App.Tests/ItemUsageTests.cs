using Namager.App.ViewModels;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class ItemUsageTests
{
    [Fact]
    public void Amp_item_reports_used_state_and_tooltip_with_slots()
    {
        var item = new AmpItemViewModel(new AmpSlot(0, "Plexi"));
        Assert.False(item.IsUsed);
        Assert.Null(item.UsedInTooltip);

        item.UsedInPresets = new[] { new PresetRef(2, "Lead"), new PresetRef(6, "Rhythm") };
        Assert.True(item.IsUsed);
        Assert.Equal("Used in: 03 Lead, 07 Rhythm", item.UsedInTooltip);
    }

    [Fact]
    public void Ir_item_reports_used_state_and_tooltip_with_slots()
    {
        var item = new IrItemViewModel(new SlotEntry(0, "V30"));
        Assert.False(item.IsUsed);

        item.UsedInPresets = new[] { new PresetRef(0, "Clean") };
        Assert.True(item.IsUsed);
        Assert.Equal("Used in: 01 Clean", item.UsedInTooltip);
    }

    [Fact]
    public void Setting_used_presets_raises_change_notifications()
    {
        var item = new AmpItemViewModel(new AmpSlot(0, "Plexi"));
        var changed = new System.Collections.Generic.List<string>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        item.UsedInPresets = new[] { new PresetRef(0, "Lead") };
        Assert.Contains(nameof(item.IsUsed), changed);
        Assert.Contains(nameof(item.UsedInTooltip), changed);
    }
}
