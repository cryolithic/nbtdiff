using System.Text;
using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using Xunit.Sdk;

namespace NbtDiff.Core.Tests;

public class FingerprinterTests
{
    private static readonly Fingerprinter Fp = new();

    private static WorldBuilder Base() => new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithLevelDat();
    private static string Region(TempDir d) => d.File(Path.Combine("region", "r.0.0.mca"));

    private static async Task<FileFingerprint> Quick(string path) => Ok(await Fp.QuickAsync(path, FileClassifier.Classify(path)));
    private static async Task<FileFingerprint> Deep(string path) => Ok(await Fp.DeepAsync(path, FileClassifier.Classify(path)));

    private static T Ok<T>(LoadResult<T> r) where T : class =>
        r.Ok ? r.Value! : throw new XunitException(r.Failure!.ToDetailedString());

    private static (TempDir left, TempDir right) Pair(WorldBuilder l, WorldBuilder r)
    {
        var a = new TempDir(); var b = new TempDir();
        l.Write(a.Path); r.Write(b.Path);
        return (a, b);
    }

    // ---- regions, quick tier ----

    [Theory]
    [InlineData("touch")]
    [InlineData("defrag")]
    [InlineData("fragment")]
    public async Task Quick_Region_IgnoresLayoutAndTimestamps(string variant)
    {
        var w = Base();
        var v = variant switch { "touch" => w.TouchTimestamps(), "defrag" => w.Defragment(), _ => w.Fragment(2) };
        var (l, r) = Pair(w, v);
        using (l) using (r)
        {
            var a = await Quick(Region(l));
            var b = await Quick(Region(r));
            Assert.True(a.ContentEquals(b));
            Assert.Equal(a.Region!.ChunkHashes, b.Region!.ChunkHashes);
            Assert.Equal(FingerprintTier.Quick, a.Tier);
            Assert.Equal(40, a.Region.ChunkCount);
            Assert.False(a.HasErrors);
        }
    }

    [Fact]
    public async Task Quick_Region_RecompressionLooksDifferent()
    {
        var (l, r) = Pair(Base(), Base().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r)
        {
            var a = await Quick(Region(l));
            var b = await Quick(Region(r));
            Assert.False(a.ContentEquals(b));
            Assert.All(a.Region!.ChunkHashes, kv => Assert.NotEqual(kv.Value, b.Region!.ChunkHashes[kv.Key]));
        }
    }

