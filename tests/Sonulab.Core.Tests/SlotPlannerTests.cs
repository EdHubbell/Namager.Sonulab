using Sonulab.Core.Services;
using Xunit;

public class SlotPlannerTests
{
    [Fact] public void Move_down_shifts_intervening_items_up()
    {
        // ids 0..4 in slots 0..4; move slot 1 -> slot 3
        var result = SlotPlanner.Move(new[] { 0, 1, 2, 3, 4 }, from: 1, to: 3);
        Assert.Equal(new[] { 0, 2, 3, 1, 4 }, result);
    }

    [Fact] public void Move_up_shifts_intervening_items_down()
    {
        var result = SlotPlanner.Move(new[] { 0, 1, 2, 3, 4 }, from: 3, to: 1);
        Assert.Equal(new[] { 0, 3, 1, 2, 4 }, result);
    }

    [Fact] public void Move_preserves_length_and_handles_empty_slots()
    {
        // -1 = empty; move slot 0 -> slot 2
        var result = SlotPlanner.Move(new[] { 5, -1, 7, -1 }, from: 0, to: 2);
        Assert.Equal(4, result.Length);
        Assert.Equal(new[] { -1, 7, 5, -1 }, result);
    }

    [Fact] public void Move_to_same_index_is_identity()
    {
        Assert.Equal(new[] { 0, 1, 2 }, SlotPlanner.Move(new[] { 0, 1, 2 }, 1, 1));
    }

    [Fact] public void ChangedRange_is_inclusive_min_max()
    {
        Assert.Equal((1, 3), SlotPlanner.ChangedRange(1, 3));
        Assert.Equal((1, 3), SlotPlanner.ChangedRange(3, 1));
        Assert.Equal((2, 2), SlotPlanner.ChangedRange(2, 2));
    }
}
