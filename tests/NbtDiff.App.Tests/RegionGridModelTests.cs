using NbtDiff.App.ViewModels;
using NbtDiff.Core;

namespace NbtDiff.App.Tests;

public class RegionGridModelTests
{
    private static RegionGridModel Filled()
    {
        var g = new RegionGridModel();
        g.Apply([
            new ChunkDiffCell(0, 0, ChunkDiffStatus.Same),
            new ChunkDiffCell(1, 0, ChunkDiffStatus.Same),
            new ChunkDiffCell(3, 1, ChunkDiffStatus.Different),
            new ChunkDiffCell(31, 31, ChunkDiffStatus.LeftOnly),
            new ChunkDiffCell(0, 31, ChunkDiffStatus.RightOnly),
            new ChunkDiffCell(5, 5, ChunkDiffStatus.Error, "Chunk (5, 5): broken"),
        ]);
        return g;
    }

    [Fact]
    public void Apply_SetsStatusesAndCounts()
    {
        var g = Filled();
        Assert.Equal(1024, g.Cells.Count);
        Assert.Equal(ChunkDiffStatus.Different, g[3, 1].Status);
        Assert.Null(g[2, 2].Status);
        Assert.False(g[2, 2].IsPresent);
        Assert.Equal((6, 2, 1, 1, 1, 1), (g.Present, g.Same, g.Different, g.LeftOnly, g.RightOnly, g.Errors));
        Assert.Equal("6 chunks · 1 different · 2 same · 1 left-only · 1 right-only · 1 error", g.CountsText);
        Assert.Equal("Chunk (5, 5): broken", g[5, 5].ToolTipText);
        Assert.Equal("(2, 2): no chunk on either side", g[2, 2].ToolTipText);
        Assert.Equal("(3, 1): different", g[3, 1].ToolTipText);
    }

    [Fact]
    public void Apply_SelectsFirstInterestingCell_RowMajor()
    {
        var g = Filled();
        Assert.Same(g[3, 1], g.Selected);   // first non-Same in (z, x) order
        Assert.True(g[3, 1].IsSelected);

        var allSame = new RegionGridModel();
        allSame.Apply([new ChunkDiffCell(7, 2, ChunkDiffStatus.Same), new ChunkDiffCell(1, 3, ChunkDiffStatus.Same)]);
        Assert.Same(allSame[7, 2], allSame.Selected);

        var empty = new RegionGridModel();
        empty.Apply([]);
        Assert.Null(empty.Selected);
        Assert.Equal("no chunks", empty.CountsText);
    }

    [Fact]
    public void Reapply_ClearsOldCells()
    {
        var g = Filled();
        g.Apply([new ChunkDiffCell(9, 9, ChunkDiffStatus.Same)]);
        Assert.Null(g[3, 1].Status);
        Assert.Null(g[5, 5].Error);
        Assert.Equal(1, g.Present);
    }

    [Fact]
    public void Select_MovesHighlight()
    {
        var g = Filled();
        var before = g.Selected!;
        g.Select(10, 10);
        Assert.False(before.IsSelected);
        Assert.True(g[10, 10].IsSelected);
        Assert.Equal("(10, 10): no chunk on either side", g.SelectionText);
    }

    [Fact]
    public void Move_ClampsAtEdges()
    {
        var g = Filled();
        g.Select(0, 0);
        Assert.False(g.Move(-1, 0));
        Assert.False(g.Move(0, -1));
        Assert.Same(g[0, 0], g.Selected);
        Assert.True(g.Move(1, 0));
        Assert.Same(g[1, 0], g.Selected);
        Assert.True(g.Move(0, 1));
        Assert.Same(g[1, 1], g.Selected);

        g.Select(31, 31);
        Assert.False(g.Move(1, 0));
        Assert.False(g.Move(0, 1));
        Assert.Same(g[31, 31], g.Selected);
        Assert.True(g.Move(-1, -1));
        Assert.Same(g[30, 30], g.Selected);
    }

    [Fact]
    public void Move_FromNoSelection_LandsOnOrigin()
    {
        var g = new RegionGridModel();
        Assert.True(g.Move(1, 1));
        Assert.Same(g[0, 0], g.Selected);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var g = new RegionGridModel();
        Assert.Throws<ArgumentOutOfRangeException>(() => g[32, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => g[0, -1]);
    }
}