    [Fact]
    public async Task Quick_Region_MutationChangesOnlyThatChunk()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            var a = await Quick(Region(l));
            var b = await Quick(Region(r));
            Assert.False(a.ContentEquals(b));
            var differing = a.Region!.ChunkHashes.Where(kv => kv.Value != b.Region!.ChunkHashes[kv.Key]).Select(kv => kv.Key).ToList();
            Assert.Equal([(3, 1)], differing);
        }
    }

    [Fact]
    public async Task Quick_Region_DoesNotDecompress()
    {
        // LZ4 and a broken zlib header both fail to decompress; the quick tier must not notice.
        using var d = new TempDir();
        var path = d.File("r.0.0.mca");
        RegionWriter.Write(path, [
            new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeLz4),
            new ChunkSpec(1, 0, WorldBuilder.MakeChunk(1, 1, 0), ChunkRef.SchemeZLib),
        ]);
        var bytes = File.ReadAllBytes(path);
        // Second chunk sits in the sector after the first; break its zlib header byte.
        using (var region = RegionFile.Open(path).ValueOrThrow())
            bytes[region[1, 0]!.Offset + 5] = 0x00;
        File.WriteAllBytes(path, bytes);

        var quick = await Quick(path);
        Assert.False(quick.HasErrors);
        Assert.Equal(2, quick.Region!.ChunkHashes.Count);

        var deep = await Deep(path);
        Assert.True(deep.HasErrors);
        Assert.Equal(2, deep.Region!.ChunkErrors.Count);
        Assert.Contains("LZ4", deep.Region.ChunkErrors[(0, 0)]);
        Assert.Empty(deep.Region.ChunkHashes);
    }

    [Fact]
    public async Task Region_CorruptHeaderSlot_IsAnErrorNotAFailure()
    {
        using var d = new TempDir();
        var path = d.File("r.0.0.mca");
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0)), new ChunkSpec(1, 0, WorldBuilder.MakeChunk(1, 1, 0))]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1000u << 8) | 1);
        File.WriteAllBytes(path, bytes);

        foreach (var fp in new[] { await Quick(path), await Deep(path) })
        {
            Assert.True(fp.HasErrors);
            Assert.Single(fp.Region!.ChunkErrors);
            Assert.Single(fp.Region.ChunkHashes);
            Assert.Contains("(0, 0)", fp.Region.ChunkErrors[(0, 0)]);
        }

        // An error chunk never counts as equal, even to an identical file.
        var a = await Quick(path);
        var b = await Quick(path);
        Assert.Equal(a.Hash, b.Hash);
        Assert.False(a.ContentEquals(b));
    }

    // ---- regions, deep tier ----

    [Theory]
    [InlineData("touch")]
    [InlineData("defrag")]
    [InlineData("recompress")]
    public async Task Deep_Region_SameContentIsSame(string variant)
    {
        var w = Base();
        var v = variant switch { "touch" => w.TouchTimestamps(), "defrag" => w.Defragment(), _ => w.Recompress(ChunkRef.SchemeNone) };
        var (l, r) = Pair(w, v);
        using (l) using (r)
        {
            var a = await Deep(Region(l));
            var b = await Deep(Region(r));
            Assert.True(a.ContentEquals(b));
            Assert.Equal(FingerprintTier.Deep, a.Tier);
        }
    }

    [Fact]
    public async Task Deep_Region_MutationChangesOnlyThatChunk()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            var a = await Deep(Region(l));
            var b = await Deep(Region(r));
            var differing = a.Region!.ChunkHashes.Where(kv => kv.Value != b.Region!.ChunkHashes[kv.Key]).Select(kv => kv.Key).ToList();
            Assert.Equal([(3, 1)], differing);
        }
    }

    [Fact]
    public async Task Deep_Region_AddedChunk_ChangesTable()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.AddChunk(0, 0, 20, 20)));
        using (l) using (r)
        {
            var a = await Deep(Region(l));
            var b = await Deep(Region(r));
            Assert.False(a.ContentEquals(b));
            Assert.Equal(41, b.Region!.ChunkCount);
            Assert.DoesNotContain((20, 20), a.Region!.ChunkHashes.Keys);
        }
    }

    [Fact]
    public async Task Deep_Region_MatchesHasherOnParsedChunk()
    {
        using var d = new TempDir();
        Base().Write(d.Path);
        var fp = await Deep(Region(d));
        using var region = RegionFile.Open(Region(d)).ValueOrThrow();
        var chunk = region[5, 0]!.ReadNbt().ValueOrThrow();
        Assert.Equal(NbtCanonicalHasher.Hash(chunk), fp.Region!.ChunkHashes[(5, 0)]);
    }

    // ---- standalone NBT ----

    [Fact]
    public async Task Nbt_Recompressed_QuickDiffers_DeepSame()
    {
        using var d = new TempDir();
        var sample = NbtFixtures.SampleCompound();
        NbtFixtures.WriteFile(d.File("a.dat"), sample, NbtFormat.JavaNbt, NbtCompression.GZip);
        NbtFixtures.WriteFile(d.File("b.dat"), sample, NbtFormat.JavaNbt, NbtCompression.None);

        Assert.False((await Quick(d.File("a.dat"))).ContentEquals(await Quick(d.File("b.dat"))));
        Assert.True((await Deep(d.File("a.dat"))).ContentEquals(await Deep(d.File("b.dat"))));
    }

    [Fact]
    public async Task Nbt_KeyOrder_DeepSame_UnlessOrdered()
    {
        using var d = new TempDir();
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2) };
        var b = new NbtCompound("") { new NbtInt("y", 2), new NbtInt("x", 1) };
        NbtFixtures.WriteFile(d.File("a.dat"), a, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), b, NbtFormat.JavaNbt);

        Assert.True((await Deep(d.File("a.dat"))).ContentEquals(await Deep(d.File("b.dat"))));

        var ordered = new Fingerprinter(compoundOrderMatters: true);
        var oa = Ok(await ordered.DeepAsync(d.File("a.dat"), FileKind.Nbt));
        var ob = Ok(await ordered.DeepAsync(d.File("b.dat"), FileKind.Nbt));
        Assert.False(oa.ContentEquals(ob));
    }

    [Fact]
    public async Task Nbt_SnbtAndBinary_SameContent_DeepSame()
    {
        using var d = new TempDir();
        var sample = NbtFixtures.SampleCompound(7);
        NbtFixtures.WriteFile(d.File("a.dat"), sample, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("a.snbt"), sample, NbtFormat.Snbt);
        var bin = await Deep(d.File("a.dat"));
        var text = await Deep(d.File("a.snbt"));
        Assert.Equal(bin.Hash, text.Hash);
        Assert.False(bin.ContentEquals(text)); // different kinds are never "same" rows anyway
    }

    [Fact]
    public async Task Nbt_Mutated_DeepDiffers()
    {
        using var d = new TempDir();
        var a = NbtFixtures.SampleCompound();
        var b = (NbtCompound)a.Clone();
        b.SetPath("nested/depth", 2);
        NbtFixtures.WriteFile(d.File("a.dat"), a, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), b, NbtFormat.JavaNbt);
        Assert.False((await Deep(d.File("a.dat"))).ContentEquals(await Deep(d.File("b.dat"))));
    }

    [Fact]
    public async Task Nbt_Unparseable_DeepFails_QuickSucceeds()
    {
        using var d = new TempDir();
        File.WriteAllBytes(d.File("junk.dat"), Enumerable.Range(0, 100).Select(i => (byte)(i * 7)).ToArray());
        var quick = await Fp.QuickAsync(d.File("junk.dat"), FileKind.Nbt);
        Assert.True(quick.Ok);
        var deep = await Fp.DeepAsync(d.File("junk.dat"), FileKind.Nbt);
        Assert.False(deep.Ok);
        Assert.Equal(4, deep.Failure!.Attempts.Count);
    }

    // ---- text / json / binary ----

    [Fact]
    public async Task Text_LineEndings_QuickDiffers_DeepSame()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.json"), "{\n  \"a\": 1\n}\n");
        File.WriteAllText(d.File("b.json"), "{\r\n  \"a\": 1\r\n}\r\n");
        File.WriteAllText(d.File("c.json"), "{\n  \"a\": 2\n}\n");

        Assert.False((await Quick(d.File("a.json"))).ContentEquals(await Quick(d.File("b.json"))));
        Assert.True((await Deep(d.File("a.json"))).ContentEquals(await Deep(d.File("b.json"))));
        Assert.False((await Deep(d.File("a.json"))).ContentEquals(await Deep(d.File("c.json"))));
        Assert.Equal(FileKind.Json, (await Deep(d.File("a.json"))).Kind);
    }

    [Fact]
    public async Task Binary_DeepEqualsQuick()
    {
        using var d = new TempDir();
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(d.File("icon.png"), bytes);
        var q = await Quick(d.File("icon.png"));
        var dp = await Deep(d.File("icon.png"));
        Assert.Equal(q.Hash, dp.Hash);
        Assert.Equal(bytes.Length, q.Size);
        Assert.Equal(FileKind.Binary, q.Kind);
    }

    [Fact]
    public async Task Text_Size_IsOriginalNotNormalized()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.txt"), "x\r\ny\r\n");
        Assert.Equal(6, (await Deep(d.File("a.txt"))).Size);
    }

    // ---- failure modes ----

    [Fact]
    public async Task MissingFile_Fails()
    {
        var r = await Fp.QuickAsync(Path.Combine(Path.GetTempPath(), "nbtdiff-missing", "x.dat"), FileKind.Nbt);
        Assert.False(r.Ok);
        Assert.IsAssignableFrom<IOException>(r.Failure!.Exception);
    }

    [Fact]
    public async Task MissingRegion_Fails()
    {
        var r = await Fp.QuickAsync(Path.Combine(Path.GetTempPath(), "nbtdiff-missing", "r.0.0.mca"), FileKind.Region);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Directory_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Fp.QuickAsync(Path.GetTempPath(), FileKind.Directory));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Fp.DeepAsync(Path.GetTempPath(), FileKind.Directory));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var d = new TempDir();
        Base().Write(d.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Fp.QuickAsync(Region(d), FileKind.Region, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Fp.QuickAsync(d.File("level.dat"), FileKind.Nbt, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Fp.DeepAsync(Region(d), FileKind.Region, cts.Token));
    }

    [Fact]
    public async Task Region_ConcurrentFingerprints_AreIndependent()
    {
        using var d = new TempDir();
        Base().Write(d.Path);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Quick(Region(d))));
        Assert.All(results, r => Assert.Equal(results[0].Hash, r.Hash));
    }

    [Fact]
    public async Task EmptyTextFile_Hashes()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("e.txt"), "");
        var q = await Quick(d.File("e.txt"));
        Assert.Equal(0, q.Size);
        Assert.Equal(q.Hash, (await Deep(d.File("e.txt"))).Hash);
    }
}
