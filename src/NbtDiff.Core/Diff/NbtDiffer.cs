using fNbt;

namespace NbtDiff.Core;

/// <summary>
/// Structural diff of two tag trees. Walk order and equality rules mirror <see cref="NbtCanonicalHasher"/>
/// exactly: with the default <see cref="IndexAligner"/>, <c>Hash(a) == Hash(b)</c> ⇔ <c>Diff(a, b).ChangedDescendants == 0</c>.
/// </summary>
public static class NbtDiffer
{
    public static DiffNode Diff(NbtTag? left, NbtTag? right, DiffOptions? options = null)
    {
        if (left is null && right is null) throw new ArgumentException("At least one side must be present");
        var o = options ?? DiffOptions.Default;
        return Build("", "", left, right, o, o.Ignored.Root);
    }

    // `ig` is the ignore-trie node for this tag; lists pass it through unchanged, compounds descend per key.
    private static DiffNode Build(string name, string path, NbtTag? left, NbtTag? right, DiffOptions o, TagIgnoreSet.Node? ig)
    {
        if (left is null) return OneSided(name, path, right!, DiffKind.Added, ig);
        if (right is null) return OneSided(name, path, left, DiffKind.Removed, ig);
        if (left.TagType != right.TagType) return new DiffNode(name, path, left, right, DiffKind.TypeChanged, []);

        // Children are matched by key, so only the root can differ in name (null vs "" included).
        var kind = string.Equals(left.Name, right.Name, StringComparison.Ordinal) ? DiffKind.Unchanged : DiffKind.Renamed;

        switch (left)
        {
            case NbtCompound lc:
                return new DiffNode(name, path, left, right, kind, CompoundChildren(path, lc, (NbtCompound)right, o, ig));

            case NbtList ll:
            {
                var rl = (NbtList)right;
                // An empty list's element type is not content (matches the hasher's rule).
                if (ll.Count > 0 && rl.Count > 0 && ll.ListType != rl.ListType)
                    return new DiffNode(name, path, left, right, DiffKind.TypeChanged, []);
                var children = new List<DiffNode>();
                foreach (var (li, ri) in o.Aligner.Align(ll, rl))
                {
                    string childName = $"[{li ?? ri}]";
                    children.Add(Build(childName, Join(path, childName), li is int l ? ll[l] : null, ri is int r ? rl[r] : null, o, ig));
                }
                return new DiffNode(name, path, left, right, kind, children);
            }

            case NbtByteArray ba:
                return ArrayNode(name, path, left, right, kind, ba.Value, ((NbtByteArray)right).Value);
            case NbtIntArray ia:
                return ArrayNode(name, path, left, right, kind, ia.Value, ((NbtIntArray)right).Value);
            case NbtLongArray la:
                return ArrayNode(name, path, left, right, kind, la.Value, ((NbtLongArray)right).Value);

            default:
                return new DiffNode(name, path, left, right, ScalarEquals(left, right) ? kind : DiffKind.ValueChanged, []);
        }
    }

    private static List<DiffNode> CompoundChildren(string path, NbtCompound lc, NbtCompound rc, DiffOptions o, TagIgnoreSet.Node? ig)
    {
        var children = new List<DiffNode>(Math.Max(lc.Count, rc.Count));
        if (!o.CompoundOrderMatters)
        {
            // Sorted union of keys, so the output order is canonical regardless of input order.
            var keys = new SortedSet<string>(lc.Names, StringComparer.Ordinal);
            keys.UnionWith(rc.Names);
            foreach (var key in keys)
            {
                if (ig?.Ignores(key) == true) continue;
                children.Add(Build(key, Join(path, key), lc.Get(key), rc.Get(key), o, ig?.Child(key)));
            }
            return children;
        }

        // Order matters: left order, then right-only keys in right order. A common key whose rank
        // among the common keys differs between sides is Moved (unless it changed in another way);
        // ranking over common keys only keeps an insertion from flagging every later sibling.
        var rightRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in rc.Tags)
            if (lc.Contains(t.Name!) && ig?.Ignores(t.Name) != true) rightRank[t.Name!] = rightRank.Count;
        int leftRank = 0;
        foreach (var l in lc.Tags)
        {
            if (ig?.Ignores(l.Name) == true) continue;
            var r = rc.Get(l.Name!);
            var node = Build(l.Name!, Join(path, l.Name!), l, r, o, ig?.Child(l.Name));
            if (r is not null)
            {
                bool moved = rightRank[l.Name!] != leftRank++;
                if (moved && node.Kind == DiffKind.Unchanged)
                    node = node.WithKind(DiffKind.Moved);
            }
            children.Add(node);
        }
        foreach (var r in rc.Tags)
            if (!lc.Contains(r.Name!) && ig?.Ignores(r.Name) != true)
                children.Add(Build(r.Name!, Join(path, r.Name!), null, r, o, ig?.Child(r.Name)));
        return children;
    }

    private static DiffNode OneSided(string name, string path, NbtTag tag, DiffKind kind, TagIgnoreSet.Node? ig)
    {
        var children = new List<DiffNode>();
        switch (tag)
        {
            case NbtCompound c:
                foreach (var child in c.Tags)
                {
                    if (ig?.Ignores(child.Name) == true) continue;
                    children.Add(OneSided(child.Name!, Join(path, child.Name!), child, kind, ig?.Child(child.Name)));
                }
                break;
            case NbtList l:
                for (int i = 0; i < l.Count; i++)
                    children.Add(OneSided($"[{i}]", Join(path, $"[{i}]"), l[i], kind, ig));
                break;
        }
        return kind == DiffKind.Added
            ? new DiffNode(name, path, null, tag, kind, children)
            : new DiffNode(name, path, tag, null, kind, children);
    }

    private static DiffNode ArrayNode<T>(string name, string path, NbtTag left, NbtTag right, DiffKind kind, T[] a, T[] b) where T : IEquatable<T>
    {
        int common = Math.Min(a.Length, b.Length);
        int first = common;
        for (int i = 0; i < common; i++)
        {
            if (!a[i].Equals(b[i])) { first = i; break; }
        }
        if (first == common && a.Length == b.Length)
            return new DiffNode(name, path, left, right, kind, []);
        return new DiffNode(name, path, left, right, DiffKind.ValueChanged, [], new ArrayDifference(first, a.Length, b.Length));
    }

    /// <summary>Scalar equality with the hasher's rules: floats by bit pattern, strings ordinal.</summary>
    public static bool ScalarEquals(NbtTag left, NbtTag right) => left switch
    {
        NbtByte b => b.Value == ((NbtByte)right).Value,
        NbtShort s => s.Value == ((NbtShort)right).Value,
        NbtInt i => i.Value == ((NbtInt)right).Value,
        NbtLong l => l.Value == ((NbtLong)right).Value,
        NbtFloat f => BitConverter.SingleToInt32Bits(f.Value) == BitConverter.SingleToInt32Bits(((NbtFloat)right).Value),
        NbtDouble d => BitConverter.DoubleToInt64Bits(d.Value) == BitConverter.DoubleToInt64Bits(((NbtDouble)right).Value),
        NbtString s => string.Equals(s.Value, ((NbtString)right).Value, StringComparison.Ordinal),
        _ => throw new ArgumentException($"{left.TagType} is not a scalar", nameof(left)),
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "/" + name;
}
