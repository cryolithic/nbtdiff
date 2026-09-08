using fNbt;

namespace NbtDiff.Core;

/// <param name="CompoundOrderMatters">Report a compound whose keys are reordered (children get <see cref="DiffKind.Moved"/>). Must match the hasher's setting for hash/diff agreement.</param>
/// <param name="ListAligner">How list items are paired; null means <see cref="IndexAligner"/>.</param>
/// <param name="IgnoredTags">Tag paths left out of the diff entirely; null means <see cref="TagIgnoreSet.Default"/>. Must match the hasher's set for hash/diff agreement.</param>
public sealed record DiffOptions(bool CompoundOrderMatters = false, IListAligner? ListAligner = null, TagIgnoreSet? IgnoredTags = null)
{
    public static readonly DiffOptions Default = new();
    public IListAligner Aligner => ListAligner ?? IndexAligner.Instance;
    public TagIgnoreSet Ignored => IgnoredTags ?? TagIgnoreSet.Default;
    /// <summary>The aligner as the hasher needs it: the <see cref="KeyedAligner"/> in use, or null for positional lists.</summary>
    public KeyedAligner? KeyedLists => ListAligner as KeyedAligner;
    /// <summary>Keyed list matching with every other option at its default.</summary>
    public static readonly DiffOptions Keyed = new(ListAligner: KeyedAligner.Default);
}

/// <summary>Pairs items of two lists. A null index on one side means the item exists only on the other.</summary>
public interface IListAligner
{
    IReadOnlyList<(int? Left, int? Right)> Align(NbtList left, NbtList right);
}

/// <summary>Pairs items by position; the longer list's tail is one-sided. Hash/diff agreement holds only with this aligner.</summary>
public sealed class IndexAligner : IListAligner
{
    public static readonly IndexAligner Instance = new();

    public IReadOnlyList<(int? Left, int? Right)> Align(NbtList left, NbtList right)
    {
        int common = Math.Min(left.Count, right.Count);
        int total = Math.Max(left.Count, right.Count);
        var pairs = new (int?, int?)[total];
        for (int i = 0; i < common; i++) pairs[i] = (i, i);
        for (int i = common; i < left.Count; i++) pairs[i] = (i, null);
        for (int i = common; i < right.Count; i++) pairs[i] = (null, i);
        return pairs;
    }
}
