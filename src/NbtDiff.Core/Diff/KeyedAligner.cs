using fNbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Core;

/// <summary>
/// Pairs compound list items by an identity key (entity <c>UUID</c>, block-entity <c>id</c>, item
/// <c>Slot</c>, …) instead of by position, so a reordered or partially removed entity list diffs as
/// the few real changes rather than a cascade. Lists whose items are not compounds fall back to
/// <see cref="IndexAligner"/>. Hash/diff agreement does <b>not</b> hold with this aligner: a reorder
/// hashes differently but diffs as unchanged — that is the point.
/// </summary>
public sealed class KeyedAligner : IListAligner
{
    public static readonly IReadOnlyList<string> DefaultKeyNames = ["UUID", "id", "Name", "Slot"];
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
            if (item.TryGet(name, out NbtTag tag))
                return $"{tag.TagType}:{SnbtWriter.Write(tag)}";
        }
        return null;
    }
}
