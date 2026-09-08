namespace NbtDiff.Core.Tests;

public class NaturalStringComparerTests
{
    [Theory]
    [InlineData("r.2.0.mca", "r.10.0.mca", -1)]
    [InlineData("r.10.0.mca", "r.2.0.mca", 1)]
    [InlineData("r.0.0.mca", "r.0.0.mca", 0)]
    [InlineData("a", "b", -1)]
    [InlineData("a2", "a10", -1)]
    [InlineData("a10", "a10b", -1)]
    [InlineData("007", "7", 1)]     // equal value, more leading zeros sorts later
    [InlineData("x", "x1", -1)]
    [InlineData("", "a", -1)]
    [InlineData("map_9.dat", "map_10.dat", -1)]
    public void Compare(string x, string y, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(x, y)));
    }

    [Fact]
    public void SortsRegionNames()
    {
        var names = new[] { "r.10.0.mca", "r.2.0.mca", "r.0.0.mca", "r.1.0.mca", "r.0.10.mca", "r.0.2.mca" };
        var sorted = names.OrderBy(n => n, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["r.0.0.mca", "r.0.2.mca", "r.0.10.mca", "r.1.0.mca", "r.2.0.mca", "r.10.0.mca"], sorted);
    }
}

public class GlobMatcherTests
{
    [Theory]
    [InlineData("session.lock", "session.lock", "session.lock", true)]
    [InlineData("SESSION.LOCK", "session.lock", "session.lock", true)]
    [InlineData("*.log", "latest.log", "logs/latest.log", true)]
    [InlineData("*.log", "latest.log.gz", "logs/latest.log.gz", false)]
    [InlineData("r.?.?.mca", "r.0.0.mca", "region/r.0.0.mca", true)]
    [InlineData("r.?.?.mca", "r.10.0.mca", "region/r.10.0.mca", false)]
    [InlineData("region/*.mca", "r.0.0.mca", "region/r.0.0.mca", true)]
    [InlineData("region/*.mca", "r.0.0.mca", "DIM-1/region/r.0.0.mca", false)]
    [InlineData("**/region/*.mca", "r.0.0.mca", "DIM-1/region/r.0.0.mca", true)]
    [InlineData("**/region/*.mca", "r.0.0.mca", "region/r.0.0.mca", true)]
    [InlineData("playerdata/**", "x.dat", "playerdata/x.dat", true)]
    [InlineData("playerdata/**", "x.dat", "other/x.dat", false)]
    [InlineData("a.b", "aXb", "aXb", false)]   // '.' is literal
    public void IsMatch(string pattern, string name, string relativePath, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, name, relativePath));
    }
}

public class RowCountsTests
{
    [Fact]
    public void Arithmetic()
    {
        var a = RowCounts.Of(RowStatus.Same) + RowCounts.Of(RowStatus.Different) + RowCounts.Of(RowStatus.Different);
        Assert.Equal(3, a.Total);
        Assert.Equal(2, a.NonSame);
        var b = a - RowCounts.Of(RowStatus.Different);
        Assert.Equal(new RowCounts(0, 1, 0, 1, 0, 0, 0), b);
    }
}
