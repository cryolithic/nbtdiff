using fNbt;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;

namespace NbtDiff.App.Tests;

public class ArrayDetailTests
{
    private static DiffNode Node(int[] left, int[] right) =>
        NbtDiffer.Diff(new NbtIntArray("a", left), new NbtIntArray("a", right));

    [Fact]
    public void DifferenceInTheMiddle_IsCentered()
    {
        var l = Enumerable.Range(0, 100).ToArray();
        var r = (int[])l.Clone();
        r[50] = -1;
        var rows = ArrayDetailWindow.Compute(Node(l, r))!;
        Assert.Equal(33, rows.Count);
        Assert.Equal(34, rows[0].Index);
        Assert.Equal(66, rows[^1].Index);
        var diff = Assert.Single(rows, x => x.IsDifferent);
        Assert.Equal(50, diff.Index);
    }

    [Fact]
    public void DifferenceNearStart_ClampsAtZero()
    {
        var l = Enumerable.Range(0, 100).ToArray();
        var r = (int[])l.Clone();
        r[2] = -1;
        var rows = ArrayDetailWindow.Compute(Node(l, r))!;
        Assert.Equal(0, rows[0].Index);
        Assert.Equal(18, rows[^1].Index);
        Assert.True(rows[2].IsDifferent);
    }

    [Fact]
    public void DifferenceNearEnd_ClampsAtLength()
    {
        var l = Enumerable.Range(0, 100).ToArray();
        var r = (int[])l.Clone();
        r[98] = -1;
        var rows = ArrayDetailWindow.Compute(Node(l, r))!;
        Assert.Equal(82, rows[0].Index);
        Assert.Equal(99, rows[^1].Index);
    }

    [Fact]
    public void DifferentLengths_ShorterSideIsNull()
    {
        var l = Enumerable.Range(0, 10).ToArray();
        var r = Enumerable.Range(0, 6).ToArray();
        var node = Node(l, r);
        Assert.Equal(6, node.Array!.FirstDifference);
        var rows = ArrayDetailWindow.Compute(node, context: 2)!;
        Assert.Equal([4, 5, 6, 7, 8], rows.Select(x => x.Index));
        Assert.Equal(["4", "5", "6", "7", "8"], rows.Select(x => x.Left));
        Assert.Equal(["4", "5", null, null, null], rows.Select(x => x.Right));
        Assert.Equal([false, false, true, true, true], rows.Select(x => x.IsDifferent));
    }

    [Fact]
    public void IdenticalArrays_WindowFromStart_NothingFlagged()
    {
        var l = Enumerable.Range(0, 50).ToArray();
        var rows = ArrayDetailWindow.Compute(Node(l, (int[])l.Clone()))!;
        Assert.Equal(17, rows.Count);
        Assert.Equal(0, rows[0].Index);
        Assert.DoesNotContain(rows, x => x.IsDifferent);
    }

    [Fact]
    public void OneSidedArray_AllRowsDifferent()
    {
        var node = NbtDiffer.Diff(null, new NbtByteArray("b", [1, 0xFF, 3]));
        var rows = ArrayDetailWindow.Compute(node)!;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, x => Assert.Null(x.Left));
        Assert.Equal(["1", "-1", "3"], rows.Select(x => x.Right));   // bytes are signed
        Assert.All(rows, x => Assert.True(x.IsDifferent));
    }

    [Fact]
    public void EmptyArrays_EmptyWindow()
    {
        Assert.Empty(ArrayDetailWindow.Compute(Node([], []))!);
    }

    [Fact]
    public void NotAnArray_Null()
    {
        Assert.Null(ArrayDetailWindow.Compute(NbtDiffer.Diff(new NbtInt("i", 1), new NbtInt("i", 2))));
    }
}
