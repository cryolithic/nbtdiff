using System.Buffers.Binary;
using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

public class RegionFileTests
{
    private static NbtCompound Tiny(int x, int z) => new("") { new NbtInt("x", x), new NbtInt("z", z) };

    private static IEnumerable<ChunkSpec> TinyChunks(int count, byte scheme = ChunkRef.SchemeZLib) =>
        Enumerable.Range(0, count).Select(i => new ChunkSpec(i % 32, i / 32, Tiny(i % 32, i / 32), scheme, Timestamp: (uint)(1000 + i)));

    [Fact]
    public void Open_ReadsExactlyTheHeader()
    {
        var bytes = RegionWriter.Build(TinyChunks(1024));
        var counting = new CountingStream(new MemoryStream(bytes));

        using var region = NbtAssert.Ok(RegionFile.Open(counting, "r.0.0.mca"));

        Assert.Equal(RegionFile.HeaderSize, counting.BytesRead);
        Assert.Equal(1024, region.ChunkCount);
        Assert.Equal(bytes.Length, region.Length);
    }

    [Fact]
    public void ZeroChunks_IsOk()
    {
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build([])), "r.0.0.mca"));
        Assert.Equal(0, region.ChunkCount);
        Assert.Empty(region.Chunks);
        Assert.Null(region[0, 0]);
    }

    [Fact]
    public void ZeroLength_IsAnEmptyRegion()
    {
        // Minecraft writes 0-byte .mca files for regions it touched but never saved.
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(), "r.0.0.mca"));
        Assert.Equal(0, region.ChunkCount);
        Assert.Equal(0, region.Length);
        Assert.Null(region[5, 5]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(RegionFile.HeaderSize - 1)]
    public void TooShort_Fails(int length)
    {
        var failure = NbtAssert.Failed(RegionFile.Open(new MemoryStream(new byte[length]), "r.0.0.mca"));
        Assert.Contains("header", failure.Description);
    }

    [Fact]
    public void MissingFile_Fails()
    {
        var failure = NbtAssert.Failed(RegionFile.Open(Path.Combine(Path.GetTempPath(), "nbtdiff-nope", "r.0.0.mca")));
        Assert.Equal("Open region file", failure.Description);
    }

    [Fact]
    public void Chunks_OrderedByZThenX_AndIndexable()
    {
        var specs = new[] { new ChunkSpec(5, 1, Tiny(5, 1)), new ChunkSpec(0, 2, Tiny(0, 2)), new ChunkSpec(31, 0, Tiny(31, 0)) };
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build(specs)), "r.0.0.mca"));

        Assert.Equal([(31, 0), (5, 1), (0, 2)], region.Chunks.Select(c => (c.X, c.Z)));
        Assert.Same(region.Chunks[1], region[5, 1]);
        Assert.Null(region[4, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => region[32, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => region[0, -1]);
    }

    [Fact]
    public void Timestamps_ComeFromHeader()
    {
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build(TinyChunks(3))), "r.0.0.mca"));
        Assert.Equal([1000u, 1001u, 1002u], region.Chunks.Select(c => c.Timestamp));
    }

    [Theory]
    [InlineData(ChunkRef.SchemeGZip, NbtCompression.GZip)]
    [InlineData(ChunkRef.SchemeZLib, NbtCompression.ZLib)]
    [InlineData(ChunkRef.SchemeNone, NbtCompression.None)]
    public void ReadNbt_SupportedSchemes(byte scheme, NbtCompression expected)
    {
        var data = WorldBuilder.MakeChunk(1, 0, 0);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build([new ChunkSpec(0, 0, data, scheme)])), "r.0.0.mca"));
        var chunk = region[0, 0]!;

        Assert.Null(chunk.Compression);          // nothing read yet
        Assert.Null(chunk.IsExternal);

        var root = NbtAssert.Ok(chunk.ReadNbt());
        NbtAssert.Equal(data, root);
        Assert.Equal(expected, chunk.Compression);
        Assert.Equal(scheme, chunk.SchemeByte);
        Assert.False(chunk.IsExternal);
    }

    [Fact]
    public void ReadNbt_Lz4_FailsButPayloadReadable()
    {
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0), ChunkRef.SchemeLz4)])), "r.0.0.mca"));
        var chunk = region[0, 0]!;

        NbtAssert.Ok(chunk.ReadCompressedPayload());
        var failure = NbtAssert.Failed(chunk.ReadNbt());
        Assert.Contains("LZ4", failure.Description);
        Assert.Null(chunk.Compression);
    }

    [Fact]
    public void ReadCompressedPayload_IsExactlyTheStoredBytes()
    {
        var data = WorldBuilder.MakeChunk(1, 2, 3);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build([new ChunkSpec(2, 3, data, ChunkRef.SchemeGZip)])), "r.0.0.mca"));

        var payload = NbtAssert.Ok(region[2, 3]!.ReadCompressedPayload());
        Assert.Equal(RegionWriter.Compress(data, ChunkRef.SchemeGZip), payload);
    }

    [Fact]
    public void CorruptSlot_OffsetPastEof_OnlyThatChunkFails()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0)), new ChunkSpec(1, 0, Tiny(1, 0))]);
        // Slot (0,0) → point at sector 1000 of a file that has ~4.
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1000u << 8) | 1);

        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        Assert.Equal(2, region.ChunkCount);

        var bad = region[0, 0]!;
        Assert.True(bad.IsCorruptHeader);
        Assert.Contains("file is only", bad.HeaderError);
        Assert.Contains("(0, 0)", NbtAssert.Failed(bad.ReadNbt()).Description);
        Assert.Contains("(0, 0)", NbtAssert.Failed(bad.ReadCompressedPayload()).Description);

        NbtAssert.Ok(region[1, 0]!.ReadNbt());
    }

    [Fact]
    public void UnpaddedLastChunk_IsNotAnError()
    {
        // Real worlds (entities/ files especially) end right after the last chunk's bytes; the header still
        // says "1 sector" for it. That must read fine — it was reported as "unreadable" on 40+ files.
        var specs = Enumerable.Range(0, 5).Select(i => new ChunkSpec(i, 0, WorldBuilder.MakeChunk(3, i, 0))).ToList();
        var bytes = RegionWriter.Build(specs, new RegionWriteOptions(PadLastSector: false));
        Assert.NotEqual(0, bytes.Length % RegionFile.SectorSize);

        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        Assert.All(region.Chunks, c => Assert.False(c.IsCorruptHeader));
        foreach (var spec in specs)
            NbtAssert.Equal(spec.Data, NbtAssert.Ok(region[spec.X, spec.Z]!.ReadNbt()));
    }

    [Fact]
    public void PayloadRunningPastEof_FailsOnRead_NotOnOpen()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(3, 0, 0))], new RegionWriteOptions(PadLastSector: false));
        var cut = bytes[..(bytes.Length - 100)];   // genuinely truncated inside the payload
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(cut), "r.0.0.mca"));
        Assert.False(region[0, 0]!.IsCorruptHeader);
        var failure = NbtAssert.Failed(region[0, 0]!.ReadCompressedPayload());
        Assert.Contains("runs past the end", failure.Exception!.Message);
    }

    [Fact]
    public void CorruptSlot_OffsetInsideHeader()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0))]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1u << 8) | 1);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        Assert.Contains("inside the header", region[0, 0]!.HeaderError);
    }

    [Fact]
    public void CorruptSlot_ZeroSectorsButNonZeroOffset()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0))]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), 2u << 8);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        Assert.Equal(1, region.ChunkCount);
        Assert.Contains("zero sectors", region[0, 0]!.HeaderError);
    }

    [Fact]
    public void BadLengthPrefix_FailsOnRead()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0))]);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(RegionFile.HeaderSize), 999_999);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        var failure = NbtAssert.Failed(region[0, 0]!.ReadNbt());
        Assert.Contains("length prefix", failure.Exception!.Message);
    }

    [Fact]
    public void CorruptPayload_FailsOnRead()
    {
        // Break the zlib header; a truncated-but-well-formed prefix can inflate to something fNbt
        // happens to parse, so corruption has to be unambiguous for the test to be stable.
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeZLib)]);
        bytes[RegionFile.HeaderSize + 5] = 0x00;
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        var failure = NbtAssert.Failed(region[0, 0]!.ReadNbt());
        Assert.Contains("parse NBT", failure.Description);
        NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload());
    }

    [Fact]
    public void ExternalChunk_ReadsMccFile()
    {
        using var dir = new TempDir();
        var data = WorldBuilder.MakeChunk(1, 40, -60);
        var path = dir.File(Path.Combine("region", "r.1.-2.mca"));
        RegionWriter.Write(path, [new ChunkSpec(8, 4, data, ChunkRef.SchemeZLib, External: true)]);
        Assert.True(File.Exists(dir.File(Path.Combine("region", "c.40.-60.mcc"))));

        using var region = NbtAssert.Ok(RegionFile.Open(path));
        Assert.Equal(new RegionCoords(1, -2), region.Coords);
        var chunk = region[8, 4]!;
        Assert.Equal((40, -60), chunk.WorldCoords);

        NbtAssert.Equal(data, NbtAssert.Ok(chunk.ReadNbt()));
        Assert.True(chunk.IsExternal);
        Assert.Equal(NbtCompression.ZLib, chunk.Compression);
    }

    [Fact]
    public void ExternalChunk_MissingMcc_Fails()
    {
        using var dir = new TempDir();
        var path = dir.File("r.0.0.mca");
        RegionWriter.Write(path, [new ChunkSpec(0, 0, Tiny(0, 0), External: true)]);
        File.Delete(dir.File("c.0.0.mcc"));

        using var region = NbtAssert.Ok(RegionFile.Open(path));
        Assert.Contains("missing", NbtAssert.Failed(region[0, 0]!.ReadNbt()).Description);
    }

    [Fact]
    public void ExternalChunk_WithoutRegionCoords_Fails()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0), External: true)], coords: new RegionCoords(0, 0), writeExternal: (_, _) => { });
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "weird-name.mca"));
        Assert.Null(region.Coords);
        Assert.Null(region[0, 0]!.WorldCoords);
        Assert.Contains("no region coordinates", NbtAssert.Failed(region[0, 0]!.ReadNbt()).Description);
    }

    [Fact]
    public void Mcr_OpensLikeMca()
    {
        using var dir = new TempDir();
        var path = dir.File("r.3.-1.mcr");
        RegionWriter.Write(path, [new ChunkSpec(0, 0, Tiny(0, 0), ChunkRef.SchemeGZip)]);
        using var region = NbtAssert.Ok(RegionFile.Open(path));
        Assert.Equal(new RegionCoords(3, -1), region.Coords);
        NbtAssert.Ok(region[0, 0]!.ReadNbt());
    }

    [Fact]
    public void Defragmented_SameContentDifferentBytes()
    {
        var specs = TinyChunks(10).ToList();
        var sequential = RegionWriter.Build(specs);
        var reversed = RegionWriter.Build(specs, new RegionWriteOptions(SectorOrder.Reverse));
        Assert.NotEqual(sequential, reversed);

        using var a = NbtAssert.Ok(RegionFile.Open(new MemoryStream(sequential), "r.0.0.mca"));
        using var b = NbtAssert.Ok(RegionFile.Open(new MemoryStream(reversed), "r.0.0.mca"));
        foreach (var chunk in a.Chunks)
        {
            var other = b[chunk.X, chunk.Z]!;
            Assert.NotEqual(chunk.Offset, other.Offset);
            NbtAssert.Equal(NbtAssert.Ok(chunk.ReadNbt()), NbtAssert.Ok(other.ReadNbt()));
        }
    }

    [Fact]
    public void Fragmented_GapsBetweenChunks()
    {
        var specs = TinyChunks(3).ToList();
        var packed = RegionWriter.Build(specs);
        var gapped = RegionWriter.Build(specs, new RegionWriteOptions(GapSectors: 2));
        Assert.True(gapped.Length > packed.Length);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(gapped), "r.0.0.mca"));
        Assert.All(region.Chunks, c => NbtAssert.Ok(c.ReadNbt()));
    }

    [Fact]
    public void ConcurrentReads_AreSafe()
    {
        var specs = Enumerable.Range(0, 64).Select(i => new ChunkSpec(i % 32, i / 32, WorldBuilder.MakeChunk(5, i % 32, i / 32))).ToList();
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(RegionWriter.Build(specs)), "r.0.0.mca"));

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(region.Chunks, new ParallelOptions { MaxDegreeOfParallelism = 8 }, chunk =>
        {
            var result = chunk.ReadNbt();
            if (!result.Ok) failures.Add(result.Failure!.ToShortString());
            else if (result.Value!.Get<NbtInt>("xPos")!.Value != chunk.X) failures.Add($"wrong content for {chunk}");
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void FileHandle_ConcurrentReads()
    {
        using var dir = new TempDir();
        var path = dir.File("r.0.0.mca");
        RegionWriter.Write(path, Enumerable.Range(0, 64).Select(i => new ChunkSpec(i % 32, i / 32, WorldBuilder.MakeChunk(6, i % 32, i / 32))));
        using var region = NbtAssert.Ok(RegionFile.Open(path));
        Parallel.ForEach(region.Chunks, chunk => NbtAssert.Ok(chunk.ReadNbt()));
    }
}
