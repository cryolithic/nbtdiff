using fNbt;

namespace NbtDiff.Core;

/// <summary>
/// One WinMerge-style copy: takes the value or subtree a <see cref="DiffNode"/> has on one side and
/// installs it at the same position on the other side — replacing what is there, inserting when the
/// source side is the only one that has it, or removing the target when only it has it. Missing
/// target containers along the way (a wholly absent side, a one-sided subtree) are synthesized, so
/// a copy can land on an empty side too. The trees are mutated in place; the caller re-runs
/// <see cref="NbtDiffer.Diff"/> afterwards.
/// </summary>
public static class NbtMerger
{
    /// <summary>
    /// <paramref name="chain"/> runs root → the node being copied; every element's <c>Left</c>/<c>Right</c>
    /// is the live tag at that depth, so positions are located by tag identity (paths are not unique
    /// under <see cref="KeyedAligner"/>). With <paramref name="toRight"/> the left value is copied
    /// onto the right side, and vice versa. Returns the destination side's root tag — the same
    /// instance as before unless the root itself was copied, deleted, or had to be synthesized, in
    /// which case the caller must adopt the returned (possibly null) tag as its new root.
    /// </summary>
    public static NbtTag? Copy(IReadOnlyList<DiffNode> chain, bool toRight)
    {
        if (chain.Count == 0) throw new ArgumentException("The chain must contain at least the root node.", nameof(chain));
        var node = chain[^1];
        var source = toRight ? node.Left : node.Right;
        var target = toRight ? node.Right : node.Left;

        if (chain.Count == 1)
            return source is null ? null : (NbtTag)source.Clone();

        // Walk the target side down to the leaf's parent, synthesizing any missing containers. The
        // diff nodes still say null for what was just created, so the live containers are tracked here.
        NbtTag? root = toRight ? chain[0].Right : chain[0].Left;
        NbtContainerTag? parent = null;
        for (int depth = 0; depth < chain.Count - 1; depth++)
        {
            var n = chain[depth];
            var container = toRight ? n.Right : n.Left;
            if (container is null)
            {
                // The source side of an ancestor always exists (it holds the node being copied).
                container = Synthesize((toRight ? n.Left : n.Right)!, n.Name);
                if (depth == 0) root = container;
                else Attach(parent!, container, n.Name);
            }
            parent = (NbtContainerTag)container;
        }

        switch (parent)
        {
            case NbtCompound compound:
                // The setter replaces an existing key in place (order kept) and appends a new one.
                if (source is null) compound.Remove(node.Name);
                else compound[node.Name] = CloneForCompound(source, node.Name);
                break;

            case NbtList list:
                if (source is null)
                {
                    int at = IndexIn(list, target, node);
                    list.RemoveAt(at);
                    // An emptied list must read back as a saveable empty list (type End), matching
                    // how the differ and hasher treat empty lists' element type as non-content.
                    if (list.Count == 0) list.ListType = NbtTagType.End;
                }
                else
                {
                    var clone = CloneForList(source);
                    if (target is not null) list[IndexIn(list, target, node)] = clone;
                    else list.Insert(InsertionIndex(toRight ? chain[^2].Left : chain[^2].Right, list, source), clone);
                }
                break;

            default:
                throw new InvalidOperationException($"Cannot copy {node.Path}: the destination parent is not a container.");
        }
        return root;
    }

    /// <summary>Where an inserted list item goes: the source index, so a copied-in entity lands among its siblings.</summary>
    private static int InsertionIndex(NbtTag? sourceParent, NbtList list, NbtTag source) =>
        sourceParent is NbtList siblings
            ? Math.Min(siblings.IndexOf(source), list.Count)
            : list.Count;

    private static int IndexIn(NbtList list, NbtTag? target, DiffNode node)
    {
        int at = target is null ? -1 : list.IndexOf(target);
        if (at < 0)
            throw new InvalidOperationException($"Cannot copy {node.Path}: the tag it replaced is no longer in its parent list.");
        return at;
    }

    /// <summary>A fresh empty container mirroring the source's kind. List items are unnamed; keys keep theirs.</summary>
    private static NbtTag Synthesize(NbtTag source, string name) => source switch
    {
        NbtList list => new NbtList(ContainerName(name), list.ListType),
        _ => new NbtCompound(ContainerName(name)),
    };

    private static void Attach(NbtContainerTag parent, NbtTag child, string name)
    {
        switch (parent)
        {
            case NbtCompound compound:
                compound[name] = child;
                break;
            case NbtList list:
                list.Insert(IndexFromName(name, list.Count), child);
                break;
        }
    }

    /// <summary>The position a synthesized list item takes: the index its <c>[i]</c> name carries.</summary>
    private static int IndexFromName(string name, int count) =>
        name.StartsWith('[') && int.TryParse(name[1..^1], out int i) ? Math.Min(i, count) : count;

    private static string? ContainerName(string name) => name.StartsWith('[') ? null : name;

    private static NbtTag CloneForCompound(NbtTag source, string name)
    {
        var clone = (NbtTag)source.Clone();
        clone.Name = name; // the compound setter requires the key and the tag name to agree
        return clone;
    }

    private static NbtTag CloneForList(NbtTag source)
    {
        var clone = (NbtTag)source.Clone();
        clone.Name = null; // list items are unnamed; a parsed SNBT item may carry ""
        return clone;
    }
}
