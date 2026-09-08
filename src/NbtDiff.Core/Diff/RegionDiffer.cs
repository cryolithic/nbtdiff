using fNbt;
using NbtDiff.Nbt;

namespace NbtDiff.Core;

public enum ChunkDiffStatus { Same, Different, LeftOnly, RightOnly, Error }

/// <summary>One occupied cell of the 32×32 region comparison grid.</summary>
public sealed record ChunkDiffCell(int X, int Z, ChunkDiffStatus Status, string? Error = null);

/// <summary>Chunk-level comparison of two region files, and tag-level diff of one chunk pair.</summary>
public static class RegionDiffer
{
    /// <summary>
    /// Status of every slot occupied on either side, in (z, x) order. Supplied fingerprints are used to
    /// avoid parsing: equal hashes are <see cref="ChunkDiffStatus.Same"/> at any tier; unequal hashes are
    /// <see cref="ChunkDiffStatus.Different"/> only when both are <see cref="FingerprintTier.Deep"/>,
    /// otherwise the chunk is parsed and compared canonically. Without fingerprints, chunks whose
    /// compressed bytes match are Same without decompression; the rest are parsed. Any unreadable side
    /// yields <see cref="ChunkDiffStatus.Error"/>.
    /// </summary>
    public static IReadOnlyList<ChunkDiffCell> Diff(RegionFile? left, RegionFile? right, DiffOptions? options = null,
        FileFingerprint? leftFp = null, FileFingerprint? rightFp = null)
    {
        options ??= DiffOptions.Default;
        var cells = new List<ChunkDiffCell>();
        if (left is null && right is null) return cells;

        var slots = new SortedSet<(int Z, int X)>();
        if (left is not null) foreach (var c in left.Chunks) slots.Add((c.Z, c.X));
        if (right is not null) foreach (var c in right.Chunks) slots.Add((c.Z, c.X));

        bool useFp = leftFp?.Region is not null && rightFp?.Region is not null;
        bool fpFinal = useFp && leftFp!.Tier == FingerprintTier.Deep && rightFp!.Tier == FingerprintTier.Deep;
        byte[]? scratchL = null, scratchR = null;

        foreach (var (z, x) in slots)
        {
            var lc = left?[x, z];
            var rc = right?[x, z];
            if (lc is null || rc is null)
            {
                var only = lc ?? rc!;
                var status = lc is null ? ChunkDiffStatus.RightOnly : ChunkDiffStatus.LeftOnly;
                cells.Add(only.IsCorruptHeader ? new ChunkDiffCell(x, z, ChunkDiffStatus.Error, $"Chunk ({x}, {z}): {only.HeaderError}") : new ChunkDiffCell(x, z, status));
                continue;
            }

            if (useFp)
            {
                var lr = leftFp!.Region!;
                var rr = rightFp!.Region!;
                if (lr.ChunkErrors.TryGetValue((x, z), out var le)) { cells.Add(new ChunkDiffCell(x, z, ChunkDiffStatus.Error, le)); continue; }
                if (rr.ChunkErrors.TryGetValue((x, z), out var re)) { cells.Add(new ChunkDiffCell(x, z, ChunkDiffStatus.Error, re)); continue; }
                if (lr.ChunkHashes.TryGetValue((x, z), out var lh) && rr.ChunkHashes.TryGetValue((x, z), out var rh))
                {
                    if (lh == rh) { cells.Add(new ChunkDiffCell(x, z, ChunkDiffStatus.Same)); continue; }
                    if (fpFinal) { cells.Add(new ChunkDiffCell(x, z, ChunkDiffStatus.Different)); continue; }
                }
            }

            cells.Add(CompareByContent(lc, rc, options, ref scratchL, ref scratchR));
        }
        return cells;
    }

    private static ChunkDiffCell CompareByContent(ChunkRef lc, ChunkRef rc, DiffOptions options, ref byte[]? scratchL, ref byte[]? scratchR)
    {
        scratchL ??= new byte[ChunkRef.MaxInlinePayload];
        scratchR ??= new byte[ChunkRef.MaxInlinePayload];
        var lp = lc.ReadCompressedPayload(scratchL);
        if (!lp.Ok) return new ChunkDiffCell(lc.X, lc.Z, ChunkDiffStatus.Error, lp.Failure!.ToShortString());
        var rp = rc.ReadCompressedPayload(scratchR);
        if (!rp.Ok) return new ChunkDiffCell(lc.X, lc.Z, ChunkDiffStatus.Error, rp.Failure!.ToShortString());

        if (lp.Value!.Scheme == rp.Value!.Scheme && lp.Value.Bytes.Span.SequenceEqual(rp.Value.Bytes.Span))
            return new ChunkDiffCell(lc.X, lc.Z, ChunkDiffStatus.Same);

        var ln = lc.ParseNbt(lp.Value);
        if (!ln.Ok) return new ChunkDiffCell(lc.X, lc.Z, ChunkDiffStatus.Error, ln.Failure!.ToShortString());
        var rn = rc.ParseNbt(rp.Value);
        if (!rn.Ok) return new ChunkDiffCell(lc.X, lc.Z, ChunkDiffStatus.Error, rn.Failure!.ToShortString());

        bool same = NbtCanonicalHasher.Hash(ln.Value!, options.CompoundOrderMatters, options.Ignored, options.KeyedLists) == NbtCanonicalHasher.Hash(rn.Value!, options.CompoundOrderMatters, options.Ignored, options.KeyedLists);
        return new ChunkDiffCell(lc.X, lc.Z, same ? ChunkDiffStatus.Same : ChunkDiffStatus.Different);
    }

    /// <summary>
    /// Tag-level diff of the chunk at local (x, z). Parses exactly the two requested chunks. A side without
    /// that chunk makes the whole tree Added/Removed; a side that fails to read is a failure result.
    /// </summary>
    public static LoadResult<DiffNode> DiffChunk(RegionFile? left, RegionFile? right, int x, int z, DiffOptions? options = null)
    {
        var lc = left?[x, z];
        var rc = right?[x, z];
        if (lc is null && rc is null)
            return LoadResult<DiffNode>.Fail($"No chunk at ({x}, {z}) on either side");

        NbtCompound? lroot = null, rroot = null;
        if (lc is not null)
        {
            var r = lc.ReadNbt();
            if (!r.Ok) return LoadResult<DiffNode>.Fail(new LoadFailure($"Left chunk ({x}, {z})") { Attempts = [r.Failure!] });
            lroot = r.Value;
        }
        if (rc is not null)
        {
            var r = rc.ReadNbt();
            if (!r.Ok) return LoadResult<DiffNode>.Fail(new LoadFailure($"Right chunk ({x}, {z})") { Attempts = [r.Failure!] });
            rroot = r.Value;
        }
        return LoadResult<DiffNode>.Success(NbtDiffer.Diff(lroot, rroot, options));
    }
}
