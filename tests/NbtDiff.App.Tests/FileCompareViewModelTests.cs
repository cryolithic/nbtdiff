using fNbt;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class FileCompareViewModelTests
{
    private static async Task<FileCompareViewModel> Loaded(NbtTag? left, NbtTag? right, string title = "pair")
    {
        var vm = new FileCompareViewModel(new TagPairSource(title, left, right), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        return vm;
    }

    private static string Rows(FileCompareViewModel vm) => string.Join(" ", vm.Tree.Rows.Select(r => r.Path.Length == 0 ? "/" : r.Path));

    // {a:{x:1,y:2}, b:{p:1}, c:5, d:[1,2]} vs {a:{x:1,y:3}, b:{p:1}, c:5, d:[1,2], e:"new"}
    private static (NbtCompound left, NbtCompound right) Sample()
    {
        var left = new NbtCompound("")
        {
            new NbtCompound("a") { new NbtInt("x", 1), new NbtInt("y", 2) },
            new NbtCompound("b") { new NbtInt("p", 1) },
            new NbtInt("c", 5),
            new NbtList("d", NbtTagType.Int) { new NbtInt(1), new NbtInt(2) },
        };
        var right = (NbtCompound)left.Clone();
        right.SetPath("a/y", 3);
        right.Add(new NbtString("e", "new"));
        return (left, right);
    }

    [Fact]
    public async Task CollapsedUnchanged_ShowsOnlyChangedPaths()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);

        Assert.True(vm.HasResult);
        Assert.False(vm.ShowUnchanged);
        Assert.Equal("/ a a/y e", Rows(vm));
        Assert.True(vm.Root!.IsExpanded);
        Assert.Equal("2 differences · 1 changed · 1 right-only", vm.SummaryText);

        var y = vm.Tree.Rows.Single(i => i.Path == "a/y");
        Assert.Equal(DiffKind.ValueChanged, y.Kind);
        Assert.Equal("2", y.LeftValueText);
        Assert.Equal("3", y.RightValueText);
        Assert.Equal("int", y.TypeText);
        Assert.Equal("≠", y.StatusGlyph);
        var e = vm.Tree.Rows.Single(i => i.Path == "e");
        Assert.False(e.HasLeft);
        Assert.Equal("\"new\"", e.RightValueText);
    }

    [Fact]
    public async Task ShowUnchanged_RevealsIdenticalNodes_AndBack()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);

        vm.ShowUnchanged = true;
        // Expansion state carries over: the root and a were expanded along the changed path; b and d were not.
        Assert.Equal("/ a a/x a/y b c d e", Rows(vm));
        var b = vm.Tree.Rows.Single(i => i.Path == "b");
        Assert.Equal("{1 entry}", b.LeftValueText);
        Assert.Equal("compound", b.TypeText);
        Assert.Equal("list<int>", vm.Tree.Rows.Single(i => i.Path == "d").TypeText);

        vm.ShowUnchanged = false;
        Assert.Equal("/ a a/y e", Rows(vm));
    }

    [Fact]
    public async Task OneSidedSubtree_StaysCollapsedButExpandable()
    {
        var left = new NbtCompound("") { new NbtInt("k", 1) };
        var right = new NbtCompound("") { new NbtInt("k", 1), new NbtCompound("big") { new NbtInt("p", 1), new NbtList("q", NbtTagType.Byte) { new NbtByte(1) } } };
        var vm = await Loaded(left, right);

        Assert.Equal("/ big", Rows(vm));
        var big = vm.Tree.Rows.Single(i => i.Path == "big");
        Assert.Equal(DiffKind.Added, big.Kind);
        Assert.True(big.HasVisibleChildren);
        vm.Tree.Expand(big);
        Assert.Equal("/ big big/p big/q", Rows(vm));
        Assert.Equal(4, vm.ChangedNodes.Count);   // big, p, q, q/[0]
        Assert.Equal("4 differences · 4 right-only", vm.SummaryText);
    }

    [Fact]
    public async Task NextPrevious_WalkChangesInPreorder_WithWrapAround()
    {
        // Changes in pre-order: a/y (ValueChanged), e (Added), f (Removed)
        var left = new NbtCompound("")
        {
            new NbtCompound("a") { new NbtInt("x", 1), new NbtInt("y", 2) },
            new NbtInt("f", 9),
        };
        var right = new NbtCompound("")
        {
            new NbtCompound("a") { new NbtInt("x", 1), new NbtInt("y", 3) },
            new NbtInt("e", 7),
        };
        var vm = await Loaded(left, right);
        Assert.Equal(["a/y", "e", "f"], vm.ChangedNodes.Select(n => n.Path));
        Assert.Null(vm.SelectedItem);

        vm.NextChangeCommand.Execute(null);
        Assert.Equal("a/y", vm.SelectedItem!.Path);
        vm.NextChangeCommand.Execute(null);
        Assert.Equal("e", vm.SelectedItem!.Path);
        vm.NextChangeCommand.Execute(null);
        Assert.Equal("f", vm.SelectedItem!.Path);
        vm.NextChangeCommand.Execute(null);
        Assert.Equal("a/y", vm.SelectedItem!.Path);      // wraps to the first

        vm.PreviousChangeCommand.Execute(null);
        Assert.Equal("f", vm.SelectedItem!.Path);        // wraps to the last
        vm.PreviousChangeCommand.Execute(null);
        Assert.Equal("e", vm.SelectedItem!.Path);

        // From an unchanged selection, Next goes to the first change after it in pre-order.
        vm.ShowUnchanged = true;
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "a/x");
        vm.NextChangeCommand.Execute(null);
        Assert.Equal("a/y", vm.SelectedItem!.Path);
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "a/x");
        vm.PreviousChangeCommand.Execute(null);
        Assert.Equal("f", vm.SelectedItem!.Path);        // nothing before a/x → wraps to the last
    }

    [Fact]
    public async Task NextChange_RevealsCollapsedTarget()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);
        vm.CollapseAllCommand.Execute(null);
        Assert.Equal("/ a e", Rows(vm));

        vm.NextChangeCommand.Execute(null);
        Assert.Equal("a/y", vm.SelectedItem!.Path);
        Assert.Contains(vm.SelectedItem, vm.Tree.Rows);
    }

    [Fact]
    public async Task NoChanges_SummaryAndNavigationNoOp()
    {
        var c = NbtFixtures.SampleCompound(1);
        var vm = await Loaded(c, (NbtCompound)c.Clone());
        Assert.Equal("No differences", vm.SummaryText);
        Assert.Empty(vm.ChangedNodes);
        // Nothing would pass the changed-only filter, so identical trees switch to showing everything.
        Assert.True(vm.ShowUnchanged);
        Assert.True(vm.Root!.IsExpanded);
        Assert.Equal(1 + c.Count, vm.Tree.Rows.Count);
        vm.NextChangeCommand.Execute(null);
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task SelectingArray_ShowsDetailWindow()
    {
        var left = new NbtCompound("") { new NbtIntArray("arr", Enumerable.Range(0, 100).ToArray()) };
        var right = (NbtCompound)left.Clone();
        ((NbtIntArray)right["arr"]).Value[40] = -1;
        var vm = await Loaded(left, right);

        var arr = vm.Tree.Rows.Single(i => i.Path == "arr");
        Assert.Equal("int[100] · differs at [40]", arr.LeftValueText);
        Assert.Equal("int[100] · differs at [40]", arr.RightValueText);
        Assert.Null(vm.ArrayDetail);

        vm.SelectedItem = arr;
        Assert.True(vm.HasArrayDetail);
        Assert.Equal(33, vm.ArrayDetail!.Count);
        Assert.Equal(24, vm.ArrayDetail[0].Index);
        var diff = Assert.Single(vm.ArrayDetail, r => r.IsDifferent);
        Assert.Equal((40, "40", "-1"), (diff.Index, diff.Left, diff.Right));

        vm.SelectedItem = vm.Root;
        Assert.False(vm.HasArrayDetail);
    }

    [Fact]
    public async Task CompoundOrderToggle_Rediffs()
    {
        var left = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2) };
        var right = new NbtCompound("") { new NbtInt("y", 2), new NbtInt("x", 1) };
        var vm = await Loaded(left, right);
        Assert.Equal("No differences", vm.SummaryText);

        vm.CompoundOrderMatters = true;
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, vm.ChangedNodes.Count);
        Assert.All(vm.ChangedNodes, n => Assert.Equal(DiffKind.Moved, n.Kind));
        Assert.Equal("2 differences · 2 moved", vm.SummaryText);
        Assert.Equal("↕", vm.ChangedNodes[0].StatusGlyph);

        vm.CompoundOrderMatters = false;
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("No differences", vm.SummaryText);
    }

    [Fact]
    public async Task KeyedAlignerToggle_Rediffs()
    {
        NbtCompound Entity(string id, float health) => new() { new NbtString("id", id), new NbtFloat("Health", health) };
        var left = new NbtCompound("") { new NbtList("Entities", NbtTagType.Compound) { Entity("minecraft:cow", 10), Entity("minecraft:pig", 8) } };
        var right = new NbtCompound("") { new NbtList("Entities", NbtTagType.Compound) { Entity("minecraft:pig", 8), Entity("minecraft:cow", 10) } };
        var vm = await Loaded(left, right);
        Assert.NotEqual("No differences", vm.SummaryText);

        vm.UseKeyedAligner = true;
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("No differences", vm.SummaryText);
    }

    [Fact]
    public async Task LoadFailure_IsShown()
    {
        var vm = await Loaded(null, null);
        Assert.True(vm.HasError);
        Assert.False(vm.HasResult);
        Assert.Contains("Nothing to compare", vm.ErrorMessage);
        Assert.Empty(vm.Tree.Rows);
    }

    [Fact]
    public async Task FileSource_LoadsBothSides_AndTitles()
    {
        using var d = new TempDir();
        var a = NbtFixtures.SampleCompound(1);
        var b = (NbtCompound)a.Clone();
        b.SetPath("nested/depth", 2);
        NbtFixtures.WriteFile(d.File(Path.Combine("l", "level.dat")), a, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File(Path.Combine("r", "level.dat")), b, NbtFormat.JavaNbt, NbtCompression.None);

        var source = new FileDiffSource(d.File(Path.Combine("l", "level.dat")), d.File(Path.Combine("r", "level.dat")));
        Assert.Equal("level.dat", source.Title);
        var vm = new FileCompareViewModel(source, new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.HasError);
        Assert.Equal(["nested/depth"], vm.ChangedNodes.Select(n => n.Path));

        Assert.Equal("a.dat ↔ b.dat", new FileDiffSource("x/a.dat", "y/b.dat").Title);
        Assert.Equal("a.dat ↔ (missing)", new FileDiffSource("x/a.dat", null).Title);
    }

    [Fact]
    public async Task FileSource_MissingSide_IsWholeTreeRemoved()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        var vm = new FileCompareViewModel(new FileDiffSource(d.File("a.dat"), null), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DiffKind.Removed, vm.Root!.Kind);
        Assert.Equal("2 differences · 2 left-only", vm.SummaryText);
    }

    [Fact]
    public async Task FileSource_Unparseable_Fails()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.dat"), "not nbt");
        File.WriteAllText(d.File("b.dat"), "not nbt");
        var vm = new FileCompareViewModel(new FileDiffSource(d.File("a.dat"), d.File("b.dat")), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.HasError);
        Assert.Contains("Left:", vm.ErrorMessage);
    }

    [Fact]
    public void DiffNodeItem_TypeAndValueTexts()
    {
        var left = new NbtCompound("")
        {
            new NbtString("s", new string('x', 200)),
            new NbtByte("b", unchecked((byte)-1)),
            new NbtList("empty"),
            new NbtLongArray("la", [1, 2, 3]),
        };
        var right = (NbtCompound)left.Clone();
        right.SetPath("b", new NbtString("changed"));
        var root = DiffNodeItem.Build(NbtDiffer.Diff(left, right));
        var byPath = root.Descendants().ToDictionary(i => i.Path);

        Assert.Equal("(root)", root.Name);
        Assert.Equal("byte → string", byPath["b"].TypeText);
        Assert.Equal(DiffKind.TypeChanged, byPath["b"].Kind);
        Assert.Equal("-1b", byPath["b"].LeftValueText);
        Assert.Equal("list", byPath["empty"].TypeText);
        Assert.Equal("[0 items]", byPath["empty"].LeftValueText);
        Assert.Equal("long[3]", byPath["la"].LeftValueText);
        Assert.Equal(DiffNodeItem.MaxValueLength + 1, byPath["s"].LeftValueText!.Length);
        Assert.EndsWith("…", byPath["s"].LeftValueText);
        Assert.Equal(202, byPath["s"].LeftToolTip!.Length);   // quotes + 200 chars
        Assert.Equal([0, 1, 2, 3, 4], root.Descendants().Select(i => i.DfsIndex));
    }
    [Fact]
    public async Task NoNavigation_CommandsDisabled()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);
        Assert.False(vm.HasChunkNavigation);
        Assert.Null(vm.Navigation);
        Assert.Equal("", vm.ChunkPositionText);
        Assert.False(vm.NextChunkCommand.CanExecute(null));
        Assert.False(vm.PreviousChunkCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChunkNavigation_StepsInOrderWrapsAndMovesGrid()
    {
        var (l, r) = Sample();
        var sources = new Dictionary<(int, int), IDiffSource>
        {
            [(1, 0)] = new TagPairSource("c(1,0)", l, r),
            [(5, 0)] = new TagPairSource("c(5,0)", l, l),   // identical pair
            [(2, 1)] = new TagPairSource("c(2,1)", l, r),
        };
        var moved = new List<(int, int)>();
        var nav = new ChunkNavigation([(1, 0), (5, 0), (2, 1)], (x, z) => sources[(x, z)], (x, z) => moved.Add((x, z)));

        var vm = new FileCompareViewModel(new TagPairSource("start", l, r), new ImmediateUiDispatcher(), navigation: nav);
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.HasChunkNavigation);
        Assert.Equal("", vm.ChunkPositionText);   // "start" is not one of the changed chunks

        var titles = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Title)) titles.Add(vm.Title); };

        async Task Step(bool next)
        {
            (next ? vm.NextChunkCommand : vm.PreviousChunkCommand).Execute(null);
            await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await Step(next: true);
        Assert.Equal("c(1,0)", vm.Title);
        Assert.Equal("changed chunk 1 of 3", vm.ChunkPositionText);
        Assert.Single(vm.ChangedNodes, n => n.Path == "a/y");

        await Step(next: true);
        Assert.Equal("c(5,0)", vm.Title);
        Assert.Empty(vm.ChangedNodes);
        Assert.True(vm.ShowUnchanged);   // identical pair shows the whole tree

        await Step(next: true);
        Assert.Equal("c(2,1)", vm.Title);
        Assert.Equal("changed chunk 3 of 3", vm.ChunkPositionText);

        await Step(next: true);           // wraps
        Assert.Equal("c(1,0)", vm.Title);

        await Step(next: false);          // wraps the other way
        Assert.Equal("c(2,1)", vm.Title);

        Assert.Equal([(1, 0), (5, 0), (2, 1), (1, 0), (2, 1)], moved);
        Assert.Equal(["c(1,0)", "c(5,0)", "c(2,1)", "c(1,0)", "c(2,1)"], titles);
    }

    [Fact]
    public async Task ChunkNavigation_EmptyList_IsInert()
    {
        var (l, r) = Sample();
        var vm = new FileCompareViewModel(new TagPairSource("x", l, r), new ImmediateUiDispatcher(),
            navigation: new ChunkNavigation([], (_, _) => throw new InvalidOperationException()));
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.HasChunkNavigation);
        vm.NextChunkCommand.Execute(null);
        Assert.Equal("x", vm.Title);
    }
}
