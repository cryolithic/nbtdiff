using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using NbtDiff.Nbt;

namespace NbtDiff.Core;

/// <summary>Default <see cref="IFingerprinter"/> over the local file system.</summary>
/// <param name="ignoredTags">Tag paths excluded from deep hashes; null means <see cref="TagIgnoreSet.Default"/>.</param>
/// <param name="keyedLists">Hash compound list items in key order (reordered entities are not a change); null means positional.</param>
public sealed class Fingerprinter(bool compoundOrderMatters = false, TagIgnoreSet? ignoredTags = null, KeyedAligner? keyedLists = null) : IFingerprinter
{
    private readonly TagIgnoreSet _ignored = ignoredTags ?? TagIgnoreSet.Default;
    private const int FileBufferSize = 1 << 16;

    /// <summary>A fingerprinter whose deep tier agrees with <paramref name="options"/>.</summary>
    public static Fingerprinter For(CompareOptions options) =>
        new(options.CompoundOrderMatters, options.EffectiveIgnoredTags, options.KeyedListAligner);
    private static readonly ArrayPool<byte> ChunkPool = ArrayPool<byte>.Create(ChunkRef.MaxInlinePayload, maxArraysPerBucket: 64);

    public async ValueTask<LoadResult<FileFingerprint>> QuickAsync(string path, FileKind kind, CancellationToken ct = default)
    {
        ThrowIfDirectory(kind);
        try
        {
            if (kind == FileKind.Region)
                return FingerprintRegion(path, FingerprintTier.Quick, ct);

            var (size, hash) = await HashFileAsync(path, stripCarriageReturns: false, ct).ConfigureAwait(false);
            return LoadResult<FileFingerprint>.Success(new FileFingerprint(kind, FingerprintTier.Quick, size, hash));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return LoadResult<FileFingerprint>.Fail("Read file", e);
        }
    }

