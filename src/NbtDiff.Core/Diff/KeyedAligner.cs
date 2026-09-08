using fNbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Core;

/// <summary>
/// Pairs compound list items by an identity key (entity <c>UUID</c>, block-entity <c>id</c>, item
/// <c>Slot</c>, …) instead of by position, so a reordered or partially removed entity list diffs as
/// the few real changes rather than a cascade. Lists whose items are not compounds fall back to
/// <see cref="IndexAligner"/>. Hash/diff agreement holds when the hasher is given the same aligner
/// (<see cref="NbtCanonicalHasher.Hash"/>'s <c>keyedLists</c>): it then hashes keyed items in key order,
/// so a reorder neither hashes differently nor diffs.
/// </summary>
public sealed class KeyedAligner : IListAligner
{
    /// <summary>
    /// Tried in order. Block entities have no UUID but do have <c>x</c>/<c>y</c>/<c>z</c>; that position is
    /// tried right after <c>UUID</c> (see <see cref="PositionKey"/>) so two chests are not paired by
    /// their shared <c>id</c>. <c>Y</c> keys chunk sections.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultKeyNames = ["UUID", PositionKey, "id", "Name", "Slot", "Y"];
    /// <summary>Pseudo key name: the compound's integer <c>x</c>, <c>y</c>, <c>z</c> children together.</summary>
    public const string PositionKey = "{x,y,z}";
    public static readonly KeyedAligner Default = new(DefaultKeyNames);

    private readonly string[] _keyNames;

    /// <param name="keyNames">Tried in order; an item's key is the value of the first name present in it.</param>
    public KeyedAligner(IEnumerable<string> keyNames)
    {
        _keyNames = keyNames.ToArray();
        if (_keyNames.Length == 0) throw new ArgumentException("At least one key name is required", nameof(keyNames));
    }

    public IReadOnlyList<string> KeyNames => _keyNames;

    public IReadOnlyList<(int? Left, int? Right)> Align(NbtList left, NbtList right)
    {
        if (left.Count == 0 || right.Count == 0 || left.ListType != NbtTagType.Compound || right.ListType != NbtTagType.Compound)
            return IndexAligner.Instance.Align(left, right);

        // Right side: per key, the indices carrying it in order; plus the unkeyed indices in order.
        var rightByKey = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        var rightUnkeyed = new Queue<int>();
        for (int r = 0; r < right.Count; r++)
        {
            var key = KeyOf((NbtCompound)right[r]);
            if (key is null) rightUnkeyed.Enqueue(r);
            else (rightByKey.TryGetValue(key, out var q) ? q : rightByKey[key] = new Queue<int>()).Enqueue(r);
        }

        var pairs = new List<(int? Left, int? Right)>(Math.Max(left.Count, right.Count));
        var matchedRight = new bool[right.Count];
        for (int l = 0; l < left.Count; l++)
        {
            var key = KeyOf((NbtCompound)left[l]);
            int? match = null;
            if (key is null)
            {
                if (rightUnkeyed.TryDequeue(out int r)) match = r;
            }
            else if (rightByKey.TryGetValue(key, out var q) && q.TryDequeue(out int r))
            {
                match = r;
            }
            if (match is int m) matchedRight[m] = true;
            pairs.Add((l, match));
        }

        for (int r = 0; r < right.Count; r++)
            if (!matchedRight[r]) pairs.Add((null, r));

        return pairs;
    }

    /// <summary>The item's identity, or null when none of the key names is present. Includes the tag type so <c>5</c> and <c>"5"</c> differ.</summary>
    public string? KeyOf(NbtCompound item)
    {
        foreach (var name in _keyNames)
        {
            if (name == PositionKey)
            {
                if (item.TryGet("x", out NbtInt x) && item.TryGet("y", out NbtInt y) && item.TryGet("z", out NbtInt z))
                    return $"xyz:{x.Value},{y.Value},{z.Value}";
                continue;
            }
            if (item.TryGet(name, out NbtTag tag))
                return $"{tag.TagType}:{SnbtWriter.Write(tag)}";
        }
        return null;
    }

    /// <summary>
    /// The order in which the hasher visits a compound list's items so that it agrees with <see cref="Align"/>:
    /// keyed items sorted by key (ties keep their original order, matching pairing by occurrence), then
    /// unkeyed items in original order. Non-compound and empty lists keep their order.
    /// </summary>
    public IEnumerable<NbtTag> CanonicalItems(NbtList list)
    {
        if (list.Count == 0 || list.ListType != NbtTagType.Compound) return list;
        var keyed = new List<(string Key, int Index)>();
        var unkeyed = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            var key = KeyOf((NbtCompound)list[i]);
            if (key is null) unkeyed.Add(i); else keyed.Add((key, i));
        }
        if (keyed.Count == 0) return list;
        keyed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key) is var c && c != 0 ? c : a.Index.CompareTo(b.Index));
        return keyed.Select(k => list[k.Index]).Concat(unkeyed.Select(i => list[i]));
    }
}
