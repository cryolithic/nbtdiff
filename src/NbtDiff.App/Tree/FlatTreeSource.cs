using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace NbtDiff.App.Tree;

/// <summary>
/// Projects a tree into the flat, virtualizable list a <c>DataGrid</c> can show (DESIGN §5.0). Only the
/// visible rows exist in <see cref="Rows"/>: collapsed subtrees cost nothing. Expanding or collapsing a
/// node touches only that node's slice; a filter change rebuilds the whole list in one reset.
/// Not thread-safe: call from the UI thread.
/// </summary>
public sealed class FlatTreeSource<T> where T : class, IFlatTreeNode<T>
{
    /// <summary>Above this many rows, an expand/collapse uses a single Reset instead of per-row inserts.</summary>
    public int BulkThreshold { get; set; } = 256;

    private readonly RangeObservableCollection<T> _rows = new();
    private readonly HashSet<T> _expanded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<T, bool> _visible = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<T> _roots = [];
    private Func<T, bool>? _filter;

    public ObservableCollection<T> Rows => _rows;
    public IReadOnlyList<T> Roots => _roots;

    /// <summary>Leaf filter; a node with children is visible when any child is. Null shows everything.</summary>
    public Func<T, bool>? Filter => _filter;

    public void SetRoots(IEnumerable<T> roots)
    {
        _roots = roots.ToList();
        _expanded.Clear();
        Rebuild();
    }

    public void SetFilter(Func<T, bool>? filter)
    {
        _filter = filter;
        Rebuild();
    }

    /// <summary>Re-evaluates the filter after node state changed. Returns true if any node's visibility flipped (and the list was rebuilt).</summary>
    public bool Refilter()
    {
        var before = new Dictionary<T, bool>(_visible, ReferenceEqualityComparer.Instance);
        ComputeVisibility();
        foreach (var (node, visible) in _visible)
            if (!before.TryGetValue(node, out var was) || was != visible)
            {
                Rebuild(recompute: false);
                return true;
            }
        return false;
    }

    /// <summary>Whether the node passed the filter at the last rebuild (false for unknown nodes).</summary>
    public bool IsVisible(T node) => _visible.TryGetValue(node, out var v) && v;

    public bool IsExpanded(T node) => _expanded.Contains(node);

    public void Toggle(T node)
    {
        if (_expanded.Contains(node)) Collapse(node);
        else Expand(node);
    }

    public void Expand(T node)
    {
        if (_expanded.Contains(node) || !node.HasVisibleChildren) return;
        _expanded.Add(node);
        node.IsExpanded = true;
        int index = _rows.IndexOf(node);
        if (index < 0) return; // not currently shown (ancestor collapsed); takes effect when it appears
        var slice = new List<T>();
        AppendVisible(node, slice);
        if (slice.Count > BulkThreshold) _rows.InsertRangeReset(index + 1, slice);
        else _rows.InsertRange(index + 1, slice);
    }

    public void Collapse(T node)
    {
        if (!_expanded.Remove(node)) return;
        node.IsExpanded = false;
        int index = _rows.IndexOf(node);
        if (index < 0) return;
        int end = index + 1;
        while (end < _rows.Count && _rows[end].Depth > node.Depth) end++;
        int count = end - index - 1;
        if (count > BulkThreshold) _rows.RemoveRangeReset(index + 1, count);
        else _rows.RemoveRange(index + 1, count);
    }

    /// <summary>Expands every node whose depth is below <paramref name="depth"/> (roots are depth 0).</summary>
    public void ExpandToDepth(int depth)
    {
        _expanded.Clear();
        foreach (var node in AllNodes())
            if (node.Depth < depth && node.HasVisibleChildren) _expanded.Add(node);
        Rebuild(recompute: false);
    }

    public void ExpandAll() => ExpandToDepth(int.MaxValue);

    public void CollapseAll()
    {
        _expanded.Clear();
        Rebuild(recompute: false);
    }

    /// <summary>Expands ancestors so <paramref name="node"/> is in <see cref="Rows"/>; no-op if it is filtered out.</summary>
    public void Reveal(T node)
    {
        if (!IsVisible(node)) return;
        bool changed = false;
        for (var p = node.Parent; p is not null; p = p.Parent)
            changed |= _expanded.Add(p);
        if (changed) Rebuild(recompute: false);
    }

    private void Rebuild(bool recompute = true)
    {
        if (recompute) ComputeVisibility();
        var list = new List<T>();
        foreach (var root in _roots)
        {
            if (!IsVisible(root)) continue;
            root.Depth = 0;
            list.Add(root);
            root.IsExpanded = _expanded.Contains(root);
            if (root.IsExpanded) AppendVisible(root, list);
        }
        _rows.ReplaceAll(list);
    }

    // Visible descendants of an expanded node, in display order, setting Depth/IsExpanded as it goes.
    private void AppendVisible(T node, List<T> into)
    {
        foreach (var child in node.Children)
        {
            if (!IsVisible(child)) continue;
            child.Depth = node.Depth + 1;
            into.Add(child);
            child.IsExpanded = _expanded.Contains(child) && child.HasVisibleChildren;
            if (child.IsExpanded) AppendVisible(child, into);
        }
    }

    private void ComputeVisibility()
    {
        _visible.Clear();
        foreach (var root in _roots) Visit(root);

        bool Visit(T node)
        {
            bool visible;
            if (node.Children.Count == 0)
            {
                visible = _filter?.Invoke(node) ?? true;
                node.HasVisibleChildren = false;
            }
            else
            {
                bool any = false;
                foreach (var child in node.Children) any |= Visit(child);
                node.HasVisibleChildren = any;
                visible = any;
            }
            _visible[node] = visible;
            return visible;
        }
    }

    private IEnumerable<T> AllNodes()
    {
        var stack = new Stack<(T node, int depth)>();
        for (int i = _roots.Count - 1; i >= 0; i--) stack.Push((_roots[i], 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            node.Depth = depth;
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], depth + 1));
        }
    }

    /// <summary>ObservableCollection with bulk operations that raise one event instead of one per item.</summary>
    private sealed class RangeObservableCollection<TItem> : ObservableCollection<TItem>
    {
        public void ReplaceAll(IEnumerable<TItem> items)
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            RaiseReset();
        }

        public void InsertRange(int index, IReadOnlyList<TItem> items)
        {
            for (int i = 0; i < items.Count; i++) Insert(index + i, items[i]);
        }

        public void InsertRangeReset(int index, IReadOnlyList<TItem> items)
        {
            for (int i = 0; i < items.Count; i++) Items.Insert(index + i, items[i]);
            RaiseReset();
        }

        public void RemoveRange(int index, int count)
        {
            for (int i = 0; i < count; i++) RemoveAt(index);
        }

        public void RemoveRangeReset(int index, int count)
        {
            for (int i = 0; i < count; i++) Items.RemoveAt(index);
            RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
