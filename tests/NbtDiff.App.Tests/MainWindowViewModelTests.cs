using fNbt;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Nbt;
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
    public async Task TwoNbtFiles_FileCompare()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var shell = Shell();
        shell.Start([d.File("a.dat"), d.File("b.dat")]);
        var vm = Assert.IsType<FileCompareViewModel>(shell.Current);
        Assert.Equal("nbt-diff — a.dat ↔ b.dat", shell.WindowTitle);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["k"], vm.ChangedNodes.Select(n => n.Path));
    }

    [Fact]
    public async Task TwoRegionFiles_RegionCompare()
    {
        using var d = new TempDir();
        new WorldBuilder(3).WithRegion(0, 0, chunks: 4).Write(d.Path);
        var region = Path.Combine(d.Path, "region", "r.0.0.mca");
        var shell = Shell();
        shell.Start([region, region]);
        var vm = Assert.IsType<RegionCompareViewModel>(shell.Current);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(4, vm.Grid.Same);
    }

    [Fact]
    public async Task OneFileMissing_StillOpensCompare()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        var shell = Shell();
        shell.Start([d.File("a.dat"), d.File("missing.dat")]);
        var vm = Assert.IsType<FileCompareViewModel>(shell.Current);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DiffKind.Removed, vm.Root!.Kind);
    }

    [Fact]
    public async Task TwoTextFiles_TextCompare()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.json"), "{}");
        File.WriteAllText(d.File("b.json"), "{}");
        var shell = Shell();
        shell.Start([d.File("a.json"), d.File("b.json")]);
        var vm = Assert.IsType<TextCompareViewModel>(shell.Current);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.HasError);
        Assert.False(vm.Result!.HasChanges);
    }

    [Fact]
    public void FilesOfDifferentKinds_IsError()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.dat"), "x");
        File.WriteAllText(d.File("b.mca"), "y");
        var shell = Shell();
        shell.Start([d.File("a.dat"), d.File("b.mca")]);
        var vm = Assert.IsType<PlaceholderViewModel>(shell.Current);
        Assert.True(vm.IsError);
        Assert.Contains("different kinds", vm.Message);
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
    public void BothMissing_IsError()
    {
        var shell = Shell();
        shell.Start([Path.Combine(Path.GetTempPath(), "nbtdiff-nope-a.dat"), Path.Combine(Path.GetTempPath(), "nbtdiff-nope-b.dat")]);
        Assert.True(Assert.IsType<PlaceholderViewModel>(shell.Current).IsError);
    }

    [Fact]
    public void WrongArgCount_IsError()
    {
        var shell = Shell();
        shell.Start(["one"]);
        Assert.True(Assert.IsType<PlaceholderViewModel>(shell.Current).IsError);
    }

    [Fact]
    public void CreateCompareView_ByKind()
    {
        var shell = Shell();
        Assert.IsType<RegionCompareViewModel>(shell.CreateCompareView(FileKind.Region, null, null));
        Assert.IsType<FileCompareViewModel>(shell.CreateCompareView(FileKind.Nbt, null, null));
        Assert.IsType<FileCompareViewModel>(shell.CreateCompareView(FileKind.Snbt, null, null));
        Assert.IsType<TextCompareViewModel>(shell.CreateCompareView(FileKind.Json, null, null));
        Assert.IsType<TextCompareViewModel>(shell.CreateCompareView(FileKind.Text, null, null));
        Assert.IsType<PlaceholderViewModel>(shell.CreateCompareView(FileKind.Binary, null, null));
    }

    [Fact]
    public async Task FolderRow_NavigatesByKind_AndBackKeepsScan()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        new WorldBuilder(1).WithRegion(0, 0, chunks: 2).WithLevelDat().Write(l.Path);
        new WorldBuilder(1).WithRegion(0, 0, chunks: 2).WithLevelDat().Mutate(m => m.File("level.dat").SetPath("Data/Time", 5L)).Write(r.Path);
        var shell = Shell();
        shell.Start([l.Path, r.Path]);
        var folder = Assert.IsType<FolderCompareViewModel>(shell.Current);
        await folder.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));

        folder.SelectedRow = folder.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca");
        folder.OpenSelectedCommand.Execute(null);
        var region = Assert.IsType<RegionCompareViewModel>(shell.Current);
        await region.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, region.Grid.Same);

        shell.Back();
        Assert.Same(folder, shell.Current);
        Assert.True(region.IsDisposed);
        Assert.Equal("1 differ · 1 same", folder.StatusText);   // scan state intact

        folder.SelectedRow = folder.Tree.Rows.Single(i => i.RelativePath == "level.dat");
        folder.OpenSelectedCommand.Execute(null);
        var file = Assert.IsType<FileCompareViewModel>(shell.Current);
        await file.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["Data/Time"], file.ChangedNodes.Select(n => n.Path));
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
