using System.Diagnostics;
using NbtDiff.Core.Diff;

namespace NbtDiff.Core.Tests;

public class LineDifferTests
{
    private static string Render(LineDiffResult r) => string.Join(" ", r.Rows.Select(row => row.Kind switch
    {
        LineDiffKind.Unchanged => $"={row.LeftText}",
        LineDiffKind.Changed => $"~{row.LeftText}>{row.RightText}",
        LineDiffKind.Removed => $"-{row.LeftText}",
        _ => $"+{row.RightText}",
    }));

    [Fact]
    public void Identical()
    {
        var r = LineDiffer.Diff("a\nb\nc\n", "a\nb\nc\n");
        Assert.False(r.HasChanges);
        Assert.Equal("=a =b =c", Render(r));
        Assert.Equal([1, 2, 3], r.Rows.Select(x => x.LeftLine));
        Assert.Equal([1, 2, 3], r.Rows.Select(x => x.RightLine));
    }

    [Fact]
    public void Insert()
    {
        var r = LineDiffer.Diff("a\nc\n", "a\nb\nc\n");
        Assert.Equal("=a +b =c", Render(r));
        Assert.Equal(1, r.Added);
        Assert.Equal([1], r.HunkStarts);
        var added = r.Rows[1];
        Assert.Null(added.LeftLine);
        Assert.Equal(2, added.RightLine);
    }

    [Fact]
    public void Delete()
    {
        var r = LineDiffer.Diff("a\nb\nc\n", "a\nc\n");
        Assert.Equal("=a -b =c", Render(r));
        Assert.Equal(1, r.Removed);
    }

    [Fact]
    public void Change_PairsDeleteWithInsert()
    {
        var r = LineDiffer.Diff("a\nb\nc\n", "a\nB\nc\n");
        Assert.Equal("=a ~b>B =c", Render(r));
        Assert.Equal(1, r.Changed);
        Assert.Equal(0, r.Added + r.Removed);
        Assert.Equal(2, r.Rows[1].LeftLine);
        Assert.Equal(2, r.Rows[1].RightLine);
    }

    [Fact]
    public void Change_UnevenHunk()
    {
        var r = LineDiffer.Diff("a\nb\nc\nd\n", "a\nX\nd\n");
        Assert.Equal("=a ~b>X -c =d", Render(r));
        Assert.Equal([1], r.HunkStarts);
    }

    [Fact]
    public void LineEndings_Equal()
    {
        Assert.False(LineDiffer.Diff("a\r\nb\r\n", "a\nb\n").HasChanges);
        Assert.False(LineDiffer.Diff("a\rb\r", "a\nb").HasChanges);
        Assert.False(LineDiffer.Diff("a\nb", "a\nb\n").HasChanges);
    }

    [Fact]
    public void Empty()
    {
        Assert.Empty(LineDiffer.Diff("", "").Rows);
        Assert.Equal("+a +b", Render(LineDiffer.Diff("", "a\nb\n")));
        Assert.Equal("-a", Render(LineDiffer.Diff("a", "")));
        Assert.Equal([""], LineDiffer.SplitLines("\n"));
        Assert.Equal(["", "x"], LineDiffer.SplitLines("\nx"));
    }

    [Fact]
    public void MultipleHunks()
    {
        var r = LineDiffer.Diff("a\nb\nc\nd\ne\n", "a\nB\nc\nd\nE\nf\n");
        Assert.Equal("=a ~b>B =c =d ~e>E +f", Render(r));
        Assert.Equal([1, 4], r.HunkStarts);
    }

    [Fact]
    public void CompletelyDifferent()
    {
        var r = LineDiffer.Diff("a\nb\n", "c\nd\ne\n");
        Assert.Equal("~a>c ~b>d +e", Render(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Random_IsOptimalAndReconstructsBothSides(int seed)
    {
        var rng = new Random(seed);
        for (int trial = 0; trial < 200; trial++)
        {
            var left = Enumerable.Range(0, rng.Next(0, 14)).Select(_ => rng.Next(4).ToString()).ToList();
            var right = Enumerable.Range(0, rng.Next(0, 14)).Select(_ => rng.Next(4).ToString()).ToList();
            var r = LineDiffer.Diff(left, right);

            // Reconstruct both sides from the rows.
            Assert.Equal(left, r.Rows.Where(x => x.LeftLine is not null).Select(x => x.LeftText));
            Assert.Equal(right, r.Rows.Where(x => x.RightLine is not null).Select(x => x.RightText));
            Assert.Equal(Enumerable.Range(1, left.Count), r.Rows.Where(x => x.LeftLine is not null).Select(x => x.LeftLine!.Value));
            Assert.Equal(Enumerable.Range(1, right.Count), r.Rows.Where(x => x.RightLine is not null).Select(x => x.RightLine!.Value));

            // Myers is optimal: the number of unchanged rows is the LCS length.
            int unchanged = r.Rows.Count(x => x.Kind == LineDiffKind.Unchanged);
            Assert.Equal(Lcs(left, right), unchanged);
        }
    }

    [Fact]
    public void LargeInput_IsFast()
    {
        var left = Enumerable.Range(0, 5000).Select(i => $"line {i}").ToList();
        var right = left.Select((l, i) => i % 2 == 0 ? l : l + " changed").ToList();
        right.RemoveRange(1000, 300);
        right.InsertRange(3000, Enumerable.Range(0, 200).Select(i => $"new {i}"));

        var sw = Stopwatch.StartNew();
        var r = LineDiffer.Diff(left, right);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(left, r.Rows.Where(x => x.LeftLine is not null).Select(x => x.LeftText));
        Assert.Equal(right, r.Rows.Where(x => x.RightLine is not null).Select(x => x.RightText));
        Assert.Equal(Lcs(left, right), r.Rows.Count(x => x.Kind == LineDiffKind.Unchanged));
    }

    private static int Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return prev[b.Count];
    }
}
