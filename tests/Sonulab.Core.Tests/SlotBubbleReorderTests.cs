using Sonulab.Core.Services;
using Xunit;

public class SlotBubbleReorderTests
{
    // In-memory block: names + an atomic swap, mirroring what dswap does on the device.
    sealed class Block
    {
        public readonly List<string> Names;
        public int SwapCount;
        public int? NoOpSwapAt;                    // when set, the Nth swap silently does nothing
        public Block(params string[] names) => Names = names.ToList();
        public Task<IReadOnlyList<string>> Read(System.Threading.CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Names.ToArray());
        public Task Swap(int a, int b, System.Threading.CancellationToken ct)
        {
            if (++SwapCount == NoOpSwapAt) return Task.CompletedTask;   // simulate a swap that didn't apply
            (Names[a], Names[b]) = (Names[b], Names[a]);
            return Task.CompletedTask;
        }
    }

    [Fact] public async Task MoveStep_down_swaps_once()
    {
        var b = new Block("A", "B", "C");
        await SlotBubbleReorder.MoveStepAsync(0, up: false, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "B", "A", "C" }, b.Names);
        Assert.Equal(1, b.SwapCount);
    }

    [Fact] public async Task MoveStep_into_empty_neighbor_moves_via_single_swap()
    {
        var b = new Block("A", "");            // slot 1 empty
        await SlotBubbleReorder.MoveStepAsync(0, up: false, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "", "A" }, b.Names);
    }

    [Fact] public async Task MoveAsync_up_bubbles_to_remove_insert_order()
    {
        var b = new Block("A", "B", "C", "D");
        await SlotBubbleReorder.MoveAsync(3, 1, b.Read, b.Swap, null, default);   // D -> slot 1
        Assert.Equal(new[] { "A", "D", "B", "C" }, b.Names);
        Assert.Equal(2, b.SwapCount);
    }

    [Fact] public async Task MoveAsync_over_interior_empty_bubbles_the_empty_too()
    {
        var b = new Block("A", "", "C", "D");
        await SlotBubbleReorder.MoveAsync(3, 0, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "D", "A", "", "C" }, b.Names);
    }

    [Fact] public async Task MoveAsync_empty_source_throws()
    {
        var b = new Block("A", "", "C");
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => SlotBubbleReorder.MoveAsync(1, 2, b.Read, b.Swap, null, default));
    }

    [Fact] public async Task MoveAsync_midway_verify_failure_throws_and_leaves_valid_partial_order()
    {
        var b = new Block("A", "B", "C", "D") { NoOpSwapAt = 2 };   // 2nd swap of MoveAsync(0,3) no-ops
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => SlotBubbleReorder.MoveAsync(0, 3, b.Read, b.Swap, null, default));
        Assert.Equal(new[] { "B", "A", "C", "D" }, b.Names);        // only swap 1 applied — valid order
    }

    [Fact] public async Task MoveStep_at_boundary_is_noop()
    {
        var b = new Block("A", "B");
        await SlotBubbleReorder.MoveStepAsync(0, up: true, b.Read, b.Swap, null, default);
        Assert.Equal(new[] { "A", "B" }, b.Names);
        Assert.Equal(0, b.SwapCount);
    }
}
