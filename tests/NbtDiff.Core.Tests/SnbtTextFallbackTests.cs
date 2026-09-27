using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

/// <summary>FTB leaves comment-only .snbt files in every world ("# File has moved!"); they compare as text, not as errors.</summary>
public class SnbtTextFallbackTests
{
    private const string Moved = "# File has moved!\n# FTB Essentials configuration is now in <instance-folder>/config/ftbessentials.snbt\n";

    private static async Task<CompareRow> ScanOne(string leftText, string rightText)
    {
        using var l = new TempDir();
        using var r = new TempDir();
        File.WriteAllText(l.File("ftbessentials.snbt"), leftText);
        File.WriteAllText(r.File("ftbessentials.snbt"), rightText);
        var root = new DirectoryComparer(new CompareOptions(DeepVerify: true)).Prepare(l.Path, r.Path);
        root.Run();
        await root.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return root.Root.Children.Single();
    }

    [Fact]
    public async Task CommentOnly_Identical_IsSame() =>
        Assert.Equal(RowStatus.Same, (await ScanOne(Moved, Moved)).Status);

    [Fact]
    public async Task CommentOnly_LineEndingsOnly_IsSame() =>
        Assert.Equal(RowStatus.Same, (await ScanOne(Moved, Moved.Replace("\n", "\r\n"))).Status);

    [Fact]
    public async Task CommentOnly_DifferentText_IsDifferent_NotAnError()
    {
        var row = await ScanOne(Moved, Moved + "# another line\n");
        Assert.Equal(RowStatus.Different, row.Status);
        Assert.Null(row.Error);
    }
}
