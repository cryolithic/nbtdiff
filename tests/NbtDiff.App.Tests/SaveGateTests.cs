using fNbt;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class SaveGateTests
{
    [Fact]
    public async Task FirstSave_AsksForBackup_ThenNotAgain()
    {
        var dialogs = new FakeDialogService();
        var gate = new SaveGate(dialogs, _ => null);

        Assert.Equal(SaveCheck.Go, await gate.CheckAsync(["a.dat"]));
        Assert.True(gate.BackupConfirmed);
        Assert.Equal(SaveCheck.Go, await gate.CheckAsync(["a.dat"]));
        Assert.Equal(["confirm:Back up before saving"], dialogs.Requests);
    }

    [Fact]
    public async Task CancelledBackupPrompt_StopsTheSave_AndAsksAgainNextTime()
    {
        var dialogs = new FakeDialogService { NextConfirm = false };
        var gate = new SaveGate(dialogs, _ => null);

        Assert.Equal(SaveCheck.Cancelled, await gate.CheckAsync(["a.dat"]));
        Assert.False(gate.BackupConfirmed);
        dialogs.NextConfirm = true;
        Assert.Equal(SaveCheck.Go, await gate.CheckAsync(["a.dat"]));
        Assert.Equal(2, dialogs.Requests.Count);
    }

    [Fact]
    public async Task OpenWorld_IsRefused_WithoutAskingAboutBackups()
    {
        var dialogs = new FakeDialogService();
        var gate = new SaveGate(dialogs, p => p.StartsWith("/open/") ? "/open" : null);

        var check = await gate.CheckAsync(["/closed/level.dat", "/open/region/r.0.0.mca"]);
        Assert.False(check.Proceed);
        Assert.StartsWith("This world is open in Minecraft or a server: /open", check.Error);
        Assert.Empty(dialogs.Requests);
    }

    // ── FileCompareViewModel wiring ─────────────────────────────────────────────────────────

    private static async Task<(FileCompareViewModel vm, TempDir dir)> EditedPair(SaveGate gate)
    {
        var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var vm = new FileCompareViewModel(new FileDiffSource(d.File("a.dat"), d.File("b.dat")), new ImmediateUiDispatcher(), saveGate: gate);
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "k");
        await vm.CopyToRightCommand.ExecuteAsync(null);
        return (vm, d);
    }

    private static int ReadK(string path) => NbtDocument.Load(path).ValueOrThrow().Root.Get<NbtInt>("k")!.Value;

    [Fact]
    public async Task Save_GatePassesTheEditedSidesPath_AndWrites()
    {
        var seen = new List<string>();
        var gate = new SaveGate(new FakeDialogService(), p => { seen.Add(p); return null; });
        var (vm, d) = await EditedPair(gate);
        using (d)
        {
            await vm.SaveCommand.ExecuteAsync(null);
            await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([d.File("b.dat")], seen);   // only the edited (right) side
            Assert.False(vm.HasUnsavedEdits);
            Assert.Equal(1, ReadK(d.File("b.dat")));
        }
    }

    [Fact]
    public async Task Save_BackupPromptCancelled_WritesNothing_AndKeepsTheEdit()
    {
        var gate = new SaveGate(new FakeDialogService { NextConfirm = false }, _ => null);
        var (vm, d) = await EditedPair(gate);
        using (d)
        {
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.True(vm.HasUnsavedEdits);
            Assert.Null(vm.ErrorMessage);
            Assert.Equal(2, ReadK(d.File("b.dat")));
            Assert.False(File.Exists(d.File("b.dat.bak")));
        }
    }

    [Fact]
    public async Task Save_WorldOpen_ShowsTheRefusal_AndWritesNothing()
    {
        var gate = new SaveGate(new FakeDialogService(), _ => "/worlds/survival");
        var (vm, d) = await EditedPair(gate);
        using (d)
        {
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.True(vm.HasUnsavedEdits);
            Assert.StartsWith("This world is open in Minecraft or a server: /worlds/survival", vm.ErrorMessage);
            Assert.Equal(2, ReadK(d.File("b.dat")));
        }
    }
    [Fact]
    public async Task Shell_FilesItOpens_AreGatedByItsSessionGate()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var dialogs = new FakeDialogService();
        var shell = new MainWindowViewModel(dialogs, new ImmediateUiDispatcher());
        shell.OpenPair(d.File("a.dat"), d.File("b.dat"));
        var vm = Assert.IsType<FileCompareViewModel>(shell.Current);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "k");
        await vm.CopyToRightCommand.ExecuteAsync(null);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("confirm:Back up before saving", dialogs.Requests);
        Assert.True(shell.SaveGate.BackupConfirmed);
        Assert.Equal(1, ReadK(d.File("b.dat")));
    }

    [Fact]
    public async Task ChunkOpenedFromRegionView_GatesOnTheRegionFile()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        var world = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 4);
        world.Write(l.Path);
        world.Mutate(m => m.Chunk(0, 0, 1, 0).SetPath("InhabitedTime", 999L)).Write(r.Path);
        string Region(string dir) => Path.Combine(dir, "region", "r.0.0.mca");

        var seen = new List<string>();
        var gate = new SaveGate(new FakeDialogService(), p => { seen.Add(p); return null; });
        using var region = new RegionCompareViewModel(Region(l.Path), Region(r.Path), null, null, new ImmediateUiDispatcher(), saveGate: gate);
        await region.Load().WaitAsync(TimeSpan.FromSeconds(30));
        FileCompareViewModel? chunk = null;
        region.NavigationRequested += v => chunk = (FileCompareViewModel)v;
        region.SelectCommand.Execute(region.Grid[1, 0]);
        region.OpenSelectedCommand.Execute(null);
        await chunk!.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        chunk.SelectedItem = chunk.Tree.Rows.Single(i => i.Path == "InhabitedTime");
        await chunk.CopyToLeftCommand.ExecuteAsync(null);

        await chunk.SaveCommand.ExecuteAsync(null);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([Path.GetFullPath(Region(l.Path))], seen.Select(Path.GetFullPath));
        Assert.False(chunk.HasUnsavedEdits);
    }
}
