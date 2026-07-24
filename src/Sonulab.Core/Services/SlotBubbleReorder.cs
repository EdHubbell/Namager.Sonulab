namespace Sonulab.Core.Services;

/// <summary>The block-agnostic bubble-swap reorder engine: a move from→to is |from-to| adjacent
/// atomic swaps, each verified by reading the block's names back and comparing the two affected
/// slots against a locally-tracked expected order; on mismatch it throws. Because each swap is
/// atomic (firmware `dswap`), a stopped multi-swap move leaves a VALID partial order — the caller
/// resyncs. Shared by preset reorder (over DeviceRepository) and amp/IR reorder (over
/// SlotBlobService); each supplies its own readNames + swap delegates. No temp slot, no
/// name-uniqueness precondition.</summary>
public static class SlotBubbleReorder
{
    public static async Task MoveAsync(int from, int to,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var names = await readNames(ct);
        if (from < 0 || from >= names.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= names.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (string.IsNullOrEmpty(names[from])) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = names.ToArray();
        int step = from < to ? 1 : -1;
        int total = Math.Abs(to - from), done = 0;
        for (int i = from; i != to; i += step)
            await SwapVerifiedAsync(i, i + step, expected, readNames, swap, progress, ++done, total, ct);
    }

    public static async Task MoveStepAsync(int from, bool up,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var names = await readNames(ct);
        if (from < 0 || from >= names.Count) throw new ArgumentOutOfRangeException(nameof(from));
        int to = up ? from - 1 : from + 1;
        if (to < 0 || to >= names.Count) return;                  // at a boundary: nothing to do
        if (string.IsNullOrEmpty(names[from])) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = names.ToArray();
        await SwapVerifiedAsync(from, to, expected, readNames, swap, progress, 1, 1, ct);
    }

    // One atomic swap + read-back name verify. Mutates `expected` to track the post-swap order.
    private static async Task SwapVerifiedAsync(int a, int b, string[] expected,
        Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
        Func<int, int, CancellationToken, Task> swap,
        IProgress<ReorderProgress>? progress, int done, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await swap(a, b, ct);
        (expected[a], expected[b]) = (expected[b], expected[a]);
        var back = await readNames(ct);
        if (back[a] != expected[a] || back[b] != expected[b])
            throw new InvalidOperationException(
                $"Reorder verify failed after swapping slots {a + 1}/{b + 1}: device shows " +
                $"'{back[a]}'/'{back[b]}', expected '{expected[a]}'/'{expected[b]}'.");
        progress?.Report(new ReorderProgress(done, total, $"slots {a + 1}/{b + 1}"));
    }
}
