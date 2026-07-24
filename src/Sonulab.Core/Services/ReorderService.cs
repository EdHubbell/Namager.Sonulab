using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

/// <summary>Reorders preset slots using the atomic firmware `dswap` verb: a move from→to is a
/// sequence of |from-to| adjacent swaps, each moving name AND content atomically (~213 ms).
/// After each swap the two affected slot names are read back and verified against the expected
/// order; on mismatch the move stops and throws. Because `dswap` is atomic per firmware, a stopped
/// move leaves a VALID partial order (no torn/corrupted slot) — the caller resyncs from the device.
/// No temp slot, no save-by-name, no name-uniqueness precondition.</summary>
public sealed class ReorderService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = slots.Select(s => s.Name).ToArray();
        int step = from < to ? 1 : -1;
        int total = Math.Abs(to - from), done = 0;
        for (int i = from; i != to; i += step)
            await SwapVerifiedAsync(i, i + step, expected, progress, ++done, total, ct);
        Log.Info("MoveAsync from={0} to={1} completed in {2} swap(s)", from, to, total);
    }

    public async Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        int to = up ? from - 1 : from + 1;
        if (to < 0 || to >= slots.Count) return;                  // at a boundary: nothing to do
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");

        var expected = slots.Select(s => s.Name).ToArray();
        await SwapVerifiedAsync(from, to, expected, progress, 1, 1, ct);
    }

    // One atomic swap + read-back name verify. Mutates `expected` to track the post-swap order.
    private async Task SwapVerifiedAsync(int a, int b, string[] expected,
        IProgress<ReorderProgress>? progress, int done, int total, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _repo.SwapPresetSlotsAsync(a, b, ct);
        (expected[a], expected[b]) = (expected[b], expected[a]);
        var back = await _repo.ListPresetsAsync(ct);
        if (back[a].Name != expected[a] || back[b].Name != expected[b])
            throw new InvalidOperationException(
                $"Reorder verify failed after swapping slots {a + 1}/{b + 1}: device shows " +
                $"'{back[a].Name}'/'{back[b].Name}', expected '{expected[a]}'/'{expected[b]}'.");
        progress?.Report(new ReorderProgress(done, total, $"slots {a + 1}/{b + 1}"));
    }
}
