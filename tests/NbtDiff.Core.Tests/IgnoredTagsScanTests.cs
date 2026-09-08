using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using static NbtDiff.Core.Tests.ScanSupport;

namespace NbtDiff.Core.Tests;

/// <summary>End-to-end: a chunk re-saved by Minecraft with only its LastUpdate tick changed is not a difference.</summary>
public class IgnoredTagsScanTests
{
    private static WorldBuilder BumpLastUpdate(WorldBuilder w)
    {
        var chunks = w.ChunksIn(0, 0).ToList();
        return w.Mutate(m =>
        {
            foreach (var (x, z) in chunks)
                m.Chunk(0, 0, x, z).SetPath("LastUpdate", 99_999_999L + x);
        });
    }

    [Fact]
    public async Task Scan_LastUpdateOnly_IsSameAfterDeepVerify()
    {
        var (l, r) = Pair(BaseWorld(), BumpLastUpdate(BaseWorld()));
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path);
            Assert.Equal(RowStatus.Same, root.Row("region/r.0.0.mca").Status);
            Assert.Equal(RowStatus.Same, root.Root.Status);
            Assert.Equal(0, root.Root.Counts.Different);

            var strict = await Scan(l.Path, r.Path, new CompareOptions(IgnoredTags: []));
            Assert.Equal(RowStatus.Different, strict.Row("region/r.0.0.mca").Status);
        }
    }

    [Fact]
    public async Task Fingerprinter_Deep_IgnoresLastUpdate()
    {
        var (l, r) = Pair(BaseWorld(), BumpLastUpdate(BaseWorld()));
        using (l) using (r)
        {
            string lp = l.File(Path.Combine("region", "r.0.0.mca")), rp = r.File(Path.Combine("region", "r.0.0.mca"));
            var fp = new Fingerprinter();
            var quickL = (await fp.QuickAsync(lp, FileKind.Region)).ValueOrThrow();
            var quickR = (await fp.QuickAsync(rp, FileKind.Region)).ValueOrThrow();
            Assert.False(quickL.ContentEquals(quickR));                       // bytes differ
            var deepL = (await fp.DeepAsync(lp, FileKind.Region)).ValueOrThrow();
            var deepR = (await fp.DeepAsync(rp, FileKind.Region)).ValueOrThrow();
            Assert.True(deepL.ContentEquals(deepR));                          // content does not

            var strict = new Fingerprinter(ignoredTags: TagIgnoreSet.Empty);
            Assert.False((await strict.DeepAsync(lp, FileKind.Region)).ValueOrThrow().ContentEquals((await strict.DeepAsync(rp, FileKind.Region)).ValueOrThrow()));
        }
    }

    [Fact]
    public void RegionDiffer_LastUpdateOnly_AllSame()
    {
        var (l, r) = Pair(BaseWorld(), BumpLastUpdate(BaseWorld()));
        using (l) using (r)
        {
            using var left = RegionFile.Open(l.File(Path.Combine("region", "r.0.0.mca"))).ValueOrThrow();
            using var right = RegionFile.Open(r.File(Path.Combine("region", "r.0.0.mca"))).ValueOrThrow();

            var cells = RegionDiffer.Diff(left, right, DiffOptions.Default);
            Assert.All(cells, c => Assert.Equal(ChunkDiffStatus.Same, c.Status));

            var strict = RegionDiffer.Diff(left, right, new DiffOptions(IgnoredTags: TagIgnoreSet.Empty));
            Assert.All(strict, c => Assert.Equal(ChunkDiffStatus.Different, c.Status));

            var chunk = RegionDiffer.DiffChunk(left, right, 3, 0, DiffOptions.Default).ValueOrThrow();
            Assert.False(chunk.HasChanges);
            Assert.DoesNotContain(chunk.Descendants(), n => n.Name == "LastUpdate");
        }
    }

    [Fact]
    public void Nbt_IgnoredTagInLevelDat_ViaCompareOptionsPaths()
    {
        // A user-supplied path applies to standalone files too.
        var a = new NbtCompound("") { new NbtCompound("Data") { new NbtLong("LastPlayed", 1), new NbtString("LevelName", "x") } };
        var b = new NbtCompound("") { new NbtCompound("Data") { new NbtLong("LastPlayed", 2), new NbtString("LevelName", "x") } };
        var set = new CompareOptions(IgnoredTags: ["Data/LastPlayed"]).EffectiveIgnoredTags;
        Assert.Equal(NbtCanonicalHasher.Hash(a, ignoredTags: set), NbtCanonicalHasher.Hash(b, ignoredTags: set));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
    }
}
