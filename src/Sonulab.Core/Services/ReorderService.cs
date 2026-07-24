namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

/// <summary>Reorders PRESET slots via the shared <see cref="SlotBubbleReorder"/> engine over the
/// atomic firmware `dswap` verb (see that type for the algorithm + safety rationale).</summary>
public sealed class ReorderService
{
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default) =>
        SlotBubbleReorder.MoveAsync(from, to, ReadNamesAsync, _repo.SwapPresetSlotsAsync, progress, ct);

    public Task MoveStepAsync(int from, bool up, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default) =>
        SlotBubbleReorder.MoveStepAsync(from, up, ReadNamesAsync, _repo.SwapPresetSlotsAsync, progress, ct);

    private async Task<IReadOnlyList<string>> ReadNamesAsync(CancellationToken ct) =>
        (await _repo.ListPresetsAsync(ct)).Select(s => s.Name).ToArray();
}
