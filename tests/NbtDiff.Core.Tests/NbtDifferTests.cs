using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

public class NbtDifferTests
{
    private static readonly DiffOptions Ordered = new(CompoundOrderMatters: true);

    /// <summary>A named tag of the given type; <paramref name="variant"/> 0 and 1 differ in value.</summary>
    private static NbtTag Make(NbtTagType type, int variant, string? name = "t") => type switch
    {
        NbtTagType.Byte => new NbtByte(name, (byte)(1 + variant)),
        NbtTagType.Short => new NbtShort(name, (short)(100 + variant)),
        NbtTagType.Int => new NbtInt(name, 1000 + variant),
        NbtTagType.Long => new NbtLong(name, 1L << 40 + variant),
        NbtTagType.Float => new NbtFloat(name, 1.5f + variant),
        NbtTagType.Double => new NbtDouble(name, 2.5 + variant),
        NbtTagType.String => new NbtString(name, "v" + variant),
        NbtTagType.ByteArray => new NbtByteArray(name, [1, (byte)(2 + variant), 3]),
        NbtTagType.IntArray => new NbtIntArray(name, [1, 2 + variant, 3]),
        NbtTagType.LongArray => new NbtLongArray(name, [1, 2 + variant, 3]),
        NbtTagType.List => new NbtList(name, NbtTagType.Int) { new NbtInt(7 + variant) },
        NbtTagType.Compound => new NbtCompound(name) { new NbtInt("inner", 9 + variant) },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static TheoryData<NbtTagType> AllTypes =>
    [
        NbtTagType.Byte, NbtTagType.Short, NbtTagType.Int, NbtTagType.Long, NbtTagType.Float, NbtTagType.Double,
        NbtTagType.String, NbtTagType.ByteArray, NbtTagType.IntArray, NbtTagType.LongArray, NbtTagType.List, NbtTagType.Compound,
    ];

    [Theory, MemberData(nameof(AllTypes))]
    public void Unchanged(NbtTagType type)
    {
        var d = NbtDiffer.Diff(Make(type, 0), Make(type, 0));
        Assert.Equal(DiffKind.Unchanged, d.Kind);
        Assert.Equal(0, d.ChangedDescendants);
        Assert.False(d.HasChanges);
        Assert.Equal(type, d.LeftType);
        Assert.Equal(type, d.RightType);
    }

    [Theory, MemberData(nameof(AllTypes))]
    public void Changed(NbtTagType type)
    {
        var d = NbtDiffer.Diff(Make(type, 0), Make(type, 1));
        Assert.True(d.HasChanges);
        if (type is NbtTagType.List or NbtTagType.Compound)
        {
            // Containers stay Unchanged themselves; the change is on the child.
            Assert.Equal(DiffKind.Unchanged, d.Kind);
            Assert.Equal(1, d.ChangedDescendants);
            Assert.Equal(DiffKind.ValueChanged, Assert.Single(d.Children).Kind);
        }
        else
        {
            Assert.Equal(DiffKind.ValueChanged, d.Kind);
            Assert.Equal(1, d.ChangedDescendants);
            Assert.Empty(d.Children);
        }
    }

    [Theory, MemberData(nameof(AllTypes))]
    public void Added(NbtTagType type)
    {
        var d = NbtDiffer.Diff(null, Make(type, 0));
        Assert.Equal(DiffKind.Added, d.Kind);
        Assert.Null(d.Left);
        Assert.NotNull(d.Right);
        Assert.Null(d.LeftType);
        Assert.All(d.Descendants(), n => Assert.Equal(DiffKind.Added, n.Kind));
        Assert.Equal(d.Descendants().Count(), d.ChangedDescendants);
    }

    [Theory, MemberData(nameof(AllTypes))]
    public void Removed(NbtTagType type)
    {
        var d = NbtDiffer.Diff(Make(type, 0), null);
        Assert.Equal(DiffKind.Removed, d.Kind);
        Assert.NotNull(d.Left);
        Assert.Null(d.Right);
        Assert.All(d.Descendants(), n => Assert.Equal(DiffKind.Removed, n.Kind));
        Assert.Equal(d.Descendants().Count(), d.ChangedDescendants);
    }

    [Theory, MemberData(nameof(AllTypes))]
    public void TypeChanged_StopsRecursion(NbtTagType type)
    {
        var other = type == NbtTagType.Compound ? NbtTagType.List : NbtTagType.Compound;
        var d = NbtDiffer.Diff(Make(type, 0), Make(other, 0));
        Assert.Equal(DiffKind.TypeChanged, d.Kind);
        Assert.Empty(d.Children);
        Assert.Equal(1, d.ChangedDescendants);
        Assert.Equal(type, d.LeftType);
        Assert.Equal(other, d.RightType);
    }

    [Fact]
    public void BothNull_Throws()
    {
        Assert.Throws<ArgumentException>(() => NbtDiffer.Diff(null, null));
    }

    [Fact]
    public void OneSided_SubtreeIsExpanded()
    {
        var c = new NbtCompound("c") { new NbtInt("a", 1), new NbtList("l", NbtTagType.Int) { new NbtInt(1), new NbtInt(2) } };
        var d = NbtDiffer.Diff(null, c);
        Assert.Equal(["a", "l"], d.Children.Select(x => x.Name));
        Assert.Equal(["[0]", "[1]"], d.Children[1].Children.Select(x => x.Name));
        Assert.Equal("l/[1]", d.Children[1].Children[1].Path);
        Assert.Equal(5, d.ChangedDescendants);
    }

    [Fact]
    public void Nested_ChangeDeepInside_CountsAndPaths()
    {
        var a = WorldBuilder.MakeChunk(1, 0, 0);
        var b = (NbtCompound)a.Clone();
        var data = (NbtLongArray)((NbtCompound)((NbtCompound)((NbtList)b["sections"])[0])["block_states"])["data"];
        data.Value[17] ^= 1;

        var d = NbtDiffer.Diff(a, b);
        Assert.Equal(1, d.ChangedDescendants);
        var changed = Assert.Single(d.Descendants(), n => n.Kind != DiffKind.Unchanged);
        Assert.Equal("sections/[0]/block_states/data", changed.Path);
        Assert.Equal("data", changed.Name);
        Assert.Equal(DiffKind.ValueChanged, changed.Kind);
        Assert.Equal(new ArrayDifference(17, 256, 256), changed.Array);

        // Every ancestor of the change reports exactly one changed descendant; unrelated siblings zero.
        Assert.Equal(1, d.Children.Single(c => c.Name == "sections").ChangedDescendants);
        Assert.Equal(0, d.Children.Single(c => c.Name == "Heightmaps").ChangedDescendants);
    }

    [Fact]
    public void Compound_AddedRemovedChanged_Counts()
    {
        var a = new NbtCompound("") { new NbtInt("keep", 1), new NbtInt("change", 1), new NbtInt("remove", 1) };
        var b = new NbtCompound("") { new NbtInt("keep", 1), new NbtInt("change", 2), new NbtInt("add", 1) };
        var d = NbtDiffer.Diff(a, b);
        Assert.Equal(3, d.ChangedDescendants);
        Assert.Equal(DiffKind.Unchanged, d.Kind);
        // Order-insensitive output is sorted by key.
        Assert.Equal(["add", "change", "keep", "remove"], d.Children.Select(c => c.Name));
        Assert.Equal([DiffKind.Added, DiffKind.ValueChanged, DiffKind.Unchanged, DiffKind.Removed], d.Children.Select(c => c.Kind));
    }

    [Fact]
    public void Compound_KeyOrder_IgnoredByDefault()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2), new NbtCompound("z") { new NbtByte("p", 1), new NbtByte("q", 2) } };
        var b = new NbtCompound("") { new NbtCompound("z") { new NbtByte("q", 2), new NbtByte("p", 1) }, new NbtInt("y", 2), new NbtInt("x", 1) };
        Assert.Equal(0, NbtDiffer.Diff(a, b).ChangedDescendants);
    }

    [Fact]
    public void Compound_KeyOrder_ReportedAsMovedWhenAsked()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2), new NbtInt("z", 3) };
        var b = new NbtCompound("") { new NbtInt("z", 3), new NbtInt("y", 2), new NbtInt("x", 1) };
        var d = NbtDiffer.Diff(a, b, Ordered);
        Assert.True(d.HasChanges);
        Assert.Equal(["x", "y", "z"], d.Children.Select(c => c.Name));        // left order
        Assert.Equal([DiffKind.Moved, DiffKind.Unchanged, DiffKind.Moved], d.Children.Select(c => c.Kind));
        Assert.Equal(2, d.ChangedDescendants);
    }

    [Fact]
    public void Compound_OrderMatters_InsertionDoesNotMoveSiblings()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2) };
        var b = new NbtCompound("") { new NbtInt("new", 0), new NbtInt("x", 1), new NbtInt("y", 2) };
        var d = NbtDiffer.Diff(a, b, Ordered);
        Assert.Equal(["x", "y", "new"], d.Children.Select(c => c.Name));
        Assert.Equal([DiffKind.Unchanged, DiffKind.Unchanged, DiffKind.Added], d.Children.Select(c => c.Kind));
    }

    [Fact]
    public void Compound_OrderMatters_ValueChangeWinsOverMoved()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2) };
        var b = new NbtCompound("") { new NbtInt("y", 2), new NbtInt("x", 9) };
        var d = NbtDiffer.Diff(a, b, Ordered);
        Assert.Equal([DiffKind.ValueChanged, DiffKind.Moved], d.Children.Select(c => c.Kind));
    }

    [Fact]
    public void List_IndexAligned()
    {
        var a = new NbtList("l", NbtTagType.Int) { new NbtInt(1), new NbtInt(2), new NbtInt(3) };
        var b = new NbtList("l", NbtTagType.Int) { new NbtInt(1), new NbtInt(9) };
        var d = NbtDiffer.Diff(a, b);
        Assert.Equal(["[0]", "[1]", "[2]"], d.Children.Select(c => c.Name));
        Assert.Equal([DiffKind.Unchanged, DiffKind.ValueChanged, DiffKind.Removed], d.Children.Select(c => c.Kind));
        Assert.Equal(2, d.ChangedDescendants);
        Assert.Equal("[2]", d.Children[2].Path);
    }

    [Fact]
    public void List_Reorder_IsAChange()
    {
        var a = new NbtList("l", NbtTagType.String) { new NbtString("a"), new NbtString("b") };
        var b = new NbtList("l", NbtTagType.String) { new NbtString("b"), new NbtString("a") };
        Assert.Equal(2, NbtDiffer.Diff(a, b).ChangedDescendants);
    }

    [Fact]
    public void List_ElementTypeChange_StopsRecursion()
    {
        var a = new NbtList("l", NbtTagType.Int) { new NbtInt(1) };
        var b = new NbtList("l", NbtTagType.Byte) { new NbtByte(1) };
        var d = NbtDiffer.Diff(a, b);
        Assert.Equal(DiffKind.TypeChanged, d.Kind);
        Assert.Empty(d.Children);
    }

    [Fact]
    public void List_EmptyListType_IsNotADifference()
    {
        Assert.Equal(0, NbtDiffer.Diff(new NbtList("l"), new NbtList("l", NbtTagType.Compound)).ChangedDescendants);
        Assert.Equal(0, NbtDiffer.Diff(new NbtList("l", NbtTagType.End), new NbtList("l", NbtTagType.Int)).ChangedDescendants);
        // Empty vs non-empty of another declared type: items are Added, not TypeChanged.
        var d = NbtDiffer.Diff(new NbtList("l"), new NbtList("l", NbtTagType.Int) { new NbtInt(1) });
        Assert.Equal(DiffKind.Unchanged, d.Kind);
        Assert.Equal(DiffKind.Added, Assert.Single(d.Children).Kind);
    }

    [Fact]
    public void List_OfCompounds_ChangesInside()
    {
        var a = new NbtList("l", NbtTagType.Compound) { new NbtCompound { new NbtString("id", "a") }, new NbtCompound { new NbtString("id", "b") } };
        var b = new NbtList("l", NbtTagType.Compound) { new NbtCompound { new NbtString("id", "a") }, new NbtCompound { new NbtString("id", "c") } };
        var d = NbtDiffer.Diff(a, b);
        Assert.Equal(1, d.ChangedDescendants);
        Assert.Equal("[1]/id", d.Descendants().Single(n => n.Kind == DiffKind.ValueChanged).Path);
    }

    [Fact]
    public void CustomAligner_IsUsed()
    {
        var a = new NbtList("l", NbtTagType.Int) { new NbtInt(1), new NbtInt(2) };
        var b = new NbtList("l", NbtTagType.Int) { new NbtInt(2), new NbtInt(1) };
        var d = NbtDiffer.Diff(a, b, new DiffOptions(ListAligner: new ReverseAligner()));
        Assert.Equal(0, d.ChangedDescendants);
        Assert.Equal(["[0]", "[1]"], d.Children.Select(c => c.Name));
    }

    private sealed class ReverseAligner : IListAligner
    {
        public IReadOnlyList<(int? Left, int? Right)> Align(NbtList left, NbtList right) =>
            Enumerable.Range(0, left.Count).Select(i => ((int?)i, (int?)(right.Count - 1 - i))).ToList();
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2, 3 }, false, -1)]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 9, 3 }, true, 1)]
    [InlineData(new long[] { 1, 2, 3 }, new long[] { 1, 2 }, true, 2)]
    [InlineData(new long[] { 1, 2 }, new long[] { 1, 2, 3 }, true, 2)]
    [InlineData(new long[] { }, new long[] { 1 }, true, 0)]
    [InlineData(new long[] { }, new long[] { }, false, -1)]
    [InlineData(new long[] { 5, 2 }, new long[] { 1, 2, 3 }, true, 0)]
    public void Arrays_FirstDifferenceAndLengths(long[] left, long[] right, bool changed, int first)
    {
        var d = NbtDiffer.Diff(new NbtLongArray("a", left), new NbtLongArray("a", right));
        Assert.Equal(changed, d.HasChanges);
        if (changed)
        {
            Assert.Equal(DiffKind.ValueChanged, d.Kind);
            Assert.Equal(new ArrayDifference(first, left.Length, right.Length), d.Array);
        }
        else
        {
            Assert.Null(d.Array);
        }
    }

    [Fact]
    public void Arrays_ByteAndInt()
    {
        var b = NbtDiffer.Diff(new NbtByteArray("a", [1, 2, 255]), new NbtByteArray("a", [1, 2, 254]));
        Assert.Equal(new ArrayDifference(2, 3, 3), b.Array);
        var i = NbtDiffer.Diff(new NbtIntArray("a", [1, 2]), new NbtIntArray("a", [1, 2]));
        Assert.False(i.HasChanges);
    }

    [Fact]
    public void Floats_ByBits()
    {
        Assert.True(NbtDiffer.Diff(new NbtDouble("d", 0.0), new NbtDouble("d", -0.0)).HasChanges);
        Assert.False(NbtDiffer.Diff(new NbtDouble("d", double.NaN), new NbtDouble("d", double.NaN)).HasChanges);
        Assert.True(NbtDiffer.Diff(new NbtFloat("f", 1.5f), new NbtFloat("f", 1.5000001f)).HasChanges);
    }

    [Fact]
    public void RootName_NullVsEmpty_IsRenamed()
    {
        var d = NbtDiffer.Diff(new NbtInt("", 1), new NbtInt(1));
        Assert.Equal(DiffKind.Renamed, d.Kind);
        Assert.Equal(1, d.ChangedDescendants);
        Assert.Equal(DiffKind.Renamed, NbtDiffer.Diff(new NbtCompound("a"), new NbtCompound("b")).Kind);
        // Renamed root with a changed child counts both.
        Assert.Equal(2, NbtDiffer.Diff(new NbtCompound("a") { new NbtInt("x", 1) }, new NbtCompound("b") { new NbtInt("x", 2) }).ChangedDescendants);
    }

    [Fact]
    public void Root_NameAndPath()
    {
        var d = NbtDiffer.Diff(new NbtCompound("root") { new NbtInt("x", 1) }, new NbtCompound("root") { new NbtInt("x", 1) });
        Assert.Equal("", d.Name);
        Assert.Equal("", d.Path);
        Assert.Equal("x", d.Children[0].Path);
        Assert.Contains("<root>", d.ToString());
    }
}