    public async ValueTask<LoadResult<FileFingerprint>> DeepAsync(string path, FileKind kind, CancellationToken ct = default)
    {
        ThrowIfDirectory(kind);
        try
        {
            switch (kind)
            {
                case FileKind.Region:
                    return FingerprintRegion(path, FingerprintTier.Deep, ct);

                case FileKind.Nbt:
                case FileKind.Snbt:
                {
                    var doc = NbtDocument.Load(path);
                    if (!doc.Ok && kind == FileKind.Snbt)
                    {
                        // Not SNBT after all (FTB leaves comment-only .snbt files behind): compare as text.
                        var (textSize, textHash) = await HashFileAsync(path, stripCarriageReturns: true, ct).ConfigureAwait(false);
                        return LoadResult<FileFingerprint>.Success(new FileFingerprint(kind, FingerprintTier.Deep, textSize, textHash));
                    }
                    if (!doc.Ok) return LoadResult<FileFingerprint>.Fail(doc.Failure!);
                    ct.ThrowIfCancellationRequested();
                    ulong hash = NbtCanonicalHasher.Hash(doc.Value!.Root, compoundOrderMatters, _ignored, keyedLists);
                    return LoadResult<FileFingerprint>.Success(new FileFingerprint(kind, FingerprintTier.Deep, new FileInfo(path).Length, hash));
                }

                case FileKind.Json:
                case FileKind.Text:
                {
                    var (size, hash) = await HashFileAsync(path, stripCarriageReturns: true, ct).ConfigureAwait(false);
                    return LoadResult<FileFingerprint>.Success(new FileFingerprint(kind, FingerprintTier.Deep, size, hash));
                }

                default:
                {
                    var (size, hash) = await HashFileAsync(path, stripCarriageReturns: false, ct).ConfigureAwait(false);
                    return LoadResult<FileFingerprint>.Success(new FileFingerprint(kind, FingerprintTier.Deep, size, hash));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return LoadResult<FileFingerprint>.Fail("Read file", e);
        }
    }

    private LoadResult<FileFingerprint> FingerprintRegion(string path, FingerprintTier tier, CancellationToken ct)
    {
        var open = RegionFile.Open(path);
        if (!open.Ok) return LoadResult<FileFingerprint>.Fail(open.Failure!);

        using var region = open.Value!;
        var hashes = new Dictionary<(int X, int Z), ulong>(region.ChunkCount);
        var errors = new Dictionary<(int X, int Z), string>();
        byte[] scratch = ChunkPool.Rent(ChunkRef.MaxInlinePayload);
        var h = new XxHash64();   // one hasher per region, reset per chunk
        try
        {
            foreach (var chunk in region.Chunks)
            {
                ct.ThrowIfCancellationRequested();
                var payload = chunk.ReadCompressedPayload(scratch);
                if (!payload.Ok)
                {
                    errors[(chunk.X, chunk.Z)] = payload.Failure!.ToShortString();
                    continue;
                }

                if (tier == FingerprintTier.Quick)
                {
                    h.Reset();
                    h.Append([payload.Value!.Scheme]);
                    h.Append(payload.Value.Bytes.Span);
                    hashes[(chunk.X, chunk.Z)] = h.GetCurrentHashAsUInt64();
                }
                else
                {
                    var nbt = chunk.ParseNbt(payload.Value!);
                    if (!nbt.Ok)
                        errors[(chunk.X, chunk.Z)] = nbt.Failure!.ToShortString();
                    else
                        hashes[(chunk.X, chunk.Z)] = NbtCanonicalHasher.Hash(nbt.Value!, compoundOrderMatters, _ignored, keyedLists);
                }
            }
        }
        finally
        {
            ChunkPool.Return(scratch);
        }

        var fp = new RegionFingerprint(hashes, errors);
        return LoadResult<FileFingerprint>.Success(new FileFingerprint(FileKind.Region, tier, region.Length, CombineChunkTable(region, fp), fp));
    }

    // Chunks are visited in the region's fixed (z, x) order, so this is deterministic. Errors feed
    // their text in, so a file with a corrupt slot still gets a stable hash that differs from a clean one.
    private static ulong CombineChunkTable(RegionFile region, RegionFingerprint fp)
    {
        var h = new XxHash64();
        Span<byte> buf = stackalloc byte[16];
        foreach (var chunk in region.Chunks)
        {
            var key = (chunk.X, chunk.Z);
            BinaryPrimitives.WriteInt32LittleEndian(buf, chunk.X);
            BinaryPrimitives.WriteInt32LittleEndian(buf[4..], chunk.Z);
            if (fp.ChunkHashes.TryGetValue(key, out var hash))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buf[8..], hash);
                h.Append(buf);
            }
            else
            {
                h.Append(buf[..8]);
                h.Append("!"u8);
                h.Append(System.Text.Encoding.UTF8.GetBytes(fp.ChunkErrors[key]));
            }
        }
        return h.GetCurrentHashAsUInt64();
    }

    private static async ValueTask<(long Size, ulong Hash)> HashFileAsync(string path, bool stripCarriageReturns, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(FileBufferSize);
        try
        {
            var h = new XxHash64();
            long size = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer.AsMemory(0, FileBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                size += n;
                var span = buffer.AsSpan(0, n);
                if (stripCarriageReturns)
                    n = RemoveCarriageReturns(span);
                h.Append(buffer.AsSpan(0, n));
            }
            return (size, h.GetCurrentHashAsUInt64());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int RemoveCarriageReturns(Span<byte> span)
    {
        int write = 0;
        for (int read = 0; read < span.Length; read++)
        {
            if (span[read] != (byte)'\r')
                span[write++] = span[read];
        }
        return write;
    }

    private static void ThrowIfDirectory(FileKind kind)
    {
        if (kind == FileKind.Directory)
            throw new ArgumentException("Directories have no fingerprint", nameof(kind));
    }
}
