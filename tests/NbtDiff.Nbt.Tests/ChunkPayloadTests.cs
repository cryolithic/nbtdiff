using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

public class ChunkPayloadTests
{
    private static RegionFile Open(byte[] bytes) => RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca").ValueOrThrow();

    [Fact]
    public void Scratch_IsUsedWhenLargeEnough()
    {
        var data = WorldBuilder.MakeChunk(1, 0, 0);
        using var region = Open(RegionWriter.Build([new ChunkSpec(0, 0, data)]));
        var scratch = new byte[ChunkRef.MaxInlinePayload];

        var payload = NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload(scratch));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload.Bytes, out var seg) && ReferenceEquals(seg.Array, scratch));
        Assert.Equal(ChunkRef.SchemeZLib, payload.Scheme);
        Assert.False(payload.IsExternal);
        Assert.Equal(NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload()), payload.Bytes.ToArray());

        NbtAssert.Equal(data, NbtAssert.Ok(region[0, 0]!.ParseNbt(payload)));
    }

    [Fact]
    public void Scratch_TooSmall_Allocates()
    {
        using var region = Open(RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0))]));
        var scratch = new byte[16];
        var payload = NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload(scratch));
        Assert.True(payload.Bytes.Length > 16);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(payload.Bytes, out var seg) && !ReferenceEquals(seg.Array, scratch));
    }

    [Fact]
    public void External_IgnoresScratch()
    {
        using var dir = new TempDir();
        var path = dir.File("r.0.0.mca");
        var data = WorldBuilder.MakeChunk(1, 0, 0);
        RegionWriter.Write(path, [new ChunkSpec(0, 0, data, External: true)]);
        using var region = RegionFile.Open(path).ValueOrThrow();

        var payload = NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload(new byte[ChunkRef.MaxInlinePayload]));
        Assert.True(payload.IsExternal);
        Assert.Equal(ChunkRef.SchemeZLib, payload.Scheme);
        NbtAssert.Equal(data, NbtAssert.Ok(region[0, 0]!.ParseNbt(payload)));
    }

    [Fact]
    public void ParseNbt_UnsupportedScheme_Fails()
    {
        using var region = Open(RegionWriter.Build([new ChunkSpec(0, 0, WorldBuilder.MakeChunk(1, 0, 0), ChunkRef.SchemeLz4)]));
        var payload = NbtAssert.Ok(region[0, 0]!.ReadCompressedPayload(null));
        Assert.Contains("LZ4", NbtAssert.Failed(region[0, 0]!.ParseNbt(payload)).Description);
    }
}
