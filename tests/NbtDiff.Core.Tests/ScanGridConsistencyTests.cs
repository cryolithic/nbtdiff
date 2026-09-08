using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using static NbtDiff.Core.Tests.ScanSupport;

namespace NbtDiff.Core.Tests;

/// <summary>
/// The folder scan's verdict on a region row and the chunk grid opened from it (with the row's own
/// fingerprints) must agree: a Different row has at least one non-Same cell, a Same row has none.
/// </summary>
public class ScanGridConsistencyTests
{
    private static NbtCompound BlockEntity(int x, int y, int z, int items) => new()
    {
        new NbtString("id", "minecraft:chest"),
        new NbtInt("x", x), new NbtInt("y", y), new NbtInt("z", z),
        new NbtList("Items", Enumerable.Range(0, items).Select(i => (NbtTag)new NbtCompound { new NbtByte("Slot", (byte)i), new NbtString("id", "minecraft:stone") }), NbtTagType.Compound),
    };

    private static WorldBuilder WithBlockEntities(WorldBuilder w, params NbtCompound[] be) =>
        w.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("block_entities", new NbtList("block_entities", be.Cast<NbtTag>(), NbtTagType.Compound)));

    public static TheoryData<string> Variants =>
    [
        "identical", "mutate", "addChunk", "removeChunk", "recompress", "defragment", "touchTimestamps",
        "lastUpdate", "reorderedBlockEntities", "changedBlockEntity",
    ];

    private static (WorldBuilder Left, WorldBuilder Right) Make(string variant)
    {
        var b = BaseWorld();
        var chunks = b.ChunksIn(0, 0).ToList();
        return variant switch
        {
            "identical" => (b, BaseWorld()),
            "mutate" => (b, b.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L))),
            "addChunk" => (b, b.Mutate(m => m.AddChunk(0, 0, 20, 20))),
            "removeChunk" => (b, b.Mutate(m => m.RemoveChunk(0, 0, 0, 0))),
            "recompress" => (b, b.Recompress(ChunkRef.SchemeGZip)),
            "defragment" => (b, b.Defragment()),
            "touchTimestamps" => (b, b.TouchTimestamps()),
            "lastUpdate" => (b, b.Mutate(m => { foreach (var (x, z) in chunks) m.Chunk(0, 0, x, z).SetPath("LastUpdate", 1L + x); })),
            "reorderedBlockEntities" => (WithBlockEntities(b, BlockEntity(1, 64, 1, 2), BlockEntity(2, 64, 2, 0)), WithBlockEntities(b, BlockEntity(2, 64, 2, 0), BlockEntity(1, 64, 1, 2))),
            "changedBlockEntity" => (WithBlockEntities(b, BlockEntity(1, 64, 1, 2), BlockEntity(2, 64, 2, 0)), WithBlockEntities(b, BlockEntity(1, 64, 1, 3), BlockEntity(2, 64, 2, 0))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
    }

    [Theory, MemberData(nameof(Variants))]
    public async Task RowVerdict_MatchesGrid_WithDeepVerify(string variant)
    {
        var (lw, rw) = Make(variant);
        var (l, r) = Pair(lw, rw);
        using (l) using (r)
        {
            var options = new CompareOptions();
            var root = await Scan(l.Path, r.Path, options);
            var row = root.Row("region/r.0.0.mca");
            Assert.NotEqual(RowStatus.Pending, row.Status);
            Assert.NotEqual(RowStatus.ProbablyDifferent, row.Status);

            using var lf = RegionFile.Open(row.Left!.FullPath).ValueOrThrow();
            using var rf = RegionFile.Open(row.Right!.FullPath).ValueOrThrow();
            var cells = RegionDiffer.Diff(lf, rf, options.ToDiffOptions(), row.Left.Fingerprint, row.Right.Fingerprint);
            bool gridDiffers = cells.Any(c => c.Status != ChunkDiffStatus.Same);

            Assert.Equal(row.Status == RowStatus.Different, gridDiffers);
            Assert.Equal(variant is "mutate" or "addChunk" or "removeChunk" or "changedBlockEntity", gridDiffers);
        }
    }

    [Theory, MemberData(nameof(Variants))]
    public async Task WithoutDeepVerify_ByteMismatchIsOnlyProbable(string variant)
    {
        var (lw, rw) = Make(variant);
        var (l, r) = Pair(lw, rw);
        using (l) using (r)
        {
            var options = new CompareOptions(DeepVerify: false);
            var root = await Scan(l.Path, r.Path, options);
            var row = root.Row("region/r.0.0.mca");
            // Without a content pass the scan can only say "bytes differ"; it must never claim Different.
            Assert.NotEqual(RowStatus.Different, row.Status);
            Assert.Equal(FingerprintTier.Quick, row.Left!.Fingerprint!.Tier);

            using var lf = RegionFile.Open(row.Left.FullPath).ValueOrThrow();
            using var rf = RegionFile.Open(row.Right!.FullPath).ValueOrThrow();
            var cells = RegionDiffer.Diff(lf, rf, options.ToDiffOptions(), row.Left.Fingerprint, row.Right.Fingerprint);
            bool gridDiffers = cells.Any(c => c.Status != ChunkDiffStatus.Same);
            // The grid parses what the quick hashes could not settle and reaches the verified answer.
            Assert.Equal(variant is "mutate" or "addChunk" or "removeChunk" or "changedBlockEntity", gridDiffers);
            if (row.Status == RowStatus.Same) Assert.False(gridDiffers);
        }
    }
}
