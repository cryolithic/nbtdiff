using System.Collections.Specialized;
using NbtDiff.App.Tree;

namespace NbtDiff.App.Tests;

public class FlatTreeSourceTests
{
    private sealed class Node(string name, bool keep = true) : IFlatTreeNode<Node>
    {
        private readonly List<Node> _children = [];
        public string Name { get; } = name;
        public bool Keep { get; set; } = keep;
        public IReadOnlyList<Node> Children => _children;
        public Node? Parent { get; private set; }
        public int Depth { get; set; }
        public bool IsExpanded { get; set; }
        public bool HasVisibleChildren { get; set; }

        public Node Add(params Node[] children)
        {
            foreach (var c in children) { c.Parent = this; _children.Add(c); }
            return this;
        }

        public override string ToString() => Name;
    }

    // root tree:
    //   a/            (dir)
    //     a1          (keep)
    //     a2          (filtered out when Keep=false)
    //     sub/
    //       s1
    //   b             (file)
    //   c/            (dir, only filtered-out child)
    //     c1 (drop)
    private static (Node a, Node a1, Node a2, Node sub, Node s1, Node b, Node c, Node c1, List<Node> roots) Tree()
    {
        var a1 = new Node("a1"); var a2 = new Node("a2", keep: false); var s1 = new Node("s1");
        var sub = new Node("sub").Add(s1);
        var a = new Node("a").Add(a1, a2, sub);
        var b = new Node("b");
        var c1 = new Node("c1", keep: false);
        var c = new Node("c").Add(c1);
        return (a, a1, a2, sub, s1, b, c, c1, [a, b, c]);
    }

    private static string Names(FlatTreeSource<Node> src) => string.Join(" ", src.Rows.Select(r => new string(' ', r.Depth) + r.Name));

    [Fact]
    public void Roots_StartCollapsed()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        Assert.Equal("a b c", Names(src));
        Assert.True(t.a.HasVisibleChildren);
        Assert.False(t.b.HasVisibleChildren);
        Assert.False(t.a.IsExpanded);
        Assert.Equal([0, 0, 0], src.Rows.Select(r => r.Depth));
    }

    [Fact]
    public void Expand_InsertsOnlyThatSlice()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        var events = new List<NotifyCollectionChangedEventArgs>();
        src.Rows.CollectionChanged += (_, e) => events.Add(e);

        src.Expand(t.a);
        Assert.Equal("a  a1  a2  sub b c", Names(src));
        Assert.True(t.a.IsExpanded);
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Add, e.Action));
        Assert.Equal(3, events.Count);

        src.Expand(t.sub);
        Assert.Equal("a  a1  a2  sub   s1 b c", Names(src));
        Assert.Equal(2, t.s1.Depth);
    }

    [Fact]
    public void Collapse_RemovesAllDescendants_AndRemembersInnerState()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.Expand(t.a);
        src.Expand(t.sub);

        src.Collapse(t.a);
        Assert.Equal("a b c", Names(src));
        Assert.False(t.a.IsExpanded);

        // sub was expanded before; expanding a again shows it expanded.
        src.Expand(t.a);
        Assert.Equal("a  a1  a2  sub   s1 b c", Names(src));
        Assert.True(t.sub.IsExpanded);
    }

    [Fact]
    public void Toggle_And_ExpandOnLeaf_IsNoop()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.Toggle(t.a);
        Assert.True(t.a.IsExpanded);
        src.Toggle(t.a);
        Assert.False(t.a.IsExpanded);
        src.Expand(t.b);
        Assert.Equal("a b c", Names(src));
        Assert.False(src.IsExpanded(t.b));
    }

    [Fact]
    public void Filter_HidesLeaves_KeepsAncestors_DropsEmptyFolders()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.ExpandAll();
        Assert.Equal("a  a1  a2  sub   s1 b c  c1", Names(src));

        src.SetFilter(n => n.Keep);
        Assert.Equal("a  a1  sub   s1 b", Names(src));   // a2 and c1 hidden, c gone with them
        Assert.False(t.c.HasVisibleChildren);
        Assert.False(src.IsVisible(t.c));
        Assert.True(src.IsVisible(t.sub));

        src.SetFilter(null);
        Assert.Equal("a  a1  a2  sub   s1 b c  c1", Names(src));
    }

    [Fact]
    public void Filter_PreservesExpansionState()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.Expand(t.a);              // sub stays collapsed
        src.SetFilter(n => n.Keep);
        Assert.Equal("a  a1  sub b", Names(src));
        src.SetFilter(null);
        Assert.Equal("a  a1  a2  sub b c", Names(src));
    }

    [Fact]
    public void Refilter_ReportsFlips_AndRebuildsOnlyThen()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.SetFilter(n => n.Keep);
        src.ExpandAll();
        Assert.Equal("a  a1  sub   s1 b", Names(src));

        Assert.False(src.Refilter());              // nothing changed
        t.a2.Keep = true;
        Assert.True(src.Refilter());
        Assert.Equal("a  a1  a2  sub   s1 b", Names(src));
        t.c1.Keep = true;
        Assert.True(src.Refilter());
        Assert.Equal("a  a1  a2  sub   s1 b c", Names(src));   // c reappears collapsed (never expanded)
    }

    [Fact]
    public void ExpandToDepth_And_CollapseAll()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.ExpandToDepth(1);
        Assert.Equal("a  a1  a2  sub b c  c1", Names(src));
        Assert.False(t.sub.IsExpanded);
        src.CollapseAll();
        Assert.Equal("a b c", Names(src));
    }

    [Fact]
    public void Reveal_ExpandsAncestors()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.Reveal(t.s1);
        Assert.Contains(t.s1, src.Rows);
        Assert.True(t.a.IsExpanded);
        Assert.True(t.sub.IsExpanded);
        Assert.False(t.c.IsExpanded);
    }

    [Fact]
    public void ExpandWhileHidden_TakesEffectWhenShown()
    {
        var t = Tree();
        var src = new FlatTreeSource<Node>();
        src.SetRoots(t.roots);
        src.Expand(t.sub);            // a is collapsed, sub not in Rows
        Assert.Equal("a b c", Names(src));
        src.Expand(t.a);
        Assert.Equal("a  a1  a2  sub   s1 b c", Names(src));
    }

    [Fact]
    public void LargeSlice_UsesReset()
    {
        var root = new Node("root");
        root.Add(Enumerable.Range(0, 1000).Select(i => new Node($"n{i}")).ToArray());
        var src = new FlatTreeSource<Node> { BulkThreshold = 100 };
        src.SetRoots([root]);
        var actions = new List<NotifyCollectionChangedAction>();
        src.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        src.Expand(root);
        Assert.Equal(1001, src.Rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);

        actions.Clear();
        src.Collapse(root);
        Assert.Single(src.Rows);
        Assert.Equal([NotifyCollectionChangedAction.Reset], actions);
    }
}
