using fNbt;

namespace NbtDiff.Core;

public enum DiffKind
{
    Unchanged,
    /// <summary>Present on the right only.</summary>
    Added,
    /// <summary>Present on the left only.</summary>
    Removed,
    /// <summary>Same type, different scalar/array value.</summary>
    ValueChanged,
    /// <summary>Different tag type; no children are compared.</summary>
    TypeChanged,
    /// <summary>Same content, different position among its siblings. Only reported when <see cref="DiffOptions.CompoundOrderMatters"/>.</summary>
    Moved,
    /// <summary>Same position and content, different name. Only possible at the root (children are matched by name).</summary>
    Renamed,
}

/// <param name="FirstDifference">Index of the first differing element, or the shorter length when one array is a prefix of the other.</param>
public sealed record ArrayDifference(int FirstDifference, int LeftLength, int RightLength);

/// <summary>One node of an aligned diff tree. Immutable.</summary>
public sealed class DiffNode
{
    /// <summary>Compound key, or <c>[i]</c> for a list index. Empty for the root.</summary>
    public string Name { get; }
    /// <summary>Slash-separated names from the root, e.g. <c>sections/[0]/block_states/data</c>. Empty for the root.</summary>
    public string Path { get; }
    public NbtTag? Left { get; }
    public NbtTag? Right { get; }
    public NbtTagType? LeftType => Left?.TagType;
    public NbtTagType? RightType => Right?.TagType;
    public DiffKind Kind { get; }
    public IReadOnlyList<DiffNode> Children { get; }
    /// <summary>Set when <see cref="Kind"/> is <see cref="DiffKind.ValueChanged"/> on an array tag.</summary>
    public ArrayDifference? Array { get; }
    /// <summary>
    /// Number of nodes in this subtree, <b>including this node</b>, whose kind is not
    /// <see cref="DiffKind.Unchanged"/>. Zero means the subtree can be collapsed as identical.
    /// </summary>
    public int ChangedDescendants { get; }
    public bool HasChanges => ChangedDescendants > 0;

    internal DiffNode(string name, string path, NbtTag? left, NbtTag? right, DiffKind kind, IReadOnlyList<DiffNode> children, ArrayDifference? array = null)
    {
        Name = name;
        Path = path;
        Left = left;
        Right = right;
        Kind = kind;
        Children = children;
        Array = array;
        int count = kind == DiffKind.Unchanged ? 0 : 1;
        foreach (var c in children) count += c.ChangedDescendants;
        ChangedDescendants = count;
    }

    internal DiffNode WithKind(DiffKind kind) => new(Name, Path, Left, Right, kind, Children, Array);

    /// <summary>Depth-first enumeration of this node and all descendants.</summary>
    public IEnumerable<DiffNode> Descendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Descendants())
                yield return d;
    }

    public override string ToString() => $"{(Path.Length == 0 ? "<root>" : Path)}: {Kind} ({LeftType?.ToString() ?? "-"} → {RightType?.ToString() ?? "-"})";
}
