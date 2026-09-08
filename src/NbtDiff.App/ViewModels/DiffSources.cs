using fNbt;
using NbtDiff.Nbt;

namespace NbtDiff.App.ViewModels;

/// <summary>Both sides of a tag-level compare. A null side is absent (whole tree Added/Removed).</summary>
public sealed record TagPair(NbtTag? Left, NbtTag? Right);

/// <summary>Where a <see cref="FileCompareViewModel"/> gets its two trees. <see cref="Load"/> runs off the UI thread, once; re-diffs reuse the tags.</summary>
public interface IDiffSource
{
    string Title { get; }
    LoadResult<TagPair> Load();
}

/// <summary>In-memory pair, for tests and for callers that already parsed both sides.</summary>
public sealed class TagPairSource(string title, NbtTag? left, NbtTag? right) : IDiffSource
{
    public string Title => title;

    public LoadResult<TagPair> Load() =>
        left is null && right is null
            ? LoadResult<TagPair>.Fail("Nothing to compare")
            : LoadResult<TagPair>.Success(new TagPair(left, right));
}

/// <summary>Two standalone NBT/SNBT files; a null or missing path is an absent side.</summary>
public sealed class FileDiffSource(string? leftPath, string? rightPath) : IDiffSource
{
    public string? LeftPath => leftPath;
    public string? RightPath => rightPath;

    public string Title => PairTitle(leftPath, rightPath);

    public LoadResult<TagPair> Load()
    {
        bool hasLeft = leftPath is not null && File.Exists(leftPath);
        bool hasRight = rightPath is not null && File.Exists(rightPath);
        if (!hasLeft && !hasRight)
            return LoadResult<TagPair>.Fail("Neither file exists");

        NbtTag? left = null, right = null;
        if (hasLeft)
        {
            var doc = NbtDocument.Load(leftPath!);
            if (!doc.Ok) return LoadResult<TagPair>.Fail(new LoadFailure($"Left: {leftPath}") { Attempts = [doc.Failure!] });
            left = doc.Value!.Root;
        }
        if (hasRight)
        {
            var doc = NbtDocument.Load(rightPath!);
            if (!doc.Ok) return LoadResult<TagPair>.Fail(new LoadFailure($"Right: {rightPath}") { Attempts = [doc.Failure!] });
            right = doc.Value!.Root;
        }
        return LoadResult<TagPair>.Success(new TagPair(left, right));
    }

    /// <summary><c>name</c> when both sides share a file name, else <c>left ↔ right</c>; an absent side reads <c>(missing)</c>.</summary>
    public static string PairTitle(string? leftPath, string? rightPath)
    {
        string l = leftPath is null ? "(missing)" : Path.GetFileName(leftPath);
        string r = rightPath is null ? "(missing)" : Path.GetFileName(rightPath);
        return l == r ? l : $"{l} ↔ {r}";
    }
}

/// <summary>One chunk slot of two open region files. The region files are owned by the caller (the region view) and must outlive this source's Load.</summary>
public sealed class ChunkDiffSource(RegionFile? left, RegionFile? right, int x, int z, string regionTitle) : IDiffSource
{
    public int X => x;
    public int Z => z;
    public string Title => $"{regionTitle} ({x}, {z})";

    public LoadResult<TagPair> Load()
    {
        var lc = left?[x, z];
        var rc = right?[x, z];
        if (lc is null && rc is null)
            return LoadResult<TagPair>.Fail($"No chunk at ({x}, {z}) on either side");

        NbtTag? l = null, r = null;
        if (lc is not null)
        {
            var read = lc.ReadNbt();
            if (!read.Ok) return LoadResult<TagPair>.Fail(new LoadFailure($"Left chunk ({x}, {z})") { Attempts = [read.Failure!] });
            l = read.Value;
        }
        if (rc is not null)
        {
            var read = rc.ReadNbt();
            if (!read.Ok) return LoadResult<TagPair>.Fail(new LoadFailure($"Right chunk ({x}, {z})") { Attempts = [read.Failure!] });
            r = read.Value;
        }
        return LoadResult<TagPair>.Success(new TagPair(l, r));
    }
}
