using NbtDiff.Nbt;

namespace NbtDiff.Core;

public enum FingerprintTier
{
    /// <summary>Bytes only, no decompression. Equal ⇒ same content; different ⇒ maybe.</summary>
    Quick,
    /// <summary>Parsed content. Both answers are final.</summary>
    Deep,
}

/// <summary>Per-chunk hashes of a region file, keyed by local (x, z). Chunks that could not be read are in <see cref="ChunkErrors"/> instead.</summary>
public sealed record RegionFingerprint(
    IReadOnlyDictionary<(int X, int Z), ulong> ChunkHashes,
    IReadOnlyDictionary<(int X, int Z), string> ChunkErrors)
{
    public bool HasErrors => ChunkErrors.Count > 0;
    public int ChunkCount => ChunkHashes.Count + ChunkErrors.Count;
}

/// <param name="Hash">Whole-file content hash. For regions it is derived from the chunk table, so two regions with equal chunk hashes have equal file hashes.</param>
public sealed record FileFingerprint(FileKind Kind, FingerprintTier Tier, long Size, ulong Hash, RegionFingerprint? Region = null)
{
    public bool HasErrors => Region?.HasErrors ?? false;

    /// <summary>Same kind and hash, and neither side has unreadable chunks (an error chunk is never "same").</summary>
    public bool ContentEquals(FileFingerprint other) =>
        Kind == other.Kind && Hash == other.Hash && !HasErrors && !other.HasErrors;
}

public interface IFingerprinter
{
    /// <summary>Tier 1: hashes bytes (per chunk for regions, excluding timestamps and sector layout). Never decompresses.</summary>
    ValueTask<LoadResult<FileFingerprint>> QuickAsync(string path, FileKind kind, CancellationToken ct = default);

    /// <summary>Tier 2: hashes parsed content (canonical NBT; line-ending-normalized text). Binary files reuse the quick hash.</summary>
    ValueTask<LoadResult<FileFingerprint>> DeepAsync(string path, FileKind kind, CancellationToken ct = default);
}
