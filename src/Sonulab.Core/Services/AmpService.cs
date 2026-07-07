using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class AmpServiceException(string message) : Exception(message);

// Member ORDER must match SlotUploadStage (SlotBlobService.cs) — AmpService casts between them.
public enum AmpUploadStage { BackingUp, Writing, Verifying, Done }

/// <summary>Progress for the ~3s guarded upload. ChunksTotal is always 98
/// (chunk 0 = name, 1..96 = payload, -1 = commit).</summary>
public sealed record AmpUploadProgress(AmpUploadStage Stage, int ChunksDone, int ChunksTotal);

/// <summary>Guarded amp-slot operations on root\amp — thin front over SlotBlobService,
/// which holds the ONE hardware-verified implementation of the write sequence.</summary>
public sealed class AmpService
{
    public const int SlotCount = SlotBlobService.SlotCount;
    public const int AmpChunks = 96;             // 12288 / 128
    public const int AmpBytes = 12288;
    public const int NameMaxChars = SlotBlobService.NameMaxChars;

    private readonly SlotBlobService _inner;

    public AmpService(SonuClient client, string backupDir, int paceMs = 25, int settleMs = 750) =>
        _inner = new SlotBlobService(client, SlotBlobKind.Amp, backupDir,
                                     msg => new AmpServiceException(msg), paceMs, settleMs);

    public async Task<IReadOnlyList<AmpSlot>> ListAmpsAsync(CancellationToken ct = default) =>
        (await _inner.ListAsync(ct)).Select(s => new AmpSlot(s.Index, s.Name)).ToArray();

    public Task<byte[]> ReadAmpAsync(int index, CancellationToken ct = default) => _inner.ReadAsync(index, ct);

    /// <summary>Validated read of an arbitrary 1-based chunk range (128 B per chunk).</summary>
    public Task<byte[]> ReadChunksAsync(int index, int firstChunk, int count, CancellationToken ct = default) =>
        _inner.ReadChunkRangeAsync(index, firstChunk, count, ct);

    public Task DeleteAmpAsync(int index, CancellationToken ct = default) => _inner.DeleteAsync(index, ct);
    public Task RenameAmpAsync(int index, string name, CancellationToken ct = default) => _inner.RenameAsync(index, name, ct);

    public Task UploadAmpAsync(int slot, byte[] vxampBytes, string name,
        IProgress<AmpUploadProgress>? progress = null, CancellationToken ct = default) =>
        _inner.UploadAsync(slot, vxampBytes, name,
            progress is null ? null : new Adapter(progress), ct);

    /// <summary>Trim + validate an amp name: non-empty, ASCII, truncated to 31 chars.</summary>
    internal static string ValidateName(string name) =>
        SlotBlobService.ValidateName(name, SlotBlobKind.Amp, msg => new AmpServiceException(msg));

    internal static byte[] NamePad(string name) => SlotBlobService.NamePad(name);

    private sealed class Adapter(IProgress<AmpUploadProgress> target) : IProgress<SlotUploadProgress>
    {
        public void Report(SlotUploadProgress p) =>
            target.Report(new AmpUploadProgress((AmpUploadStage)(int)p.Stage, p.ChunksDone, p.ChunksTotal));
    }
}
