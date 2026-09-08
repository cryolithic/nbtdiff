using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using fNbt;
using NbtDiff.App.Tree;
using NbtDiff.Core;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.App.ViewModels;

/// <summary>
/// Tree-row wrapper over a <see cref="DiffNode"/> for the aligned file compare view (DESIGN §5.3).
/// Immutable apart from the flat-tree state; identity (not <see cref="Path"/>, which repeats under
/// <see cref="KeyedAligner"/>) is what navigation keys on.
/// </summary>
public sealed partial class DiffNodeItem : ObservableObject, IFlatTreeNode<DiffNodeItem>
{
    /// <summary>Scalar text longer than this is cut in the cell; the tooltip carries the whole value.</summary>
    public const int MaxValueLength = 120;

    private readonly List<DiffNodeItem> _children = [];

    public DiffNode Node { get; }
    public DiffNodeItem? Parent { get; }
    public IReadOnlyList<DiffNodeItem> Children => _children;
    /// <summary>Pre-order position in the whole tree; changed nodes are navigated in this order.</summary>
    public int DfsIndex { get; private set; }

    public string Name => Node.Name.Length == 0 ? "(root)" : Node.Name;
    public string Path => Node.Path;
    public DiffKind Kind => Node.Kind;
    public bool IsChanged => Node.Kind != DiffKind.Unchanged;
    public bool HasChanges => Node.HasChanges;
    public int ChangedDescendants => Node.ChangedDescendants;
    public bool HasLeft => Node.Left is not null;
    public bool HasRight => Node.Right is not null;
    public bool IsArray => Node.Left is NbtByteArray or NbtIntArray or NbtLongArray || Node.Right is NbtByteArray or NbtIntArray or NbtLongArray;

    public string TypeText { get; }
    public string? LeftValueText { get; }
    public string? RightValueText { get; }
    public string? LeftToolTip { get; }
    public string? RightToolTip { get; }
    public string StatusGlyph => Glyph(Node.Kind);
    public string ToolTipText => $"{(Path.Length == 0 ? "(root)" : Path)}: {Describe(Node.Kind)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool _hasVisibleChildren;

    public int Depth { get; set; }

    public string ExpanderGlyph => !HasVisibleChildren ? "" : IsExpanded ? "▾" : "▸";

    private DiffNodeItem(DiffNode node, DiffNodeItem? parent)
    {
        Node = node;
        Parent = parent;
        TypeText = TypeDescription(node);
        (LeftValueText, LeftToolTip) = ValueText(node.Left, node.Array);
        (RightValueText, RightToolTip) = ValueText(node.Right, node.Array);
    }

    public static DiffNodeItem Build(DiffNode root)
    {
        int counter = 0;
        return Build(root, null, ref counter);
    }

    private static DiffNodeItem Build(DiffNode node, DiffNodeItem? parent, ref int counter)
    {
        var item = new DiffNodeItem(node, parent) { DfsIndex = counter++ };
        foreach (var child in node.Children)
            item._children.Add(Build(child, item, ref counter));
        return item;
    }

    /// <summary>This item and all descendants, pre-order.</summary>
    public IEnumerable<DiffNodeItem> Descendants()
    {
        yield return this;
        foreach (var c in _children)
            foreach (var d in c.Descendants())
                yield return d;
    }

    public static string Glyph(DiffKind kind) => kind switch
    {
        DiffKind.Unchanged => "=",
        DiffKind.Added => "▶",
        DiffKind.Removed => "◀",
        DiffKind.ValueChanged => "≠",
        DiffKind.TypeChanged => "≠",
        DiffKind.Moved => "↕",
        DiffKind.Renamed => "✎",
        _ => "?",
    };

    public static string Describe(DiffKind kind) => kind switch
    {
        DiffKind.Unchanged => "unchanged",
        DiffKind.Added => "only on the right",
        DiffKind.Removed => "only on the left",
        DiffKind.ValueChanged => "value changed",
        DiffKind.TypeChanged => "type changed",
        DiffKind.Moved => "moved (key order differs)",
        DiffKind.Renamed => "renamed",
        _ => kind.ToString(),
    };

    private static string TypeDescription(DiffNode node)
    {
        string l = TypeName(node.Left), r = TypeName(node.Right);
        if (node.Left is null) return r;
        if (node.Right is null) return l;
        return l == r ? l : $"{l} → {r}";
    }

    public static string TypeName(NbtTag? tag) => tag switch
    {
        null => "",
        NbtCompound => "compound",
        NbtList l => l.Count == 0 || l.ListType is NbtTagType.End or NbtTagType.Unknown ? "list" : $"list<{TypeName(l.ListType)}>",
        _ => TypeName(tag.TagType),
    };

    public static string TypeName(NbtTagType type) => type switch
    {
        NbtTagType.Byte => "byte",
        NbtTagType.Short => "short",
        NbtTagType.Int => "int",
        NbtTagType.Long => "long",
        NbtTagType.Float => "float",
        NbtTagType.Double => "double",
        NbtTagType.String => "string",
        NbtTagType.ByteArray => "byte[]",
        NbtTagType.IntArray => "int[]",
        NbtTagType.LongArray => "long[]",
        NbtTagType.List => "list",
        NbtTagType.Compound => "compound",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>Cell text and full tooltip text for one side.</summary>
    private static (string? Cell, string? ToolTip) ValueText(NbtTag? tag, ArrayDifference? diff)
    {
        switch (tag)
        {
            case null:
                return (null, null);
            case NbtCompound c:
                return (c.Count == 1 ? "{1 entry}" : "{" + c.Count.ToString(CultureInfo.InvariantCulture) + " entries}", null);
            case NbtList l:
                return (l.Count == 1 ? "[1 item]" : "[" + l.Count.ToString(CultureInfo.InvariantCulture) + " items]", null);
            case NbtByteArray ba:
                return ArrayText("byte", ba.Value.Length, diff);
            case NbtIntArray ia:
                return ArrayText("int", ia.Value.Length, diff);
            case NbtLongArray la:
                return ArrayText("long", la.Value.Length, diff);
            default:
            {
                string full = SnbtWriter.WriteValue(tag);
                string cell = full.Length > MaxValueLength ? full[..MaxValueLength] + "…" : full;
                return (cell, full);
            }
        }
    }

    private static (string, string?) ArrayText(string element, int length, ArrayDifference? diff)
    {
        string text = $"{element}[{length}]";
        if (diff is not null) text += $" · differs at [{diff.FirstDifference}]";
        return (text, null);
    }

    public override string ToString() => Node.ToString();
}
