using NbtDiff.App.ViewModels;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel Shell() => new(new FakeDialogService(), new ImmediateUiDispatcher());

    [Fact]
    public void NoArgs_EmptyFolderView()
    {
        var shell = Shell();
        shell.Start([]);
        var vm = Assert.IsType<FolderCompareViewModel>(shell.Current);
        Assert.False(vm.IsScanning);
        Assert.False(shell.CanGoBack);
        Assert.Equal("nbt-diff — Folder compare", shell.WindowTitle);
    }

    [Fact]
    public async Task TwoDirectories_StartsScan()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        new WorldBuilder(1).WithLevelDat().Write(l.Path);
        new WorldBuilder(1).WithLevelDat().Write(r.Path);
        var shell = Shell();
        shell.Start([l.Path, r.Path]);
        var vm = Assert.IsType<FolderCompareViewModel>(shell.Current);
        Assert.NotNull(vm.ScanCompletion);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("1 same", vm.StatusText);
    }

    [Fact]
    public void TwoFiles_Placeholder()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.dat"), "x");
        File.WriteAllText(d.File("b.dat"), "y");
        var shell = Shell();
        shell.Start([d.File("a.dat"), d.File("b.dat")]);
        var vm = Assert.IsType<PlaceholderViewModel>(shell.Current);
        Assert.False(vm.IsError);
        Assert.Contains("S6", vm.Message);
    }

    [Fact]
    public void Mismatched_IsError()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.dat"), "x");
        var shell = Shell();
        shell.Start([d.File("a.dat"), d.Path]);
        var vm = Assert.IsType<PlaceholderViewModel>(shell.Current);
        Assert.True(vm.IsError);
        Assert.Contains("file", vm.Message);
        Assert.Contains("directory", vm.Message);
    }

    [Fact]
    public void WrongArgCount_IsError()
    {
        var shell = Shell();
        shell.Start(["one"]);
        Assert.True(Assert.IsType<PlaceholderViewModel>(shell.Current).IsError);
    }

    [Fact]
    public void Navigation_PushAndBack()
    {
        var shell = Shell();
        shell.Start([]);
        var folder = shell.Current;
        shell.Push(new PlaceholderViewModel("x", "y"));
        Assert.True(shell.CanGoBack);
        Assert.Equal("nbt-diff — x", shell.WindowTitle);
        shell.Back();
        Assert.Same(folder, shell.Current);
        Assert.False(shell.CanGoBack);
    }
}
