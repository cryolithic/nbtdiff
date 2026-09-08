using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Core.Diff;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class TextCompareViewModelTests
{
    private static async Task<TextCompareViewModel> Loaded(string? left, string? right)
    {
        var vm = new TextCompareViewModel(left, right, new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        return vm;
    }

    [Fact]
    public async Task Load_DiffsAndSummarizes()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.json"), "{\n  \"a\": 1,\n  \"b\": 2\n}\n");
        File.WriteAllText(d.File("b.json"), "{\r\n  \"a\": 1,\r\n  \"b\": 3,\r\n  \"c\": 4\r\n}\r\n");
        var vm = await Loaded(d.File("a.json"), d.File("b.json"));

        Assert.True(vm.HasResult);
        Assert.False(vm.HasError);
        Assert.Equal("a.json ↔ b.json", vm.Title);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal([LineDiffKind.Unchanged, LineDiffKind.Unchanged, LineDiffKind.Changed, LineDiffKind.Added, LineDiffKind.Unchanged], vm.Rows.Select(r => r.Kind));
        Assert.Equal("1 hunk · 1 changed · 1 right-only", vm.SummaryText);
        Assert.True(vm.Rows[2].IsHunkStart);
        Assert.False(vm.Rows[3].IsHunkStart);
        Assert.Equal("≠", vm.Rows[2].StatusGlyph);
        Assert.Equal("+", vm.Rows[3].StatusGlyph);
        Assert.Equal("", vm.Rows[3].LeftLineText);
        Assert.Equal("4", vm.Rows[3].RightLineText);
    }

    [Fact]
    public async Task Identical_NoHunks()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.txt"), "x\ny\n");
        File.WriteAllText(d.File("b.txt"), "x\r\ny\r\n");
        var vm = await Loaded(d.File("a.txt"), d.File("b.txt"));
        Assert.Equal("No differences · 2 lines", vm.SummaryText);
        vm.NextHunkCommand.Execute(null);
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public async Task HunkNavigation_WrapsBothWays()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.txt"), "a\nb\nc\nd\ne\nf\n");
        File.WriteAllText(d.File("b.txt"), "A\nb\nc\nD\ne\nF\n");
        var vm = await Loaded(d.File("a.txt"), d.File("b.txt"));
        Assert.Equal([0, 3, 5], vm.Result!.HunkStarts);

        vm.NextHunkCommand.Execute(null);
        Assert.Equal(0, vm.SelectedRow!.Index);
        vm.NextHunkCommand.Execute(null);
        Assert.Equal(3, vm.SelectedRow!.Index);
        vm.NextHunkCommand.Execute(null);
        Assert.Equal(5, vm.SelectedRow!.Index);
        vm.NextHunkCommand.Execute(null);
        Assert.Equal(0, vm.SelectedRow!.Index);          // wrap

        vm.PreviousHunkCommand.Execute(null);
        Assert.Equal(5, vm.SelectedRow!.Index);          // wrap backwards
        vm.SelectedRow = vm.Rows[4];                     // unchanged row between hunks
        vm.PreviousHunkCommand.Execute(null);
        Assert.Equal(3, vm.SelectedRow!.Index);
        vm.SelectedRow = vm.Rows[4];
        vm.NextHunkCommand.Execute(null);
        Assert.Equal(5, vm.SelectedRow!.Index);
    }

    [Fact]
    public async Task MissingSide_AllAdded()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("b.txt"), "x\ny\n");
        var vm = await Loaded(d.File("nope.txt"), d.File("b.txt"));
        Assert.Equal("(missing) ↔ b.txt", vm.Title);
        Assert.All(vm.Rows, r => Assert.Equal(LineDiffKind.Added, r.Kind));
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task NeitherExists_Error()
    {
        var vm = await Loaded(null, null);
        Assert.True(vm.HasError);
        Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task Oversized_Error()
    {
        using var d = new TempDir();
        using (var f = File.Create(d.File("big.txt")))
            f.SetLength(TextCompareViewModel.MaxBytes + 1);
        File.WriteAllText(d.File("small.txt"), "x");
        var vm = await Loaded(d.File("big.txt"), d.File("small.txt"));
        Assert.True(vm.HasError);
        Assert.Contains("big.txt", vm.ErrorMessage);
    }

    [Fact]
    public async Task Shell_RoutesTextAndJsonToTextView_AndBinaryToPlaceholder()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.json"), "{}");
        File.WriteAllText(d.File("b.json"), "{ }");
        File.WriteAllBytes(d.File("a.png"), [1, 2, 3]);
        File.WriteAllBytes(d.File("b.png"), [1, 2, 3, 4]);
        var shell = new MainWindowViewModel(new FakeDialogService(), new ImmediateUiDispatcher());

        shell.Start([d.File("a.json"), d.File("b.json")]);
        var text = Assert.IsType<TextCompareViewModel>(shell.Current);
        await text.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(text.Result!.HasChanges);

        var placeholder = Assert.IsType<PlaceholderViewModel>(shell.CreateCompareView(FileKind.Binary, d.File("a.png"), d.File("b.png")));
        Assert.Contains("3 bytes", placeholder.Message);
        Assert.Contains("4 bytes", placeholder.Message);
        Assert.Contains("hash not computed", placeholder.Message);

        var fp = new FileFingerprint(FileKind.Binary, FingerprintTier.Quick, 3, 0xABCDEF0123456789UL);
        var withHash = Assert.IsType<PlaceholderViewModel>(shell.CreateCompareView(FileKind.Binary, d.File("a.png"), null, fp, null));
        Assert.Contains("xxh64 abcdef0123456789", withHash.Message);
        Assert.Contains("(missing)", withHash.Message);
    }
}
