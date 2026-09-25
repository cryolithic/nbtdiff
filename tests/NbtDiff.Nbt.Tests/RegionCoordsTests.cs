namespace NbtDiff.Nbt.Tests;

public class RegionCoordsTests
{
    [Theory]
    [InlineData("r.0.0.mca", 0, 0)]
    [InlineData("r.-1.3.mcr", -1, 3)]
    [InlineData("region/r.2.2.mca", 2, 2)]
    public void FromFileName_Parses(string path, int x, int z)
    {
        Assert.Equal(new RegionCoords(x, z), RegionCoords.FromFileName(path));
    }

    [Fact]
    public void FromFileName_ParsesNativeAbsolutePath()
    {
        // Built with the platform's separator: a backslash is a filename character on Linux.
        var path = Path.Combine(Path.GetTempPath(), "worlds", "DIM-1", "region", "r.-12.7.mca");
        Assert.Equal(new RegionCoords(-12, 7), RegionCoords.FromFileName(path));
    }

    [Theory]
    [InlineData("r.1.mca")]
    [InlineData("c.0.0.mcc")]
    [InlineData("r.a.b.mca")]
    [InlineData("level.dat")]
    [InlineData("r.0.0")]
    public void FromFileName_RejectsOtherShapes(string path)
    {
        Assert.Null(RegionCoords.FromFileName(path));
    }

    [Fact]
    public void ChunkAt_ScalesBy32()
    {
        Assert.Equal((-32, 96), new RegionCoords(-1, 3).ChunkAt(0, 0));
        Assert.Equal((-1, 127), new RegionCoords(-1, 3).ChunkAt(31, 31));
    }
}
