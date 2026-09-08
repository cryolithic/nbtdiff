namespace NbtDiff.App.Tree;

/// <summary>
/// A tree node that <see cref="FlatTreeSource{T}"/> can project into a flat list. <see cref="Depth"/>,
/// <see cref="IsExpanded"/> and <see cref="HasVisibleChildren"/> are owned by the source; implementations
/// must raise change notifications for them so bound cells update.
/// </summary>
public interface IFlatTreeNode<T> where T : class, IFlatTreeNode<T>
{
    IReadOnlyList<T> Children { get; }
    T? Parent { get; }
    int Depth { get; set; }
    bool IsExpanded { get; set; }
    /// <summary>True when at least one child passes the current filter, i.e. the row deserves an expander.</summary>
    bool HasVisibleChildren { get; set; }
}
