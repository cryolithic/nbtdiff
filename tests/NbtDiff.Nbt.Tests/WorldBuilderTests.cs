using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

public class WorldBuilderTests
{
    private static WorldBuilder Base() =>
        new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithRegion(-1, 0, chunks: 3).WithLevelDat().WithPlayer(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public void Write_ProducesWorldLayout()
    {
        using var dir = new TempDir();
        Base().Write(dir.Path);

        Assert.True(File.Exists(dir.File("level.dat")));
        Assert.True(File.Exists(dir.File("session.lock")));
        Assert.True(File.Exists(dir.File(Path.Combine("playerdata", "11111111-2222-3333-4444-555555555555.dat"))));
        Assert.True(File.Exists(dir.File(Path.Combine("region", "r.0.0.mca"))));
        Assert.True(File.Exists(dir.File(Path.Combine("region", "r.-1.0.mca"))));

        var level = NbtAssert.Ok(NbtDocument.Load(dir.File("level.dat")));
        Assert.Equal(NbtFormat.JavaNbt, level.Format.Format);
        Assert.Equal("Fixture World", ((NbtString)level.Root.GetPath("Data/LevelName")!).Value);

        using var region = NbtAssert.Ok(RegionFile.Open(dir.File(Path.Combine("region", "r.0.0.mca"))));
        Assert.Equal(40, region.ChunkCount);
        var chunk = NbtAssert.Ok(region[3, 1]!.ReadNbt());
        Assert.Equal(3, chunk.Get<NbtInt>("xPos")!.Value);
        Assert.Equal(1, chunk.Get<NbtInt>("zPos")!.Value);
    }

    [Fact]
    public void Deterministic_AcrossBuilders()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        Base().Write(a.Path);
        Base().Write(b.Path);
        Assert.Equal(File.ReadAllBytes(a.File(Path.Combine("region", "r.0.0.mca"))), File.ReadAllBytes(b.File(Path.Combine("region", "r.0.0.mca"))));
        Assert.Equal(File.ReadAllBytes(a.File("level.dat")), File.ReadAllBytes(b.File("level.dat")));
    }

    [Fact]
    public void Mutate_DoesNotTouchTheOriginal()
    {
        var original = Base();
        var mutated = original.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L));

        Assert.Equal(999L, mutated.Chunk(0, 0, 3, 1).Get<NbtLong>("InhabitedTime")!.Value);
        Assert.NotEqual(999L, original.Chunk(0, 0, 3, 1).Get<NbtLong>("InhabitedTime")!.Value);
        NbtAssert.Equal(original.Chunk(0, 0, 4, 1), mutated.Chunk(0, 0, 4, 1));
    }

    [Fact]
    public void Mutate_AddRemoveChunk()
    {
        var w = Base().Mutate(m => m.RemoveChunk(0, 0, 0, 0).AddChunk(0, 0, 20, 20));
        Assert.DoesNotContain((0, 0), w.ChunksIn(0, 0));
        Assert.Contains((20, 20), w.ChunksIn(0, 0));
        Assert.Equal(40, w.ChunksIn(0, 0).Count());
    }

    [Fact]
    public void Recompress_SameContentDifferentBytes()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var w = Base();
        w.Write(a.Path);
        w.Recompress(ChunkRef.SchemeGZip).Write(b.Path);

        var ra = NbtAssert.Ok(RegionFile.Open(a.File(Path.Combine("region", "r.0.0.mca"))));
        var rb = NbtAssert.Ok(RegionFile.Open(b.File(Path.Combine("region", "r.0.0.mca"))));
        Assert.NotEqual(NbtAssert.Ok(ra[0, 0]!.ReadCompressedPayload()), NbtAssert.Ok(rb[0, 0]!.ReadCompressedPayload()));
        Assert.Equal(NbtCompression.ZLib, ra[0, 0]!.Compression);
        Assert.Equal(NbtCompression.GZip, rb[0, 0]!.Compression);
        NbtAssert.Equal(NbtAssert.Ok(ra[0, 0]!.ReadNbt()), NbtAssert.Ok(rb[0, 0]!.ReadNbt()));
    }

    [Fact]
    public void TouchTimestamps_ChangesOnlyTheTimestampTable()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var w = Base();
        w.Write(a.Path);
        w.TouchTimestamps().Write(b.Path);

        var ba = File.ReadAllBytes(a.File(Path.Combine("region", "r.0.0.mca")));
        var bb = File.ReadAllBytes(b.File(Path.Combine("region", "r.0.0.mca")));
        Assert.Equal(ba.Length, bb.Length);
        Assert.Equal(ba.AsSpan(0, RegionFile.SectorSize).ToArray(), bb.AsSpan(0, RegionFile.SectorSize).ToArray());
        Assert.NotEqual(ba.AsSpan(RegionFile.SectorSize, RegionFile.SectorSize).ToArray(), bb.AsSpan(RegionFile.SectorSize, RegionFile.SectorSize).ToArray());
        Assert.Equal(ba.AsSpan(RegionFile.HeaderSize).ToArray(), bb.AsSpan(RegionFile.HeaderSize).ToArray());
    }

    [Fact]
    public void Defragment_MovesEveryChunk()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var w = Base();
        w.Write(a.Path);
        w.Defragment().Write(b.Path);

        using var ra = NbtAssert.Ok(RegionFile.Open(a.File(Path.Combine("region", "r.0.0.mca"))));
        using var rb = NbtAssert.Ok(RegionFile.Open(b.File(Path.Combine("region", "r.0.0.mca"))));
        Assert.All(ra.Chunks, c => Assert.NotEqual(c.Offset, rb[c.X, c.Z]!.Offset));
        Assert.Equal(NbtAssert.Ok(ra[7, 0]!.ReadCompressedPayload()), NbtAssert.Ok(rb[7, 0]!.ReadCompressedPayload()));
    }

    [Fact]
    public void MakeChunk_IsStableAcrossProcesses()
    {
        // Pinned values: if these change, every fixture-based expectation in other test projects moves too.
        var chunk = WorldBuilder.MakeChunk(42, 0, 0);
        Assert.Equal((95293L, 8494243L), (chunk.Get<NbtLong>("InhabitedTime")!.Value, chunk.Get<NbtLong>("LastUpdate")!.Value));
    }

    [Fact]
    public void NbtPath_GetAndSet()
    {
        var root = new NbtCompound("") { new NbtCompound("a") { new NbtInt("b", 1) } };
        Assert.Equal(1, ((NbtInt)root.GetPath("a/b")!).Value);
        Assert.Null(root.GetPath("a/c"));
        Assert.Null(root.GetPath("x/y"));

        root.SetPath("a/b", 2).SetPath("a/new", "v");
        Assert.Equal(2, ((NbtInt)root.GetPath("a/b")!).Value);
        Assert.Equal("v", ((NbtString)root.GetPath("a/new")!).Value);
        Assert.Throws<ArgumentException>(() => root.SetPath("missing/x", 1));
    }
}
