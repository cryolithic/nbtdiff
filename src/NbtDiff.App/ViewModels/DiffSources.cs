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

/// <summary>
/// A diff source whose sides can be written back to where they came from, so WinMerge-style copies
/// can be saved. <see cref="Save"/> runs off the UI thread and overwrites the original file or
/// chunk (after a one-time backup where a whole file is involved).
/// </summary>
public interface ISaveableDiffSource
{
    /// <param name="right">Which side: false writes the left file/chunk, true the right one.</param>
    /// <param name="root">The edited root tag of that side; never null.</param>
    LoadResult<object> Save(bool right, NbtTag root);

    /// <summary>The file <see cref="Save"/> would write for that side, or null when the side has nowhere to save.</summary>
    string? SavePath(bool right);
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
public sealed class FileDiffSource(string? leftPath, string? rightPath) : IDiffSource, ISaveableDiffSource
{
    public string? LeftPath => leftPath;
    public string? RightPath => rightPath;

    public string Title => PairTitle(leftPath, rightPath);

    private NbtDocument? _leftDoc, _rightDoc;

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
            _leftDoc = doc.Value!;
            left = _leftDoc.Root;
        }
        if (hasRight)
        {
            var doc = NbtDocument.Load(rightPath!);
            if (!doc.Ok) return LoadResult<TagPair>.Fail(new LoadFailure($"Right: {rightPath}") { Attempts = [doc.Failure!] });
            _rightDoc = doc.Value!;
            right = _rightDoc.Root;
        }
        return LoadResult<TagPair>.Success(new TagPair(left, right));
    }

    public string? SavePath(bool right) => (right ? _rightDoc : _leftDoc)?.Path;

    public LoadResult<object> Save(bool right, NbtTag root)
    {
        var doc = right ? _rightDoc : _leftDoc;
        if (doc is null)
            return LoadResult<object>.Fail(right
                ? "The right side has no loaded file to save to"
                : "The left side has no loaded file to save to");
        if (root is not NbtCompound compound)
            return LoadResult<object>.Fail("Only a compound root can be saved to a file");
        return doc.Save(compound).Map(d => (object)d);
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
public sealed class ChunkDiffSource(RegionFile? left, RegionFile? right, int x, int z, string regionTitle) : IDiffSource, ISaveableDiffSource
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

    public string? SavePath(bool saveRight) => (saveRight ? right : left)?.Path;

    public LoadResult<object> Save(bool saveRight, NbtTag root)
    {
        // "saveRight" so as not to shadow the `right` region captured from the constructor.
        var region = saveRight ? right : left;
        if (region is null)
            return LoadResult<object>.Fail(saveRight ? "The right region file is not open" : "The left region file is not open");
        if (root is not NbtCompound compound)
            return LoadResult<object>.Fail("Only a compound root can be saved as a chunk");
        return region.WriteChunk(x, z, compound).Map(c => (object)c);
    }
}
