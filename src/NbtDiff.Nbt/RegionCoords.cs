using System.Text.RegularExpressions;

namespace NbtDiff.Nbt;

/// <summary>Region position in region units; one region is 32×32 chunks.</summary>
public readonly partial record struct RegionCoords(int X, int Z)
{
    public const int ChunksPerAxis = 32;

    /// <summary>Absolute chunk coordinates of a chunk at local (x, z) inside this region.</summary>
    public (int X, int Z) ChunkAt(int localX, int localZ) =>
        (X * ChunksPerAxis + localX, Z * ChunksPerAxis + localZ);

    /// <summary>Parses <c>r.&lt;x&gt;.&lt;z&gt;.mca</c> (or any extension). Null when the name has another shape.</summary>
    public static RegionCoords? FromFileName(string path)
    {
        var name = Path.GetFileName(path);
        var m = FileNamePattern().Match(name);
        if (!m.Success) return null;
        return new RegionCoords(int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["z"].Value));
    }

    public override string ToString() => $"r.{X}.{Z}";

    [GeneratedRegex(@"^r\.(?<x>-?\d+)\.(?<z>-?\d+)\.[A-Za-z0-9]+$")]
    private static partial Regex FileNamePattern();
}
