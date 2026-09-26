using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class FolderCompareViewModelTests
{
    private static WorldBuilder Base() => new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 12).WithRegion(1, 0, chunks: 3).WithLevelDat();

    private static (TempDir left, TempDir right) Pair(WorldBuilder l, WorldBuilder r)
    {
        var a = new TempDir(); var b = new TempDir();
        l.Write(a.Path); r.Write(b.Path);
        return (a, b);
    }

    private static async Task<(FolderCompareViewModel vm, ImmediateUiDispatcher ui, FakeDialogService dialogs)> Scanned(string left, string right, bool deepVerify = true)
    {
        var ui = new ImmediateUiDispatcher();
        var dialogs = new FakeDialogService();
        var vm = new FolderCompareViewModel(dialogs, ui) { LeftPath = left, RightPath = right, DeepVerify = deepVerify };
        vm.CompareCommand.Execute(null);
        Assert.True(vm.IsScanning);
        Assert.NotNull(vm.ScanCompletion);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsScanning);
        return (vm, ui, dialogs);
    }

    private static string Names(FolderCompareViewModel vm) => string.Join(" ", vm.Tree.Rows.Select(r => r.RelativePath));

    [Fact]
    public async Task Mutate_OneDifferentRegion_CountsInStatusBar()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 0).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            var (vm, ui, _) = await Scanned(l.Path, r.Path);

            Assert.StartsWith("1 differ · 2 same", vm.StatusText);
            Assert.Equal(1.0, vm.ProgressFraction);
            Assert.Equal(0, ui.ActiveTimers);

            // Root children expanded one level: level.dat, region/, and region's files.
            Assert.Equal("region region/r.0.0.mca region/r.1.0.mca level.dat", Names(vm));   // directories sort first (S3)
            var region = vm.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca");
            Assert.Equal(RowStatus.Different, region.Status);
            Assert.Equal("≠", region.StatusGlyph);
            Assert.Equal(RowStatus.Different, vm.Tree.Rows.Single(i => i.RelativePath == "region").Status);
            Assert.Contains("1 differ", vm.Tree.Rows.Single(i => i.RelativePath == "region").CountsText);
        }
    }

    [Fact]
    public async Task FilterChips_HideAndShowRows()
    {
        var (l, r) = Pair(
            Base().WithFile("data/only-left.dat", NbtFixtures.SampleCompound(1)),
            Base().Mutate(m => m.Chunk(0, 0, 3, 0).SetPath("InhabitedTime", 999L)).WithFile("data/only-right.dat", NbtFixtures.SampleCompound(2)));
        using (l) using (r)
        {
            var (vm, _, _) = await Scanned(l.Path, r.Path);
            vm.Tree.ExpandAll();
            Assert.Equal("data data/only-left.dat data/only-right.dat region region/r.0.0.mca region/r.1.0.mca level.dat", Names(vm));

            vm.Filter = RowFilter.Differences;
            Assert.Equal("data data/only-left.dat data/only-right.dat region region/r.0.0.mca", Names(vm));

            vm.Filter = RowFilter.Orphans;
            Assert.Equal("data data/only-left.dat data/only-right.dat", Names(vm));
            Assert.Equal(RowStatus.LeftOnly, vm.Tree.Rows.Single(i => i.Name == "only-left.dat").Status);
            Assert.False(vm.Tree.Rows.Single(i => i.Name == "only-left.dat").HasRight);

            vm.Filter = RowFilter.Same;
            Assert.Equal("region region/r.1.0.mca level.dat", Names(vm));

            vm.Filter = RowFilter.All;
            Assert.Equal("data data/only-left.dat data/only-right.dat region region/r.0.0.mca region/r.1.0.mca level.dat", Names(vm));
        }
    }

    [Fact]
    public async Task Filter_ErrorsAreNotDifferences()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 0).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            // Corrupt one header slot on the right so region/r.1.0.mca becomes an Error row.
            var path = r.File(Path.Combine("region", "r.1.0.mca"));
            var bytes = File.ReadAllBytes(path);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (5000u << 8) | 1);
            File.WriteAllBytes(path, bytes);

            var (vm, _, _) = await Scanned(l.Path, r.Path);
            vm.Tree.ExpandAll();
            Assert.Equal(RowStatus.Error, vm.Tree.Rows.Single(i => i.Name == "r.1.0.mca").Status);

            vm.Filter = RowFilter.Differences;
            Assert.Equal("region region/r.0.0.mca", Names(vm));

            vm.Filter = RowFilter.Errors;
            Assert.Equal("region region/r.1.0.mca", Names(vm));

            vm.Filter = RowFilter.All;
            Assert.Contains("region/r.1.0.mca", Names(vm));
        }
    }

    [Fact]
    public async Task Recompress_NoDifferencesAfterDeepVerify()
    {
        var (l, r) = Pair(Base(), Base().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r)
        {
            var (vm, _, _) = await Scanned(l.Path, r.Path);
            Assert.Equal("3 same", vm.StatusText);
            vm.Filter = RowFilter.Differences;
            Assert.Empty(vm.Tree.Rows);
        }
    }

    [Fact]
    public async Task Recompress_WithoutDeepVerify_ShowsProbablyDifferent()
    {
        // Unverified byte differences are reported as such, never as verified differences.
        var (l, r) = Pair(Base(), Base().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r)
        {
            var (vm, _, _) = await Scanned(l.Path, r.Path, deepVerify: false);
            Assert.StartsWith("2 probably differ · 1 same", vm.StatusText);
            Assert.All(vm.Tree.Rows.Where(i => !i.IsDirectory && i.Name.EndsWith(".mca")), i => Assert.Equal(RowStatus.ProbablyDifferent, i.Status));
        }
    }

    [Fact]
    public async Task StatusBar_TracksRowChangedThroughCoalescer()
    {
        var (l, r) = Pair(Base(), Base());
        using (l) using (r)
        {
            var ui = new ImmediateUiDispatcher();
            var vm = new FolderCompareViewModel(new FakeDialogService(), ui) { LeftPath = l.Path, RightPath = r.Path };
            var texts = new List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StatusText)) texts.Add(vm.StatusText); };
            vm.CompareCommand.Execute(null);
            Assert.Contains("pending", texts[0]);
            Assert.Contains("pass 1 of 2", texts[0]);
            await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("3 same", vm.StatusText);
            Assert.All(vm.Tree.Rows, i => Assert.Equal(RowStatus.Same, i.Status));
        }
    }

    [Fact]
    public async Task FinalState_IsFlushed_EvenIfTimerNeverTicked()
    {
        // ImmediateUiDispatcher never ticks the timer on its own: only the completion flush runs.
        var (l, r) = Pair(Base(), Base().Mutate(m => m.RemoveChunk(0, 0, 0, 0)));
        using (l) using (r)
        {
            var (vm, ui, _) = await Scanned(l.Path, r.Path);
            Assert.Equal(RowStatus.Different, vm.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca").Status);
            Assert.Equal(0, ui.ActiveTimers);
        }
    }

    [Fact]
    public void MissingFolder_IsAnErrorNotAScan()
    {
        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher())
        {
            LeftPath = Path.Combine(Path.GetTempPath(), "nbtdiff-nope-left"),
            RightPath = Path.GetTempPath(),
        };
        vm.CompareCommand.Execute(null);
        Assert.False(vm.IsScanning);
        Assert.True(vm.HasError);
        Assert.Contains("Left path does not exist", vm.ErrorMessage);
        Assert.Null(vm.ScanCompletion);
    }

    [Fact]
    public async Task Browse_SetsPath_WhenNotCancelled()
    {
        var dialogs = new FakeDialogService { NextFolder = @"C:\worlds\x" };
        var vm = new FolderCompareViewModel(dialogs, new ImmediateUiDispatcher());
        await vm.BrowseLeftCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\worlds\x", vm.LeftPath);
        dialogs.NextFolder = null;
        await vm.BrowseRightCommand.ExecuteAsync(null);
        Assert.Equal("", vm.RightPath);
        Assert.Equal(["folder:Left folder", "folder:Right folder"], dialogs.Requests);
    }

    [Fact]
    public async Task Export_WritesReport()
    {
        var (l, r) = Pair(Base(), Base().Mutate(m => m.Chunk(0, 0, 3, 0).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        using (var outDir = new TempDir())
        {
            var (vm, _, dialogs) = await Scanned(l.Path, r.Path);
            dialogs.NextSaveFile = outDir.File("report.json");
            await vm.ExportCommand.ExecuteAsync("json");
            var json = File.ReadAllText(outDir.File("report.json"));
            Assert.Contains("\"region/r.0.0.mca\"", json);
            Assert.Contains("Different", json);
            Assert.Contains("report.json", vm.StatusText);

            dialogs.NextSaveFile = outDir.File("report.txt");
            await vm.ExportCommand.ExecuteAsync("text");
            Assert.Contains("region/r.0.0.mca", File.ReadAllText(outDir.File("report.txt")));
        }
    }

    [Fact]
    public async Task OpenSelected_TogglesFolders_RequestsNavigationForFiles()
    {
        var (l, r) = Pair(Base(), Base());
        using (l) using (r)
        {
            var (vm, _, _) = await Scanned(l.Path, r.Path);
            CompareRow? requested = null;
            vm.NavigationRequested += row => requested = row;

            vm.SelectedRow = vm.Tree.Rows.Single(i => i.RelativePath == "region");
            vm.OpenSelectedCommand.Execute(null);
            Assert.Equal("region level.dat", Names(vm));
            Assert.Null(requested);

            vm.SelectedRow = vm.Tree.Rows.Single(i => i.RelativePath == "level.dat");
            vm.OpenSelectedCommand.Execute(null);
            Assert.Equal("level.dat", requested?.RelativePath);
        }
    }

    [Fact]
    public async Task Cancel_LeavesPendingRows_AndFinishes()
    {
        var (l, r) = Pair(Base().WithRegion(2, 0, 200), Base().WithRegion(2, 0, 200));
        using (l) using (r)
        {
            var ui = new ImmediateUiDispatcher();
            var gate = new TaskCompletionSource();
            var vm = new FolderCompareViewModel(new FakeDialogService(), ui, new BlockingFingerprinter(gate.Task)) { LeftPath = l.Path, RightPath = r.Path };
            vm.CompareCommand.Execute(null);
            Assert.True(vm.IsScanning);
            vm.CancelCommand.Execute(null);
            gate.SetResult();
            await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(vm.IsScanning);
            Assert.Contains("cancelled", vm.StatusText);
            Assert.True(vm.Current!.IsCancelled);
        }
    }

    private sealed class BlockingFingerprinter(Task gate) : IFingerprinter
    {
        private readonly Fingerprinter _inner = new();

        public async ValueTask<LoadResult<FileFingerprint>> QuickAsync(string path, FileKind kind, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return await _inner.QuickAsync(path, kind, ct);
        }

        public ValueTask<LoadResult<FileFingerprint>> DeepAsync(string path, FileKind kind, CancellationToken ct = default) => _inner.DeepAsync(path, kind, ct);
    }
}
