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

        await shell.BackCommand.ExecuteAsync(null);
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
    public async Task Navigation_PushAndBack()
    {
        var shell = Shell();
        shell.Start([]);
        var folder = shell.Current;
        shell.Push(new PlaceholderViewModel("x", "y"));
        Assert.True(shell.CanGoBack);
        Assert.Equal("nbt-diff — x", shell.WindowTitle);
        await shell.BackCommand.ExecuteAsync(null);
        Assert.Same(folder, shell.Current);
        Assert.False(shell.CanGoBack);
    }

    [Fact]
    public async Task Back_WithUnsavedEdits_ConfirmBeforeDiscarding()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var dialogs = new FakeDialogService();
        var shell = new MainWindowViewModel(dialogs, new ImmediateUiDispatcher());
        shell.Start([]);
        var folder = shell.Current;
        shell.OpenPair(d.File("a.dat"), d.File("b.dat"));
        var file = Assert.IsType<FileCompareViewModel>(shell.Current);
        await file.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        file.SelectedItem = file.Tree.Rows.Single(i => i.Path == "k");
        await file.CopyToRightCommand.ExecuteAsync(null);
        Assert.True(file.HasUnsavedEdits);
        Assert.True(shell.HasDiscardableEdits);

        dialogs.NextConfirm = false;
        await shell.BackCommand.ExecuteAsync(null);
        Assert.Same(file, shell.Current);   // the discard was refused; nothing popped

        dialogs.NextConfirm = true;
        await shell.BackCommand.ExecuteAsync(null);
        Assert.Same(folder, shell.Current);
    }

    [Fact]
    public async Task FolderView_TwoFilePaths_OpensFileCompare()
    {
        // The startup view is a folder compare, but typing two file paths there must compare the files.
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var shell = Shell();
        shell.Start([]);
        var folder = Assert.IsType<FolderCompareViewModel>(shell.Current);

        folder.LeftPath = d.File("a.dat");
        folder.RightPath = d.File("b.dat");
        folder.CompareCommand.Execute(null);

        var vm = Assert.IsType<FileCompareViewModel>(shell.Current);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["k"], vm.ChangedNodes.Select(n => n.Path));
    }

    [Fact]
    public void OpenPair_MixedFolderAndFile_ReportsError()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        var shell = Shell();
        shell.Start([]);
        var folder = Assert.IsType<FolderCompareViewModel>(shell.Current);

        folder.LeftPath = d.Path;
        folder.RightPath = d.File("a.dat");
        folder.CompareCommand.Execute(null);

        Assert.Contains("two folders, or two files", folder.ErrorMessage);
        Assert.Same(folder, shell.Current); // nothing was pushed over the folder view
    }
    [Fact]
    public void CommentOnlySnbt_OpensInTheTextView()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.snbt"), "# File has moved!\n");
        File.WriteAllText(d.File("b.snbt"), "{ ok: 1b }\n");
        var shell = new MainWindowViewModel(new FakeDialogService(), new ImmediateUiDispatcher());
        Assert.IsType<TextCompareViewModel>(shell.CreateCompareView(FileKind.Snbt, d.File("a.snbt"), d.File("b.snbt")));
        Assert.IsType<FileCompareViewModel>(shell.CreateCompareView(FileKind.Snbt, d.File("b.snbt"), d.File("b.snbt")));
        Assert.IsType<FileCompareViewModel>(shell.CreateCompareView(FileKind.Snbt, null, d.File("b.snbt")));
    }
    [Fact]
    public async Task Crumbs_WorldFolderFileAndChunk_JumpBackToAnyLevel()
    {
        var world = new WorldBuilder(seed: 42).WithRegion(-1, 1, chunks: 3).WithLevelDat();
        using var l = new TempDir();
        using var r = new TempDir();
        world.Write(l.Path);
        world.Mutate(m => m.Chunk(-1, 1, 1, 0).SetPath("InhabitedTime", 999L)).Write(r.Path);
        var dialogs = new FakeDialogService();
        var shell = new MainWindowViewModel(dialogs, new ImmediateUiDispatcher());
        shell.Start([l.Path, r.Path]);
        var folder = Assert.IsType<FolderCompareViewModel>(shell.Current);
        await folder.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        string worldName = Path.GetFileName(l.Path);
        Assert.Equal([worldName], shell.Crumbs.Select(c => c.Label));

        folder.SelectedRow = folder.Tree.Rows.Single(i => i.RelativePath == "region/r.-1.1.mca");
        folder.OpenSelectedCommand.Execute(null);
        var region = Assert.IsType<RegionCompareViewModel>(shell.Current);
        await region.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([worldName, "region", "r.-1.1.mca"], shell.Crumbs.Select(c => c.Label));
        Assert.Equal([0, 1, 1], shell.Crumbs.Select(c => c.Level));
        Assert.Equal("chunks x −32…−1, z 32…63", region.CrumbNote);

        region.OpenSelectedCommand.Execute(null);
        var chunk = Assert.IsType<FileCompareViewModel>(shell.Current);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([worldName, "region", "r.-1.1.mca", "chunk (1, 0)"], shell.Crumbs.Select(c => c.Label));
        Assert.True(shell.Crumbs[^1].IsCurrent);
        Assert.Equal(1, shell.Crumbs.Count(c => c.IsCurrent));

        // Unsaved copies block the jump until confirmed; then two levels pop at once.
        chunk.SelectedItem = chunk.Root;
        await chunk.CopyToLeftCommand.ExecuteAsync(null);
        dialogs.NextConfirm = false;
        await shell.NavigateToCommand.ExecuteAsync(0);
        Assert.Same(chunk, shell.Current);
        dialogs.NextConfirm = true;
        await shell.NavigateToCommand.ExecuteAsync(0);
        Assert.Same(folder, shell.Current);
        Assert.Single(shell.Stack);
        Assert.True(region.IsDisposed);
    }
}
