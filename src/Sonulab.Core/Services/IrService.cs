using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class IrServiceException(string message) : Exception(message);

/// <summary>Guarded IR-slot operations on root\ir — thin front over SlotBlobService
/// (32 chunks x 128 B = 4096-byte payload; same commit-at-chunk:-1 semantics as amps,
/// hardware-confirmed by the Task 7 probe).</summary>
public sealed class IrService
{
    public const int IrChunks = 32;
    public const int IrBytes = 4096;

    private readonly SlotBlobService _inner;

    public IrService(SonuClient client, string backupDir, int paceMs = 25, int settleMs = 750) =>
        _inner = new SlotBlobService(client, SlotBlobKind.Ir, backupDir,
                                     msg => new IrServiceException(msg), paceMs, settleMs);

    public Task<IReadOnlyList<SlotEntry>> ListIrsAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
    public Task<byte[]> ReadIrAsync(int index, CancellationToken ct = default) => _inner.ReadAsync(index, ct);
    public Task DeleteIrAsync(int index, CancellationToken ct = default) => _inner.DeleteAsync(index, ct);
    public Task RenameIrAsync(int index, string name, CancellationToken ct = default) => _inner.RenameAsync(index, name, ct);

    public Task UploadIrAsync(int slot, byte[] irBytes, string name,
        IProgress<SlotUploadProgress>? progress = null, CancellationToken ct = default) =>
        _inner.UploadAsync(slot, irBytes, name, progress, ct);
}
