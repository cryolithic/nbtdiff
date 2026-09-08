using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class SettingsTests
{
    [Fact]
    public void MissingFile_Defaults()
    {
        using var d = new TempDir();
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.True(s.DeepVerify);
        Assert.False(s.CompoundOrderMatters);
        Assert.Equal(["session.lock"], s.ExcludeGlobs);
        Assert.Empty(s.RecentPairs);
        Assert.Null(s.Window);
    }

    [Fact]
    public void RoundTrip()
    {
        using var d = new TempDir();
        var store = new SettingsStore(d.File(Path.Combine("nested", "dir", "settings.json")));
        var s = new AppSettings { DeepVerify = false, CompoundOrderMatters = true, UseKeyedAligner = true, ExcludeGlobs = ["session.lock", "*.log"] };
        s.AddRecent(@"C:\a", @"C:\b");
        s.Window = new WindowPlacement(10, 20, 800, 600, false);

        Assert.True(store.TrySave(s));
        var back = store.Load();

        Assert.False(back.DeepVerify);
        Assert.True(back.CompoundOrderMatters);
        Assert.True(back.UseKeyedAligner);
        Assert.Equal(["session.lock", "*.log"], back.ExcludeGlobs);
        Assert.Equal([new RecentPair(@"C:\a", @"C:\b")], back.RecentPairs);
        Assert.Equal(new WindowPlacement(10, 20, 800, 600, false), back.Window);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void CorruptFile_Defaults()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("settings.json"), "{ this is not json");
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.True(s.DeepVerify);
        Assert.Empty(s.RecentPairs);
    }

    [Fact]
    public void PartialFile_KeepsOtherDefaults_AndSanitizes()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("settings.json"), """{ "DeepVerify": false, "RecentPairs": [ { "Left": "", "Right": "x" } ], "Window": { "X": 0, "Y": 0, "Width": 10, "Height": 10, "IsMaximized": false } }""");
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.False(s.DeepVerify);
        Assert.Equal(["session.lock"], s.ExcludeGlobs);
        Assert.Empty(s.RecentPairs);          // blank path dropped
        Assert.Null(s.Window);                // too small to be real
    }

    [Fact]
    public void AddRecent_DedupesAndCaps()
    {
        var s = new AppSettings();
        for (int i = 0; i < 15; i++) s.AddRecent($"l{i}", $"r{i}");
        Assert.Equal(AppSettings.MaxRecent, s.RecentPairs.Count);
        Assert.Equal("l14", s.RecentPairs[0].Left);

        s.AddRecent("l10", "r10");
        Assert.Equal(AppSettings.MaxRecent, s.RecentPairs.Count);
        Assert.Equal("l10", s.RecentPairs[0].Left);
        Assert.Single(s.RecentPairs, p => p.Left == "l10");
    }

    [Fact]
    public void ToCompareOptions_CarriesEverything()
    {
        var s = new AppSettings { DeepVerify = false, CompoundOrderMatters = true, ExcludeGlobs = ["a", "b"] };
        var o = s.ToCompareOptions();
        Assert.False(o.DeepVerify);
        Assert.True(o.CompoundOrderMatters);
        Assert.Equal(["a", "b"], o.ExcludeGlobs);
    }

    [Fact]
    public async Task FolderCompare_RecordsRecentPair_AndPersistsDeepVerify()
    {
        using var d = new TempDir();
        using var l = new TempDir();
        using var r = new TempDir();
        new WorldBuilder(1).WithLevelDat().Write(l.Path);
        new WorldBuilder(1).WithLevelDat().Write(r.Path);
        var store = new SettingsStore(d.File("settings.json"));
        var settings = new SettingsService(store);

        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher(), settings: settings);
        Assert.True(vm.DeepVerify);
        Assert.False(vm.HasRecent);

        vm.DeepVerify = false;
        Assert.False(store.Load().DeepVerify);

        vm.LeftPath = l.Path;
        vm.RightPath = r.Path;
        vm.CompareCommand.Execute(null);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([new RecentPair(l.Path, r.Path)], store.Load().RecentPairs);
        Assert.Equal([new RecentPair(l.Path, r.Path)], vm.RecentPairs);
        Assert.True(vm.HasRecent);

        // Picking a recent pair re-runs the compare and clears the picker.
        vm.LeftPath = "";
        vm.SelectedRecent = vm.RecentPairs[0];
        Assert.Null(vm.SelectedRecent);
        Assert.Equal(l.Path, vm.LeftPath);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(store.Load().RecentPairs);
    }

    [Fact]
    public async Task FileCompare_SeedsAndPersistsToggles()
    {
        using var d = new TempDir();
        var store = new SettingsStore(d.File("settings.json"));
        store.TrySave(new AppSettings { CompoundOrderMatters = true });
        var settings = new SettingsService(store);

        var vm = new FileCompareViewModel(new TagPairSource("t", new fNbt.NbtCompound(""), new fNbt.NbtCompound("")), new ImmediateUiDispatcher(), settings);
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.CompoundOrderMatters);
        Assert.False(vm.UseKeyedAligner);

        vm.UseKeyedAligner = true;
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        var back = store.Load();
        Assert.True(back.UseKeyedAligner);
        Assert.True(back.CompoundOrderMatters);
    }

    [Fact]
    public void Shell_ExposesSettings_DefaultInMemory()
    {
        var shell = new MainWindowViewModel(new FakeDialogService(), new ImmediateUiDispatcher());
        Assert.NotNull(shell.Settings.Current);
        shell.Settings.Save(); // no store: must not throw
    }
}
