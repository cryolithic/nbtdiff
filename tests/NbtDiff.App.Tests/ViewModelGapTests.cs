using fNbt;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Core.Diff;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

/// <summary>Covers the display/formatting and edge paths of the view models that the main test classes leave untested.</summary>
public class ViewModelGapTests
{
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
        ((NbtInt)((NbtCompound)right["a"])["y"]).Value = 3;
        right.Add(new NbtString("e", "new"));
        return (left, right);
    }

    private static async Task<FileCompareViewModel> Loaded(NbtTag? left, NbtTag? right, string title = "pair")
    {
        var vm = new FileCompareViewModel(new TagPairSource(title, left, right), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        return vm;
    }

    private static string Rows(FileCompareViewModel vm) => string.Join(" ", vm.Tree.Rows.Select(r => r.Path.Length == 0 ? "/" : r.Path));

    private static async Task<(FolderCompareViewModel vm, ImmediateUiDispatcher ui, FakeDialogService dialogs)> Scanned(string left, string right)
    {
        var ui = new ImmediateUiDispatcher();
        var dialogs = new FakeDialogService();
        var vm = new FolderCompareViewModel(dialogs, ui) { LeftPath = left, RightPath = right };
        vm.CompareCommand.Execute(null);
        Assert.True(vm.IsScanning);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsScanning);
        return (vm, ui, dialogs);
    }

    private static string Names(FolderCompareViewModel vm) => string.Join(" ", vm.Tree.Rows.Select(r => r.RelativePath));

    // ── CompareRowItem ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompareRowItem_Glyph_DescribeCounts_FormatSize()
    {
        Assert.Equal("…", CompareRowItem.Glyph(RowStatus.Pending));
        Assert.Equal("=", CompareRowItem.Glyph(RowStatus.Same));
        Assert.Equal("≠?", CompareRowItem.Glyph(RowStatus.ProbablyDifferent));
        Assert.Equal("≠", CompareRowItem.Glyph(RowStatus.Different));
        Assert.Equal("◀", CompareRowItem.Glyph(RowStatus.LeftOnly));
        Assert.Equal("▶", CompareRowItem.Glyph(RowStatus.RightOnly));
        Assert.Equal("!", CompareRowItem.Glyph(RowStatus.Error));
        Assert.Equal("?", CompareRowItem.Glyph((RowStatus)99));

        Assert.Equal("empty", CompareRowItem.DescribeCounts(default));
        Assert.Equal("2 differ", CompareRowItem.DescribeCounts(new RowCounts(0, 0, 0, 2, 0, 0, 0)));
        Assert.Equal("1 probably differ", CompareRowItem.DescribeCounts(new RowCounts(0, 0, 1, 0, 0, 0, 0)));
        Assert.Equal("3 same", CompareRowItem.DescribeCounts(new RowCounts(0, 3, 0, 0, 0, 0, 0)));
        Assert.Equal("1 left-only", CompareRowItem.DescribeCounts(new RowCounts(0, 0, 0, 0, 1, 0, 0)));
        Assert.Equal("1 right-only", CompareRowItem.DescribeCounts(new RowCounts(0, 0, 0, 0, 0, 1, 0)));
        Assert.Equal("1 error", CompareRowItem.DescribeCounts(new RowCounts(0, 0, 0, 0, 0, 0, 1)));
        Assert.Equal("4 pending", CompareRowItem.DescribeCounts(new RowCounts(4, 0, 0, 0, 0, 0, 0)));
        Assert.Equal("2 differ · 1 probably differ · 3 same · 1 left-only · 1 right-only · 1 error · 4 pending",
            CompareRowItem.DescribeCounts(new RowCounts(4, 3, 1, 2, 1, 1, 1)));

        Assert.Equal("0 B", CompareRowItem.FormatSize(0));
        Assert.Equal("512 B", CompareRowItem.FormatSize(512));
        Assert.Equal("1023 B", CompareRowItem.FormatSize(1023));
        Assert.Equal("1.0 KB", CompareRowItem.FormatSize(1024));
        Assert.Equal("1.5 KB", CompareRowItem.FormatSize(1536));
        Assert.Equal("10 KB", CompareRowItem.FormatSize(10240));
        Assert.Equal("977 KB", CompareRowItem.FormatSize(1_000_000));
        Assert.Equal("1.0 MB", CompareRowItem.FormatSize(1024 * 1024));
        Assert.Equal("1.0 GB", CompareRowItem.FormatSize(1024L * 1024 * 1024));
        Assert.Equal("1.0 TB", CompareRowItem.FormatSize(1024L * 1024 * 1024 * 1024));
    }

    [Fact]
    public async Task CompareRowItem_Build_Refresh_Matches()
    {
        using var dl = new TempDir();
        using var dr = new TempDir();
        File.WriteAllText(dl.File("same.txt"), "hello");
        File.WriteAllText(dr.File("same.txt"), "hello");
        File.WriteAllText(dl.File("diff.txt"), "left");
        File.WriteAllText(dr.File("diff.txt"), "right");
        File.WriteAllText(dl.File("only-left.txt"), "x");
        File.WriteAllText(dr.File("only-right.txt"), "y");
        File.WriteAllText(dl.File("mismatch"), "file");
        Directory.CreateDirectory(dr.File("mismatch"));

        var root = new DirectoryComparer().Prepare(dl.Path, dr.Path);
        var (rootItem, index) = CompareRowItem.Build(root.Root);

        // Tree shape: parent/child links and the row→item index agree.
        Assert.Null(rootItem.Parent);
        Assert.Equal(5, rootItem.Children.Count);
        foreach (var child in root.Root.Children)
        {
            Assert.Same(rootItem, index[child].Parent);
            Assert.Contains(index[child], rootItem.Children);
        }
        Assert.Same(rootItem, index[root.Root]);

        // Display properties of a file row.
        var diffRow = root.Root.Children.Single(r => r.Name == "diff.txt");
        var diffItem = index[diffRow];
        Assert.Equal("diff.txt", diffItem.Name);
        Assert.Equal("diff.txt", diffItem.RelativePath);
        Assert.Equal(FileKind.Text, diffItem.Kind);
        Assert.False(diffItem.IsDirectory);
        Assert.True(diffItem.HasLeft);
        Assert.True(diffItem.HasRight);
        Assert.Equal("4 B", diffItem.LeftSizeText);
        Assert.Equal("5 B", diffItem.RightSizeText);
        Assert.NotNull(diffItem.LeftModifiedText);
        Assert.NotNull(diffItem.RightModifiedText);
        Assert.Null(diffItem.CountsText);       // only directories carry a counts text
        Assert.Null(diffItem.ToolTipText);     // no error, not a directory
        Assert.Equal("…", diffItem.StatusGlyph);   // paired files are Pending after Prepare
        Assert.Equal("", diffItem.ExpanderGlyph);  // no children yet

        // Directory rows: no size text, counts text doubles as the tooltip.
        Assert.True(rootItem.IsDirectory);
        Assert.Null(rootItem.LeftSizeText);
        Assert.Null(rootItem.LeftModifiedText);
        // A dir-vs-file mismatch is a Directory-kind row, and directories are not counted in their
        // parent's totals (RowCounts: "Directories are not counted"), so the error does not appear here.
        Assert.Equal("1 left-only · 1 right-only · 2 pending", rootItem.CountsText);
        Assert.Equal(rootItem.CountsText, rootItem.ToolTipText);
        Assert.Equal("dir <root>: Different", rootItem.ToString());   // one-sided children make the root Different

        // The dir-vs-file mismatch is an Error row with the exact message.
        var mismatchItem = index[root.Root.Children.Single(r => r.Name == "mismatch")];
        Assert.Equal(RowStatus.Error, mismatchItem.Status);
        Assert.Equal("file on the left, directory on the right", mismatchItem.Error);
        Assert.Equal("!", mismatchItem.StatusGlyph);
        Assert.Same(mismatchItem.Error, mismatchItem.ToolTipText);
        mismatchItem.HasVisibleChildren = true;
        Assert.Equal("▸", mismatchItem.ExpanderGlyph);
        mismatchItem.IsExpanded = true;
        Assert.Equal("▾", mismatchItem.ExpanderGlyph);

        // One-sided rows are final after Prepare.
        var leftOnly = index[root.Root.Children.Single(r => r.Name == "only-left.txt")];
        Assert.Equal(RowStatus.LeftOnly, leftOnly.Status);
        Assert.True(leftOnly.HasLeft);
        Assert.False(leftOnly.HasRight);
        Assert.Null(leftOnly.RightSizeText);
        Assert.Equal("◀", leftOnly.StatusGlyph);

        // Refresh re-reads the live row after the scan finalizes the statuses.
        root.Run();
        await root.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        diffItem.Refresh();
        Assert.Equal(RowStatus.Different, diffItem.Status);
        Assert.Equal("≠", diffItem.StatusGlyph);
        var sameItem = index[root.Root.Children.Single(r => r.Name == "same.txt")];
        sameItem.Refresh();
        Assert.Equal(RowStatus.Same, sameItem.Status);
        rootItem.Refresh();
        Assert.Equal("1 differ · 1 same · 1 left-only · 1 right-only", rootItem.CountsText);   // the dir-vs-file error is a directory-kind row, not counted

        // The full filter matrix, plus the fall-through for unknown values.
        foreach (RowStatus s in Enum.GetValues<RowStatus>())
        {
            Assert.True(CompareRowItem.Matches(RowFilter.All, s));
            Assert.Equal(s == RowStatus.Same, CompareRowItem.Matches(RowFilter.Same, s));
            Assert.Equal(s == RowStatus.LeftOnly, CompareRowItem.Matches(RowFilter.LeftOnly, s));
            Assert.Equal(s == RowStatus.RightOnly, CompareRowItem.Matches(RowFilter.RightOnly, s));
            Assert.Equal(s == RowStatus.Error, CompareRowItem.Matches(RowFilter.Errors, s));
            Assert.Equal(s is RowStatus.Different or RowStatus.ProbablyDifferent or RowStatus.LeftOnly or RowStatus.RightOnly,
                CompareRowItem.Matches(RowFilter.Differences, s));
        }
        Assert.True(CompareRowItem.Matches((RowFilter)99, RowStatus.Same));
        Assert.False(CompareRowItem.Matches(RowFilter.Differences, (RowStatus)99));
    }

    // ── DiffNodeItem ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DiffNodeItem_Glyph_Describe_TypeName()
    {
        Assert.Equal("=", DiffNodeItem.Glyph(DiffKind.Unchanged));
        Assert.Equal("▶", DiffNodeItem.Glyph(DiffKind.Added));
        Assert.Equal("◀", DiffNodeItem.Glyph(DiffKind.Removed));
        Assert.Equal("≠", DiffNodeItem.Glyph(DiffKind.ValueChanged));
        Assert.Equal("≠", DiffNodeItem.Glyph(DiffKind.TypeChanged));
        Assert.Equal("↕", DiffNodeItem.Glyph(DiffKind.Moved));
        Assert.Equal("✎", DiffNodeItem.Glyph(DiffKind.Renamed));
        Assert.Equal("?", DiffNodeItem.Glyph((DiffKind)99));

        Assert.Equal("unchanged", DiffNodeItem.Describe(DiffKind.Unchanged));
        Assert.Equal("only on the right", DiffNodeItem.Describe(DiffKind.Added));
        Assert.Equal("only on the left", DiffNodeItem.Describe(DiffKind.Removed));
        Assert.Equal("value changed", DiffNodeItem.Describe(DiffKind.ValueChanged));
        Assert.Equal("type changed", DiffNodeItem.Describe(DiffKind.TypeChanged));
        Assert.Equal("moved (key order differs)", DiffNodeItem.Describe(DiffKind.Moved));
        Assert.Equal("renamed", DiffNodeItem.Describe(DiffKind.Renamed));
        Assert.Equal("99", DiffNodeItem.Describe((DiffKind)99));

        Assert.Equal("byte", DiffNodeItem.TypeName(NbtTagType.Byte));
        Assert.Equal("short", DiffNodeItem.TypeName(NbtTagType.Short));
        Assert.Equal("int", DiffNodeItem.TypeName(NbtTagType.Int));
        Assert.Equal("long", DiffNodeItem.TypeName(NbtTagType.Long));
        Assert.Equal("float", DiffNodeItem.TypeName(NbtTagType.Float));
        Assert.Equal("double", DiffNodeItem.TypeName(NbtTagType.Double));
        Assert.Equal("string", DiffNodeItem.TypeName(NbtTagType.String));
        Assert.Equal("byte[]", DiffNodeItem.TypeName(NbtTagType.ByteArray));
        Assert.Equal("int[]", DiffNodeItem.TypeName(NbtTagType.IntArray));
        Assert.Equal("long[]", DiffNodeItem.TypeName(NbtTagType.LongArray));
        Assert.Equal("list", DiffNodeItem.TypeName(NbtTagType.List));
        Assert.Equal("compound", DiffNodeItem.TypeName(NbtTagType.Compound));
        Assert.Equal("end", DiffNodeItem.TypeName(NbtTagType.End));
        Assert.Equal("unknown", DiffNodeItem.TypeName(NbtTagType.Unknown));

        Assert.Equal("", DiffNodeItem.TypeName(null!));
        Assert.Equal("compound", DiffNodeItem.TypeName(new NbtCompound("c")));
        Assert.Equal("list", DiffNodeItem.TypeName(new NbtList("l")));
        Assert.Equal("list", DiffNodeItem.TypeName(new NbtList("l") { ListType = NbtTagType.End }));
        Assert.Equal("list", DiffNodeItem.TypeName(new NbtList("l") { ListType = NbtTagType.Unknown }));
        Assert.Equal("list<int>", DiffNodeItem.TypeName(new NbtList("l", NbtTagType.Int) { new NbtInt(1) }));
        Assert.Equal("int[]", DiffNodeItem.TypeName(new NbtIntArray("a")));
    }

    [Fact]
    public void DiffNodeItem_IdentityOf_AllBranches()
    {
        Assert.Null(DiffNodeItem.IdentityOf(null));
        Assert.Null(DiffNodeItem.IdentityOf(new NbtCompound { new NbtInt("v", 1) }));                        // no head, no position
        Assert.Equal("(1, 64, -3)", DiffNodeItem.IdentityOf(new NbtCompound { new NbtInt("x", 1), new NbtInt("y", 64), new NbtInt("z", -3) }));
        Assert.Equal("\"minecraft:cow\"", DiffNodeItem.IdentityOf(new NbtCompound { new NbtString("id", "minecraft:cow") }));
        Assert.Equal("\"minecraft:chest\"", DiffNodeItem.IdentityOf(new NbtCompound { new NbtString("Name", "minecraft:chest") }));
        Assert.Equal("[I;1,2,3,4]", DiffNodeItem.IdentityOf(new NbtCompound { new NbtIntArray("UUID", [1, 2, 3, 4]) }));
        Assert.Equal("Slot 4", DiffNodeItem.IdentityOf(new NbtCompound { new NbtInt("Slot", 4) }));
        Assert.Equal("\"minecraft:cow\" @ (1, 64, -3)",
            DiffNodeItem.IdentityOf(new NbtCompound { new NbtString("id", "minecraft:cow"), new NbtInt("x", 1), new NbtInt("y", 64), new NbtInt("z", -3) }));
    }

    [Fact]
    public void DiffNodeItem_Tree_PinsEveryCell()
    {
        var left = new NbtCompound("")
        {
            new NbtInt("k", 1),
            new NbtCompound("a") { new NbtInt("x", 1) },
            new NbtList("Entities", NbtTagType.Compound)
            {
                new NbtCompound { new NbtString("id", "minecraft:cow"), new NbtInt("x", 1), new NbtInt("y", 64), new NbtInt("z", -3) },
            },
            new NbtIntArray("arr", [1, 2, 3]),
            new NbtString("only", "left"),
        };
        var right = (NbtCompound)left.Clone();
        ((NbtInt)right["k"]).Value = 7;
        ((NbtInt)((NbtCompound)right["a"])["x"]).Value = 2;
        ((NbtIntArray)right["arr"]).Value[1] = 9;
        right.Remove("only");
        right.Add(new NbtInt("extra", 4));

        var root = DiffNodeItem.Build(NbtDiffer.Diff(left, right));
        var byPath = root.Descendants().ToDictionary(i => i.Path);

        // Root: labels, container value text, change counts, glyphs.
        Assert.Equal("(root)", root.Name);
        Assert.Equal("compound", root.TypeText);
        Assert.Equal("{5 entries}", root.LeftValueText);
        Assert.Equal("{5 entries}", root.RightValueText);
        Assert.True(root.HasLeft);
        Assert.True(root.HasRight);
        Assert.False(root.IsArray);
        Assert.False(root.IsChanged);
        Assert.True(root.HasChanges);
        Assert.Equal(5, root.ChangedDescendants);
        Assert.Equal("●", root.StatusGlyph);                       // #7: a roll-up, not "="
        Assert.Equal("(root): 5 changes below", root.ToolTipText);
        Assert.Equal("", root.ExpanderGlyph);
        root.HasVisibleChildren = true;
        Assert.Equal("▸", root.ExpanderGlyph);
        root.IsExpanded = true;
        Assert.Equal("▾", root.ExpanderGlyph);

        // Sorted-key pre-order (CompoundOrderMatters off): root's children are Entities, a, arr, extra, k, only;
        // the unchanged cow compound still descends into its 4 keys (id, x, y, z).
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], root.Descendants().Select(i => i.DfsIndex));
        Assert.Equal([root, byPath["Entities"], byPath["Entities/[0]"], byPath["Entities/[0]/id"], byPath["Entities/[0]/x"], byPath["Entities/[0]/y"], byPath["Entities/[0]/z"], byPath["a"], byPath["a/x"], byPath["arr"], byPath["extra"], byPath["k"], byPath["only"]],
            root.Descendants().ToList());
        Assert.Same(root, byPath["a"].Parent);
        Assert.Same(root, byPath["Entities"].Parent);
        Assert.Same(byPath["Entities"], byPath["Entities/[0]"].Parent);

        // Scalar change.
        Assert.Equal(DiffKind.ValueChanged, byPath["k"].Kind);
        Assert.Equal("1", byPath["k"].LeftValueText);
        Assert.Equal("7", byPath["k"].RightValueText);
        Assert.Equal("int", byPath["k"].TypeText);
        Assert.Equal("≠", byPath["k"].StatusGlyph);
        Assert.Equal("k: value changed", byPath["k"].ToolTipText);

        // Unchanged container with changes below.
        Assert.Equal(DiffKind.Unchanged, byPath["a"].Kind);
        Assert.True(byPath["a"].HasChanges);
        Assert.Equal(1, byPath["a"].ChangedDescendants);
        Assert.Equal("●", byPath["a"].StatusGlyph);
        Assert.Equal("a: 1 change below", byPath["a"].ToolTipText);

        // List item identity: id plus block position.
        Assert.Equal("[0]: \"minecraft:cow\" @ (1, 64, -3)", byPath["Entities/[0]"].Name);
        Assert.Equal("\"minecraft:cow\" @ (1, 64, -3)", byPath["Entities/[0]"].ItemLabel);
        Assert.Null(byPath["Entities"].ItemLabel);

        // Array with a first-difference marker on both sides.
        Assert.Equal(DiffKind.ValueChanged, byPath["arr"].Kind);
        Assert.Equal("int[3] · differs at [1]", byPath["arr"].LeftValueText);
        Assert.Equal("int[3] · differs at [1]", byPath["arr"].RightValueText);
        Assert.True(byPath["arr"].IsArray);
        Assert.Equal("int[]", byPath["arr"].TypeText);

        // One-sided rows: the missing side has no value, tooltip or type text of its own.
        Assert.Equal(DiffKind.Removed, byPath["only"].Kind);
        Assert.True(byPath["only"].HasLeft);
        Assert.False(byPath["only"].HasRight);
        Assert.Equal("\"left\"", byPath["only"].LeftValueText);
        Assert.Null(byPath["only"].RightValueText);
        Assert.Null(byPath["only"].RightToolTip);
        Assert.Equal("string", byPath["only"].TypeText);
        Assert.Equal("◀", byPath["only"].StatusGlyph);
        Assert.Equal("only: only on the left", byPath["only"].ToolTipText);

        Assert.Equal(DiffKind.Added, byPath["extra"].Kind);
        Assert.False(byPath["extra"].HasLeft);
        Assert.Null(byPath["extra"].LeftValueText);
        Assert.Null(byPath["extra"].LeftToolTip);
        Assert.Equal("4", byPath["extra"].RightValueText);
        Assert.Equal("int", byPath["extra"].TypeText);
        Assert.Equal("▶", byPath["extra"].StatusGlyph);
        Assert.Equal("extra: only on the right", byPath["extra"].ToolTipText);

        Assert.Equal("<root>: Unchanged (Compound → Compound)", root.ToString());
        Assert.Equal("k: ValueChanged (Int → Int)", byPath["k"].ToString());
        Assert.Equal("extra: Added (- → Int)", byPath["extra"].ToString());
    }

    // ── FileCompareViewModel ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileCompareViewModel_Saveable_And_Copy_Gates()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);
        Assert.False(vm.IsSaveable);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.CopyToLeftCommand.CanExecute(null));      // nothing selected
        vm.SelectedItem = vm.Root;
        Assert.True(vm.CopyToLeftCommand.CanExecute(null));
        await vm.CopyToRightCommand.ExecuteAsync(null);
        Assert.True(vm.HasUnsavedEdits);
        Assert.False(vm.SaveCommand.CanExecute(null));           // in-memory pair has nowhere to save

        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), l, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), r, NbtFormat.JavaNbt);
        var vm2 = new FileCompareViewModel(new FileDiffSource(d.File("a.dat"), d.File("b.dat")), new ImmediateUiDispatcher());
        await vm2.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm2.IsSaveable);
        Assert.False(vm2.SaveCommand.CanExecute(null));         // no edits yet
        vm2.SelectedItem = vm2.Tree.Rows.Single(i => i.Path == "a/y");
        await vm2.CopyToRightCommand.ExecuteAsync(null);
        Assert.True(vm2.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task FileCompareViewModel_Save_CopyToAbsentSide_Fails()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("r.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        var vm = new FileCompareViewModel(new FileDiffSource(null, d.File("r.dat")), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DiffKind.Added, vm.Root!.Kind);

        // Copying the right-only root onto the absent left populates the left with a clone (the
        // right is the source), so the left is flagged modified but there is no file to write to.
        vm.SelectedItem = vm.Root;
        await vm.CopyToLeftCommand.ExecuteAsync(null);
        Assert.True(vm.LeftModified);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("left: The left side has no loaded file to save to", vm.ErrorMessage);
        Assert.True(vm.LeftModified);                 // flags stay set so Save can be retried
        Assert.True(vm.HasUnsavedEdits);
        Assert.Equal("", vm.SaveStatusText);
    }

    [Fact]
    public async Task FileCompareViewModel_ChunkStepping_FromUnlistedChunk()
    {
        using var d = new TempDir();
        RegionWriter.Write(d.File("r.0.0.mca"), [new ChunkSpec(0, 0, new NbtCompound("") { new NbtInt("x", 1) })]);
        using var region = RegionFile.Open(d.File("r.0.0.mca")).ValueOrThrow();
        var (l, r) = Sample();
        var nav = new ChunkNavigation([(1, 0), (2, 1)], (x, z) => new TagPairSource($"c({x},{z})", l, r));

        // (0, 0) is not one of the listed chunks: Next goes to the first changed chunk after it
        // in (z, x) order, Previous to the last before it.
        var next = new FileCompareViewModel(new ChunkDiffSource(region, null, 0, 0, "r.0.0.mca"), new ImmediateUiDispatcher(), navigation: nav);
        await next.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("", next.ChunkPositionText);
        next.NextChunkCommand.Execute(null);
        await next.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("c(1,0)", next.Title);
        Assert.Equal("changed chunk 1 of 2", next.ChunkPositionText);

        var prev = new FileCompareViewModel(new ChunkDiffSource(region, null, 0, 0, "r.0.0.mca"), new ImmediateUiDispatcher(), navigation: nav);
        await prev.Load().WaitAsync(TimeSpan.FromSeconds(30));
        prev.PreviousChunkCommand.Execute(null);
        await prev.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("c(2,1)", prev.Title);
        Assert.Equal("changed chunk 2 of 2", prev.ChunkPositionText);

        // A chunk that is in the list is located by IndexOf.
        var listed = new FileCompareViewModel(new ChunkDiffSource(region, null, 1, 0, "r.0.0.mca"), new ImmediateUiDispatcher(), navigation: nav);
        await listed.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("changed chunk 1 of 2", listed.ChunkPositionText);
    }

    [Fact]
    public async Task FileCompareViewModel_Toggle_Expand_Collapse_Selected()
    {
        var (l, r) = Sample();
        var vm = await Loaded(l, r);
        Assert.Equal("/ a a/y e", Rows(vm));

        // null arguments are no-ops
        vm.ToggleCommand.Execute(null);
        vm.ToggleSelectedCommand.Execute(null);
        vm.ExpandSelectedCommand.Execute(null);
        vm.CollapseSelectedCommand.Execute(null);
        Assert.Equal("/ a a/y e", Rows(vm));
        Assert.Null(vm.SelectedItem);

        var a = vm.Tree.Rows.Single(i => i.Path == "a");
        vm.ToggleCommand.Execute(a);                          // collapse
        Assert.Equal("/ a e", Rows(vm));

        vm.SelectedItem = a;
        vm.ExpandSelectedCommand.Execute(null);               // expand again
        Assert.Equal("/ a a/y e", Rows(vm));

        vm.ToggleSelectedCommand.Execute(null);               // collapse
        Assert.Equal("/ a e", Rows(vm));

        // Collapsing a collapsed row whose parent is visible moves the selection to the parent.
        vm.CollapseSelectedCommand.Execute(null);
        Assert.Same(vm.Root, vm.SelectedItem);

        vm.ExpandAllCommand.Execute(null);
        Assert.Equal("/ a a/y e", Rows(vm));   // the IsChanged filter still hides unchanged nodes

        vm.CollapseAllCommand.Execute(null);
        Assert.Equal("/ a e", Rows(vm));
    }

    private sealed class ThrowingSource : IDiffSource
    {
        public string Title => "boom";
        public LoadResult<TagPair> Load() => throw new InvalidOperationException("boom happened");
    }

    [Fact]
    public async Task FileCompareViewModel_Run_ExceptionIsAFailure()
    {
        var vm = new FileCompareViewModel(new ThrowingSource(), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        Assert.True(vm.HasError);
        Assert.Equal("Compare: boom happened", vm.ErrorMessage);
        Assert.False(vm.HasResult);
        Assert.Empty(vm.Tree.Rows);
    }

    // ── FolderCompareViewModel ───────────────────────────────────────────────────────────────

    [Fact]
    public void FolderCompareViewModel_Compare_RoutesPairs()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("l.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("r.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);

        // Two files: the pair is routed to the shell and remembered, no scan starts.
        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher())
        {
            LeftPath = d.File("l.dat"),
            RightPath = d.File("r.dat"),
        };
        Assert.False(vm.HasResult);
        string? requested = null;
        vm.FilePairRequested += (l, r) => requested = $"{l}|{r}";
        vm.CompareCommand.Execute(null);
        Assert.False(vm.IsScanning);
        Assert.Equal($"{d.File("l.dat")}|{d.File("r.dat")}", requested);
        Assert.False(vm.HasError);
        Assert.Single(vm.RecentPairs);
        Assert.Equal(d.File("l.dat"), vm.RecentPairs[0].Left);

        // Folder + file: a refusal, not a scan.
        var mixed = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher())
        {
            LeftPath = d.Path,
            RightPath = d.File("l.dat"),
        };
        mixed.CompareCommand.Execute(null);
        Assert.False(mixed.IsScanning);
        Assert.Equal("One path is a folder and the other is a file — compare two folders, or two files.", mixed.ErrorMessage);

        // Right path missing: the exact message names the right side.
        var missing = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher())
        {
            LeftPath = d.Path,
            RightPath = d.File("nope.dat"),
        };
        missing.CompareCommand.Execute(null);
        Assert.False(missing.IsScanning);
        Assert.Equal($"Right path does not exist: {d.File("nope.dat")}", missing.ErrorMessage);
    }

    [Fact]
    public async Task FolderCompareViewModel_TwoFolders_StartAScan()
    {
        using var dl = new TempDir();
        using var dr = new TempDir();
        File.WriteAllText(dl.File("a.dat"), "x");
        File.WriteAllText(dr.File("a.dat"), "x");

        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher())
        {
            LeftPath = dl.Path,
            RightPath = dr.Path,
        };
        vm.CompareCommand.Execute(null);
        Assert.True(vm.IsScanning);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsScanning);
        Assert.True(vm.HasResult);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void FolderCompareViewModel_RecentPairs_PersistedThroughSettings()
    {
        using var d = new TempDir();
        var path = d.File("settings.json");
        var store = new SettingsStore(path);
        Assert.True(store.TrySave(new AppSettings { RecentPairs = [new RecentPair("/old/l", "/old/r")] }));

        var settings = new SettingsService(store);
        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher(), null, settings);
        Assert.True(vm.HasRecent);
        Assert.Single(vm.RecentPairs);
        Assert.Equal("/old/l  ↔  /old/r", vm.RecentPairs[0].Display);

        // A compared pair goes to the front of the list and is persisted.
        NbtFixtures.WriteFile(d.File("l.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("r.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        vm.LeftPath = d.File("l.dat");
        vm.RightPath = d.File("r.dat");
        vm.CompareCommand.Execute(null);
        Assert.Equal(2, vm.RecentPairs.Count);
        Assert.Equal(d.File("l.dat"), vm.RecentPairs[0].Left);
        Assert.Equal("/old/l", vm.RecentPairs[1].Left);
        var reloaded = new SettingsService(new SettingsStore(path)).Current;
        Assert.Equal(2, reloaded.RecentPairs.Count);
        Assert.Equal(d.File("l.dat"), reloaded.RecentPairs[0].Left);
        Assert.Equal("/old/r", reloaded.RecentPairs[1].Right);

        // Toggles persist through the same service.
        vm.UseKeyedAligner = false;
        Assert.False(settings.Current.UseKeyedAligner);
        Assert.Contains("\"UseKeyedAligner\": false", File.ReadAllText(path));

        vm.IgnoredTagsText = "LastUpdate, InhabitedTime";
        Assert.Equal(["LastUpdate", "InhabitedTime"], settings.Current.IgnoredTags);
        Assert.Contains("\"InhabitedTime\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task FolderCompareViewModel_Toggle_Expand_Collapse_Selected()
    {
        var world = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 3).WithLevelDat();
        using var l = new TempDir();
        using var r = new TempDir();
        world.Write(l.Path);
        world.Write(r.Path);
        var (vm, _, _) = await Scanned(l.Path, r.Path);
        Assert.Equal("region region/r.0.0.mca level.dat", Names(vm));

        // null arguments are no-ops
        vm.ToggleCommand.Execute(null);
        vm.ExpandSelectedCommand.Execute(null);
        vm.CollapseSelectedCommand.Execute(null);
        Assert.Equal("region region/r.0.0.mca level.dat", Names(vm));

        var region = vm.Tree.Rows.Single(i => i.RelativePath == "region");
        vm.ToggleCommand.Execute(region);                     // collapse
        Assert.Equal("region level.dat", Names(vm));

        vm.SelectedRow = region;
        vm.ExpandSelectedCommand.Execute(null);              // expand
        Assert.Equal("region region/r.0.0.mca level.dat", Names(vm));

        vm.CollapseSelectedCommand.Execute(null);            // expanded dir collapses
        Assert.Equal("region level.dat", Names(vm));

        // A file row is not a directory: ExpandSelected is a no-op.
        vm.SelectedRow = vm.Tree.Rows.Single(i => i.RelativePath == "level.dat");
        vm.ExpandSelectedCommand.Execute(null);
        Assert.Equal("region level.dat", Names(vm));

        // Collapsing a collapsed child whose parent is visible moves the selection to the parent.
        vm.SelectedRow = region;
        vm.ExpandSelectedCommand.Execute(null);
        vm.SelectedRow = vm.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca");
        vm.CollapseSelectedCommand.Execute(null);
        Assert.Same(region, vm.SelectedRow);

        vm.ExpandAllCommand.Execute(null);
        Assert.Equal("region region/r.0.0.mca level.dat", Names(vm));

        vm.CollapseAllCommand.Execute(null);
        Assert.Equal("region level.dat", Names(vm));

        vm.SetFilterCommand.Execute(RowFilter.Same);
        Assert.Equal(RowFilter.Same, vm.Filter);
    }

    [Fact]
    public async Task FolderCompareViewModel_Export_CancelledAndFailed()
    {
        var world = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 3).WithLevelDat();
        using var l = new TempDir();
        using var r = new TempDir();
        world.Write(l.Path);
        world.Write(r.Path);
        var (vm, _, dialogs) = await Scanned(l.Path, r.Path);
        var before = vm.StatusText;

        // Cancelling the picker changes nothing.
        dialogs.NextSaveFile = null;
        await vm.ExportCommand.ExecuteAsync("text");
        Assert.Equal(before, vm.StatusText);
        Assert.False(vm.HasError);

        // A path whose parent directory does not exist fails with the wrapped message.
        dialogs.NextSaveFile = Path.Combine(l.Path, "no", "such", "dir", "r.json");
        await vm.ExportCommand.ExecuteAsync("json");
        Assert.StartsWith("Export failed: ", vm.ErrorMessage);
    }

    // ── RegionCompareViewModel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegionCompareViewModel_Load_NeitherSide_Exists()
    {
        var vm = new RegionCompareViewModel(null, null, null, null, new ImmediateUiDispatcher());
        Assert.Equal("(missing)", vm.LeftPathText);
        Assert.Equal("(missing)", vm.RightPathText);
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        Assert.Equal("Neither region file exists", vm.ErrorMessage);
        Assert.Equal(0, vm.Grid.Present);
        Assert.Equal("", vm.HeaderText);

        // A path that does not exist is normalized to (missing) by the constructor.
        var vm2 = new RegionCompareViewModel("/nope/l.mca", "/nope/r.mca", null, null, new ImmediateUiDispatcher());
        Assert.Equal("(missing)", vm2.LeftPathText);
        await vm2.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("Neither region file exists", vm2.ErrorMessage);
    }

    [Fact]
    public async Task RegionCompareViewModel_Explain_AllSameWithFingerprints()
    {
        using var dl = new TempDir();
        using var dr = new TempDir();
        var chunk = WorldBuilder.MakeChunk(42, 0, 0);
        RegionWriter.Write(dl.File("r.0.0.mca"), [new ChunkSpec(0, 0, chunk, ChunkRef.SchemeZLib)]);
        RegionWriter.Write(dr.File("r.0.0.mca"), [new ChunkSpec(0, 0, (NbtCompound)chunk.Clone(), ChunkRef.SchemeGZip)]);

        async Task<RegionCompareViewModel> Loaded(FileFingerprint? lfp, FileFingerprint? rfp)
        {
            var vm = new RegionCompareViewModel(dl.File("r.0.0.mca"), dr.File("r.0.0.mca"), lfp, rfp, new ImmediateUiDispatcher());
            await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
            return vm;
        }

        // Same content, different compression: every chunk matches.
        using var plain = await Loaded(null, null);
        Assert.Equal(ChunkDiffStatus.Same, plain.Grid[0, 0].Status);
        Assert.Equal("1 chunk · 1 same", plain.Grid.CountsText);
        Assert.Equal("1 chunk · 1 same", plain.HeaderText);

        // Unverified (quick) fingerprints that disagree: say the bytes-only reason.
        using var quick = await Loaded(
            new FileFingerprint(FileKind.Region, FingerprintTier.Quick, 1000, 0x11),
            new FileFingerprint(FileKind.Region, FingerprintTier.Quick, 1001, 0x22));
        Assert.Equal(ChunkDiffStatus.Same, quick.Grid[0, 0].Status);
        Assert.Equal("1 chunk · 1 same · bytes differ (recompression, sector layout or timestamps) but every chunk's content matches", quick.HeaderText);

        // Verified (deep) fingerprints that disagree with no differing chunk: report-worthy.
        using var deep = await Loaded(
            new FileFingerprint(FileKind.Region, FingerprintTier.Deep, 1000, 0x11),
            new FileFingerprint(FileKind.Region, FingerprintTier.Deep, 1001, 0x22));
        Assert.Equal("1 chunk · 1 same · the scan marked this file different but no chunk differs — please report this", deep.HeaderText);

        // Equal fingerprints: nothing to explain.
        using var equal = await Loaded(
            new FileFingerprint(FileKind.Region, FingerprintTier.Deep, 1000, 0x11),
            new FileFingerprint(FileKind.Region, FingerprintTier.Deep, 1000, 0x11));
        Assert.Equal("1 chunk · 1 same", equal.HeaderText);
    }

    [Fact]
    public async Task RegionCompareViewModel_Select_Move_Open_Gates()
    {
        using var dl = new TempDir();
        using var dr = new TempDir();
        var c00 = WorldBuilder.MakeChunk(42, 0, 0);
        var c10 = WorldBuilder.MakeChunk(42, 1, 0);
        RegionWriter.Write(dl.File("r.0.0.mca"), [new ChunkSpec(0, 0, c00), new ChunkSpec(1, 0, c10)]);
        RegionWriter.Write(dr.File("r.0.0.mca"), [new ChunkSpec(0, 0, c00), new ChunkSpec(1, 0, c10)]);

        var vm = new RegionCompareViewModel(dl.File("r.0.0.mca"), dr.File("r.0.0.mca"), null, null, new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, vm.Grid.Present);
        Assert.Equal((0, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));   // first occupied cell

        vm.SelectCommand.Execute(vm.Grid[1, 0]);
        Assert.Same(vm.Grid[1, 0], vm.Grid.Selected);
        vm.SelectCommand.Execute(null);                                    // no-op
        Assert.Same(vm.Grid[1, 0], vm.Grid.Selected);

        // Opening an empty slot is a no-op; a present chunk opens its tag view.
        var opened = new List<ViewModelBase>();
        vm.NavigationRequested += opened.Add;
        vm.SelectCommand.Execute(vm.Grid[5, 5]);
        vm.OpenSelectedCommand.Execute(null);
        Assert.Empty(opened);
        vm.SelectCommand.Execute(vm.Grid[1, 0]);
        vm.OpenSelectedCommand.Execute(null);
        Assert.IsType<FileCompareViewModel>(Assert.Single(opened));

        // Arrow movement from (1, 0).
        vm.SelectCommand.Execute(vm.Grid[1, 0]);
        vm.MoveCommand.Execute("right");
        Assert.Equal((2, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));
        vm.MoveCommand.Execute("down");
        Assert.Equal((2, 1), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));
        vm.MoveCommand.Execute("left");
        Assert.Equal((1, 1), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));
        vm.MoveCommand.Execute("up");
        Assert.Equal((1, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));

        // Clamped at the corner: the selection does not move.
        vm.SelectCommand.Execute(vm.Grid[0, 0]);
        vm.MoveCommand.Execute("left");
        Assert.Equal((0, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));
        vm.MoveCommand.Execute("up");
        Assert.Equal((0, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));
    }

    // ── ChunkCellItem / TextDiffRowItem ─────────────────────────

    [Fact]
    public void ChunkCellItem_Display()
    {
        var cell = new ChunkCellItem(3, 4);
        Assert.Equal(3, cell.X);
        Assert.Equal(4, cell.Z);
        Assert.False(cell.IsPresent);
        Assert.Equal("(3, 4): no chunk on either side", cell.ToolTipText);
        Assert.Equal(cell.ToolTipText, cell.ToString());

        cell.Status = ChunkDiffStatus.Same;
        Assert.True(cell.IsPresent);
        Assert.Equal("(3, 4): same", cell.ToolTipText);

        cell.Status = ChunkDiffStatus.Error;
        Assert.Equal("(3, 4): error", cell.ToolTipText);
        cell.Error = "chunk blew up";
        Assert.Equal("chunk blew up", cell.ToolTipText);

        cell.Status = ChunkDiffStatus.Different;
        Assert.Equal("(3, 4): different", cell.ToolTipText);
        cell.Status = ChunkDiffStatus.LeftOnly;
        Assert.Equal("(3, 4): only on the left", cell.ToolTipText);
        cell.Status = ChunkDiffStatus.RightOnly;
        Assert.Equal("(3, 4): only on the right", cell.ToolTipText);

        Assert.Equal("same", ChunkCellItem.Describe(ChunkDiffStatus.Same));
        Assert.Equal("different", ChunkCellItem.Describe(ChunkDiffStatus.Different));
        Assert.Equal("only on the left", ChunkCellItem.Describe(ChunkDiffStatus.LeftOnly));
        Assert.Equal("only on the right", ChunkCellItem.Describe(ChunkDiffStatus.RightOnly));
        Assert.Equal("error", ChunkCellItem.Describe(ChunkDiffStatus.Error));
        Assert.Equal("99", ChunkCellItem.Describe((ChunkDiffStatus)99));
    }

    [Fact]
    public void TextDiffRowItem_Display()
    {
        void Check(LineDiffKind kind, int? leftLine, string? leftText, int? rightLine, string? rightText, string glyph)
        {
            var row = new LineDiffRow(kind, leftLine, leftText, rightLine, rightText);
            var item = new TextDiffRowItem(row, 7, kind != LineDiffKind.Unchanged);
            Assert.Same(row, item.Row);
            Assert.Equal(kind, item.Kind);
            Assert.Equal(7, item.Index);
            Assert.Equal(leftText ?? "", item.LeftText);
            Assert.Equal(rightText ?? "", item.RightText);
            Assert.Equal(leftLine is not null, item.HasLeft);
            Assert.Equal(rightLine is not null, item.HasRight);
            Assert.Equal(glyph, item.StatusGlyph);
        }

        Check(LineDiffKind.Unchanged, 1, "a", 1, "a", "");
        Check(LineDiffKind.Changed, 1, "a", 1, "b", "≠");
        Check(LineDiffKind.Removed, 1, "a", null, null, "−");
        Check(LineDiffKind.Added, null, null, 1, "b", "+");
    }

    // ── Diff sources ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileDiffSource_Save_ErrorPaths()
    {
        var s = new FileDiffSource(null, null);
        Assert.Null(s.LeftPath);
        Assert.Null(s.RightPath);
        Assert.Equal("(missing)", s.Title);
        Assert.Equal("The left side has no loaded file to save to", s.Save(false, new NbtCompound("")).Failure!.ToShortString());
        Assert.Equal("The right side has no loaded file to save to", s.Save(true, new NbtCompound("")).Failure!.ToShortString());
        // (The "only a compound root" branch needs a loaded doc; tested below.)
        Assert.Equal("Neither file exists", s.Load().Failure!.ToShortString());

        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        var loaded = new FileDiffSource(d.File("a.dat"), null);
        var pair = loaded.Load().ValueOrThrow();
        Assert.NotNull(pair.Left);
        Assert.Null(pair.Right);
        // A loaded doc reaches the compound-root guard: a non-compound root is refused.
        Assert.Equal("Only a compound root can be saved to a file", loaded.Save(false, new NbtInt("k", 1)).Failure!.ToShortString());
    }

    [Fact]
    public void ChunkDiffSource_Save_ErrorPaths_And_MissingChunk()
    {
        var s = new ChunkDiffSource(null, null, 1, 2, "region");
        Assert.Equal(1, s.X);
        Assert.Equal(2, s.Z);
        Assert.Equal("region (1, 2)", s.Title);
        Assert.Equal("The left region file is not open", s.Save(false, new NbtCompound("")).Failure!.ToShortString());
        Assert.Equal("The right region file is not open", s.Save(true, new NbtCompound("")).Failure!.ToShortString());
        // (The "only a compound root" branch needs an open region; tested below.)
        Assert.Equal("No chunk at (1, 2) on either side", s.Load().Failure!.ToShortString());

        using var d = new TempDir();
        RegionWriter.Write(d.File("r.0.0.mca"), [new ChunkSpec(0, 0, new NbtCompound("") { new NbtInt("x", 1) })]);
        using var left = RegionFile.Open(d.File("r.0.0.mca")).ValueOrThrow();
        var withLeft = new ChunkDiffSource(left, null, 0, 0, "r.0.0.mca");
        var pair = withLeft.Load().ValueOrThrow();
        Assert.NotNull(pair.Left);
        Assert.Null(pair.Right);
        // An open region reaches the compound-root guard: a non-compound root is refused.
        Assert.Equal("Only a compound root can be saved as a chunk", withLeft.Save(false, new NbtInt("k", 1)).Failure!.ToShortString());
    }
}
