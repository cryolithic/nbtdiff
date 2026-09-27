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

    /// <summary>Row label: the tag name, or for a compound list item its index plus identity, e.g. <c>[3]: "minecraft:zombie"</c>.</summary>
    public string Name => Node.Name.Length == 0 ? "(root)" : ItemLabel is { } l ? $"{Node.Name}: {l}" : Node.Name;
    /// <summary>Identity of a compound list item (id / Name, UUID, Slot, or x,y,z position); <c>left → right</c> when the sides disagree. Null for other rows.</summary>
    public string? ItemLabel { get; }
    public string Path => Node.Path;
    public DiffKind Kind => Node.Kind;
    public bool IsChanged => Node.Kind != DiffKind.Unchanged;
    public bool HasChanges => Node.HasChanges;
    public int ChangedDescendants => Node.ChangedDescendants;
    public bool HasLeft => Node.Left is not null;
    public bool HasRight => Node.Right is not null;
    public bool IsArray => Node.Left is NbtByteArray or NbtIntArray or NbtLongArray || Node.Right is NbtByteArray or NbtIntArray or NbtLongArray;

    public string TypeText { get; }
    /// <summary>Right minus left for a changed number, e.g. <c>+400</c> or <c>−0.5</c>; null otherwise (#12).</summary>
    public string? DeltaText { get; }
    /// <summary>Entry-count change of a compound or list whose sizes differ, e.g. <c>+4</c>; null otherwise (#7, #12).</summary>
    public string? CountDeltaText { get; }
    /// <summary>The path as an NBT path, e.g. <c>Entities[0].NeoForgeData."naturesaura:time_alive"</c>.</summary>
    public string NbtPath => ToNbtPath(Node.Path);
    /// <summary>Muted text after a roll-up container's name (#12): <c>4 changes</c>, or <c>2 added</c> / <c>1 removed</c> when that is all it is.</summary>
    public string? InlineChangesText
    {
        get
        {
            if (!IsRollup) return null;
            var kinds = TopChanges(Node).Select(n => n.Kind).ToList();
            int n = kinds.Count;
            if (kinds.All(k => k == DiffKind.Added)) return $"{n} added";
            if (kinds.All(k => k == DiffKind.Removed)) return $"{n} removed";
            return $"{ChangedDescendants} {(ChangedDescendants == 1 ? "change" : "changes")}";
        }
    }
    /// <summary>The whole value (the cell text may be cut); null for a missing side.</summary>
    public string? LeftFullText => LeftToolTip ?? LeftValueText;
    public string? RightFullText => RightToolTip ?? RightValueText;
    /// <summary>A changed number's relative change against the left value, e.g. <c>+5.6%</c>; null when not meaningful.</summary>
    public string? PercentText => DeltaText is null ? null : Percent(Node.Left, Node.Right);
    /// <summary>For the detail pane: <c>int · changed</c>.</summary>
    public string KindText => $"{TypeText} · {(IsRollup ? "changes below" : Node.Kind switch
    {
        DiffKind.Unchanged => "unchanged",
        DiffKind.Added => "only on the right",
        DiffKind.Removed => "only on the left",
        DiffKind.ValueChanged => "changed",
        DiffKind.TypeChanged => "type changed",
        DiffKind.Moved => "moved",
        DiffKind.Renamed => "renamed",
        _ => Node.Kind.ToString(),
    })}";

    /// <summary>The changed nodes under <paramref name="node"/> whose parent is not itself changed.</summary>
    private static IEnumerable<DiffNode> TopChanges(DiffNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Kind != DiffKind.Unchanged) yield return child;
            else foreach (var inner in TopChanges(child)) yield return inner;
        }
    }

    private static string? Percent(NbtTag? left, NbtTag? right)
    {
        double? l = Number(left), r = Number(right);
        if (l is not { } a || r is not { } b || a == 0) return null;
        double p = (b - a) / Math.Abs(a) * 100;
        if (double.IsNaN(p) || double.IsInfinity(p)) return null;
        return (p >= 0 ? "+" : "−") + Math.Abs(p).ToString(Math.Abs(p) < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";
    }

    private static double? Number(NbtTag? tag) => tag switch
    {
        NbtByte b => (sbyte)b.Value,
        NbtShort s => s.Value,
        NbtInt i => i.Value,
        NbtLong l => l.Value,
        NbtFloat f => f.Value,
        NbtDouble d => d.Value,
        _ => null,
    };
    public string? LeftValueText { get; }
    public string? RightValueText { get; }
    public string? LeftToolTip { get; }
    public string? RightToolTip { get; }
    /// <summary>Unchanged itself, but something below it changed (#7): shown as a roll-up, never as "=".</summary>
    public bool IsRollup => Node.Kind == DiffKind.Unchanged && Node.HasChanges;
    public StateKind State => IsRollup ? StateKind.Different : StateKinds.Of(Node.Kind);
    /// <summary>Changed rows are tinted; a roll-up container only while collapsed, since expanded its changed children carry the colour.</summary>
    public RowTint Tint => IsRollup
        ? (IsExpanded ? RowTint.None : new RowTint(StateKind.Different, Rollup: true))
        : Node.Kind == DiffKind.Unchanged ? RowTint.None : new RowTint(StateKinds.Of(Node.Kind));
    public string StatusGlyph => IsRollup ? "●" : Glyph(Node.Kind);
    public string ToolTipText => $"{(Path.Length == 0 ? "(root)" : Path)}: " +
        (IsRollup ? $"{ChangedDescendants} {(ChangedDescendants == 1 ? "change" : "changes")} below" : Describe(Node.Kind));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph), nameof(Tint))]
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
        DeltaText = node.Kind == DiffKind.ValueChanged ? Delta(node.Left, node.Right) : null;
        CountDeltaText = (node.Left, node.Right) switch
        {
            (NbtCompound l, NbtCompound r) when l.Count != r.Count => Signed(r.Count - l.Count),
            (NbtList l, NbtList r) when l.Count != r.Count => Signed(r.Count - l.Count),
            _ => null,
        };
        (LeftValueText, LeftToolTip) = ValueText(node.Left, node.Array);
        (RightValueText, RightToolTip) = ValueText(node.Right, node.Array);
        if (node.Name.StartsWith('['))
        {
            string? l = IdentityOf(node.Left as NbtCompound), r = IdentityOf(node.Right as NbtCompound);
            ItemLabel = l is null ? r : r is null || l == r ? l : $"{l} → {r}";
        }
    }

    /// <summary>What a collapsed entity / block entity / item should read as: its id or Name (quoted), else UUID, else Slot; plus its block position when it has one.</summary>
    public static string? IdentityOf(NbtCompound? item)
    {
        if (item is null) return null;
        string? head = null;
        if (item.TryGet("id", out NbtTag id)) head = SnbtWriter.WriteValue(id);
        else if (item.TryGet("Name", out NbtTag name)) head = SnbtWriter.WriteValue(name);
        else if (item.TryGet("UUID", out NbtTag uuid)) head = SnbtWriter.WriteValue(uuid);
        else if (item.TryGet("Slot", out NbtTag slot)) head = "Slot " + SnbtWriter.WriteValue(slot);

        string? pos = item.TryGet("x", out NbtInt x) && item.TryGet("y", out NbtInt y) && item.TryGet("z", out NbtInt z)
            ? $"({x.Value}, {y.Value}, {z.Value})"
            : null;
        return (head, pos) switch
        {
            (null, null) => null,
            (null, _) => pos,
            (_, null) => head,
            _ => $"{head} @ {pos}",
        };
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

    /// <summary>Right minus left when both sides are the same numeric type; null otherwise.</summary>
    public static string? Delta(NbtTag? left, NbtTag? right) => (left, right) switch
    {
        (NbtByte l, NbtByte r) => Signed((sbyte)r.Value - (sbyte)l.Value),
        (NbtShort l, NbtShort r) => Signed(r.Value - l.Value),
        (NbtInt l, NbtInt r) => Signed((long)r.Value - l.Value),
        (NbtLong l, NbtLong r) => Signed(unchecked(r.Value - l.Value)),
        (NbtFloat l, NbtFloat r) => Signed((double)r.Value - l.Value),
        (NbtDouble l, NbtDouble r) => Signed(r.Value - l.Value),
        _ => null,
    };

    private static string Signed(long v) => v >= 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : "−" + (-(decimal)v).ToString(CultureInfo.InvariantCulture);

    private static string? Signed(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        string text = Math.Abs(v).ToString("0.######", CultureInfo.InvariantCulture);
        if (text == "0") text = Math.Abs(v).ToString("G3", CultureInfo.InvariantCulture);
        return (v >= 0 ? "+" : "−") + text;
    }

    /// <summary><c>Entities/[0]/NeoForgeData/naturesaura:time_alive</c> → <c>Entities[0].NeoForgeData."naturesaura:time_alive"</c>.</summary>
    public static string ToNbtPath(string path)
    {
        if (path.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var segment in path.Split('/'))
        {
            if (segment.StartsWith('[')) { sb.Append(segment); continue; }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(segment.All(SnbtParser.IsUnquotedChar) && segment.Length > 0 ? segment : "\"" + segment.Replace("\"", "\\\"") + "\"");
        }
        return sb.ToString();
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
