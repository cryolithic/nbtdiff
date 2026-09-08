using System.Buffers.Binary;
using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using Xunit.Sdk;

namespace NbtDiff.Core.Tests;

public class RegionDifferTests
{
    private static WorldBuilder Base() => new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40);
    private static string RegionPath(TempDir d) => d.File(Path.Combine("region", "r.0.0.mca"));

    private static T Ok<T>(LoadResult<T> r) where T : class => r.Ok ? r.Value! : throw new XunitException(r.Failure!.ToDetailedString());

    private static (TempDir l, TempDir r, RegionFile left, RegionFile right) Open(WorldBuilder a, WorldBuilder b)
    {
        var l = new TempDir(); var r = new TempDir();
        a.Write(l.Path); b.Write(r.Path);
        return (l, r, Ok(RegionFile.Open(RegionPath(l))), Ok(RegionFile.Open(RegionPath(r))));
    }

    [Fact]
    public void Identical_AllSame_NoParsing()
    {
        var (l, r, left, right) = Open(Base(), Base());
        using (l) using (r) using (left) using (right)
        {
            var cells = RegionDiffer.Diff(left, right);
            Assert.Equal(40, cells.Count);
            Assert.All(cells, c => Assert.Equal(ChunkDiffStatus.Same, c.Status));
            // Same bytes → no chunk was parsed, so Compression is still unresolved... but payload was read.
            Assert.Equal(cells.Select(c => (c.Z, c.X)), cells.Select(c => (c.Z, c.X)).OrderBy(t => t));
        }
    }

    [Fact]
    public void Mutate_ExactlyOneDifferent()
    {
        var (l, r, left, right) = Open(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r) using (left) using (right)
        {
            var cells = RegionDiffer.Diff(left, right);
            var diff = Assert.Single(cells, c => c.Status != ChunkDiffStatus.Same);
            Assert.Equal((3, 1, ChunkDiffStatus.Different), (diff.X, diff.Z, diff.Status));
            Assert.Null(diff.Error);
        }
    }

    [Fact]
    public void Recompressed_ParsesAndFindsSame()
    {
        var (l, r, left, right) = Open(Base(), Base().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r) using (left) using (right)
        {
            Assert.All(RegionDiffer.Diff(left, right), c => Assert.Equal(ChunkDiffStatus.Same, c.Status));
        }
    }

    [Fact]
    public void AddedAndRemovedChunks()
    {
        var (l, r, left, right) = Open(Base(), Base().Mutate(m => m.AddChunk(0, 0, 20, 20).RemoveChunk(0, 0, 0, 0)));
        using (l) using (r) using (left) using (right)
        {
            var cells = RegionDiffer.Diff(left, right);
            Assert.Equal(41, cells.Count);
            Assert.Equal(ChunkDiffStatus.LeftOnly, cells.Single(c => (c.X, c.Z) == (0, 0)).Status);
            Assert.Equal(ChunkDiffStatus.RightOnly, cells.Single(c => (c.X, c.Z) == (20, 20)).Status);
            Assert.Equal(39, cells.Count(c => c.Status == ChunkDiffStatus.Same));
        }
    }

    [Fact]
    public void NullSides()
    {
        var (l, r, left, right) = Open(Base(), Base());
        using (l) using (r) using (left) using (right)
        {
            Assert.All(RegionDiffer.Diff(left, null), c => Assert.Equal(ChunkDiffStatus.LeftOnly, c.Status));
            Assert.All(RegionDiffer.Diff(null, right), c => Assert.Equal(ChunkDiffStatus.RightOnly, c.Status));
            Assert.Empty(RegionDiffer.Diff(null, null));
        }
    }

    [Fact]
    public void CorruptHeader_IsError()
    {
        using var d = new TempDir();
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0)), new ChunkSpec(1, 0, WorldBuilder.MakeChunk(1, 1, 0))]);
        File.WriteAllBytes(d.File("good.mca"), bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1000u << 8) | 1);
        File.WriteAllBytes(d.File("bad.mca"), bytes);

        using var good = Ok(RegionFile.Open(d.File("good.mca")));
        using var bad = Ok(RegionFile.Open(d.File("bad.mca")));

        var cells = RegionDiffer.Diff(good, bad);
        Assert.Equal(2, cells.Count);
        var err = cells.Single(c => (c.X, c.Z) == (0, 0));
        Assert.Equal(ChunkDiffStatus.Error, err.Status);
        Assert.Contains("(0, 0)", err.Error);
        Assert.Equal(ChunkDiffStatus.Same, cells.Single(c => (c.X, c.Z) == (1, 0)).Status);

        // One-sided but unreadable is still an error, not LeftOnly.
        Assert.Equal(ChunkDiffStatus.Error, RegionDiffer.Diff(bad, null).Single(c => (c.X, c.Z) == (0, 0)).Status);
    }

    [Fact]
    public void UndecompressableChunk_IsError()
    {
        using var d = new TempDir();
        RegionWriter.Write(d.File("a.mca"), [new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeLz4)]);
        RegionWriter.Write(d.File("b.mca"), [new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeZLib)]);
        using var a = Ok(RegionFile.Open(d.File("a.mca")));
        using var b = Ok(RegionFile.Open(d.File("b.mca")));
        var cell = Assert.Single(RegionDiffer.Diff(a, b));
        Assert.Equal(ChunkDiffStatus.Error, cell.Status);
        Assert.Contains("LZ4", cell.Error);
    }

    [Fact]
    public async Task WithDeepFingerprints_MatchesWithout()
    {
        var mutated = Base().Mutate(m =>
        {
            m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L);
            m.Chunk(0, 0, 5, 0).SetPath("Status", "minecraft:empty");
        });
        var (l, r, left, right) = Open(Base().Recompress(ChunkRef.SchemeGZip), mutated);
        using (l) using (r) using (left) using (right)
        {
            var fp = new Fingerprinter();
            var lfp = Ok(await fp.DeepAsync(RegionPath(l), FileKind.Region));
            var rfp = Ok(await fp.DeepAsync(RegionPath(r), FileKind.Region));

            var without = RegionDiffer.Diff(left, right);
            var with = RegionDiffer.Diff(left, right, null, lfp, rfp);
            Assert.Equal(without, with);
            Assert.Equal(2, with.Count(c => c.Status == ChunkDiffStatus.Different));
        }
    }

    [Fact]
    public async Task WithQuickFingerprints_EqualHashesTrusted_UnequalVerified()
    {
        // Recompressed: quick hashes all differ, yet content is identical → must come out Same.
        var (l, r, left, right) = Open(Base(), Base().Recompress(ChunkRef.SchemeGZip).Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r) using (left) using (right)
        {
            var fp = new Fingerprinter();
            var lfp = Ok(await fp.QuickAsync(RegionPath(l), FileKind.Region));
            var rfp = Ok(await fp.QuickAsync(RegionPath(r), FileKind.Region));
            Assert.All(lfp.Region!.ChunkHashes, kv => Assert.NotEqual(kv.Value, rfp.Region!.ChunkHashes[kv.Key]));

            var cells = RegionDiffer.Diff(left, right, null, lfp, rfp);
            var diff = Assert.Single(cells, c => c.Status != ChunkDiffStatus.Same);
            Assert.Equal((3, 1), (diff.X, diff.Z));
        }
    }

    [Fact]
    public async Task WithFingerprints_ChunkErrorsPropagate()
    {
        using var d = new TempDir();
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0))]);
        File.WriteAllBytes(d.File("good.mca"), bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1000u << 8) | 1);
        File.WriteAllBytes(d.File("bad.mca"), bytes);
        using var good = Ok(RegionFile.Open(d.File("good.mca")));
        using var bad = Ok(RegionFile.Open(d.File("bad.mca")));
        var fp = new Fingerprinter();
        var gfp = Ok(await fp.DeepAsync(d.File("good.mca"), FileKind.Region));
        var bfp = Ok(await fp.DeepAsync(d.File("bad.mca"), FileKind.Region));

        var cell = Assert.Single(RegionDiffer.Diff(good, bad, null, gfp, bfp));
        Assert.Equal(ChunkDiffStatus.Error, cell.Status);
        Assert.NotNull(cell.Error);
    }

    [Fact]
    public void OrderMatters_Option_Respected()
    {
        // Same chunk written with a reordered root compound: default says Same, ordered says Different.
        var chunk = WorldBuilder.MakeChunk(1, 0, 0);
        var reordered = new NbtCompound("", chunk.Tags.Reverse().Select(t => (NbtTag)t.Clone()));
        using var d = new TempDir();
        RegionWriter.Write(d.File("a.mca"), [new ChunkSpec(0, 0, chunk)]);
        RegionWriter.Write(d.File("b.mca"), [new ChunkSpec(0, 0, reordered)]);
        using var a = Ok(RegionFile.Open(d.File("a.mca")));
        using var b = Ok(RegionFile.Open(d.File("b.mca")));
        Assert.Equal(ChunkDiffStatus.Same, RegionDiffer.Diff(a, b).Single().Status);
        Assert.Equal(ChunkDiffStatus.Different, RegionDiffer.Diff(a, b, new DiffOptions(CompoundOrderMatters: true)).Single().Status);
    }

    [Fact]
    public void DiffChunk_SingleValueChange()
    {
        var (l, r, left, right) = Open(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r) using (left) using (right)
        {
            var node = Ok(RegionDiffer.DiffChunk(left, right, 3, 1));
            Assert.Equal(1, node.ChangedDescendants);
            var changed = Assert.Single(node.Descendants(), n => n.Kind != DiffKind.Unchanged);
            Assert.Equal("InhabitedTime", changed.Path);
            Assert.Equal(DiffKind.ValueChanged, changed.Kind);
            Assert.Equal(999L, ((NbtLong)changed.Right!).Value);
            Assert.All(node.Children.Where(c => c.Name != "InhabitedTime"), c => Assert.Equal(0, c.ChangedDescendants));

            Assert.Equal(0, Ok(RegionDiffer.DiffChunk(left, right, 4, 1)).ChangedDescendants);
        }
    }

    [Fact]
    public void DiffChunk_OneSidedAndMissing()
    {
        var (l, r, left, right) = Open(Base(), Base().Mutate(m => m.AddChunk(0, 0, 20, 20).RemoveChunk(0, 0, 0, 0)));
        using (l) using (r) using (left) using (right)
        {
            Assert.Equal(DiffKind.Added, Ok(RegionDiffer.DiffChunk(left, right, 20, 20)).Kind);
            Assert.Equal(DiffKind.Removed, Ok(RegionDiffer.DiffChunk(left, right, 0, 0)).Kind);
            Assert.Equal(DiffKind.Removed, Ok(RegionDiffer.DiffChunk(left, null, 5, 0)).Kind);
            var missing = RegionDiffer.DiffChunk(left, right, 31, 31);
            Assert.False(missing.Ok);
            Assert.Contains("(31, 31)", missing.Failure!.Description);
        }
    }

    [Fact]
    public void DiffChunk_ReadFailure_IsFailureResult()
    {
        using var d = new TempDir();
        RegionWriter.Write(d.File("a.mca"), [new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeLz4)]);
        RegionWriter.Write(d.File("b.mca"), [new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0))]);
        using var a = Ok(RegionFile.Open(d.File("a.mca")));
        using var b = Ok(RegionFile.Open(d.File("b.mca")));
        var result = RegionDiffer.DiffChunk(a, b, 0, 0);
        Assert.False(result.Ok);
        Assert.StartsWith("Left chunk", result.Failure!.Description);
        Assert.Contains("LZ4", result.Failure.ToDetailedString());
    }
}
