using fNbt;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class RegionCompareViewModelTests
{
    private static WorldBuilder Base() => new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40);
    private static string Region(string dir) => Path.Combine(dir, "region", "r.0.0.mca");

    private static async Task<RegionCompareViewModel> Loaded(string? left, string? right, FileFingerprint? lfp = null, FileFingerprint? rfp = null)
    {
        var vm = new RegionCompareViewModel(left, right, lfp, rfp, new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(vm.IsBusy);
        return vm;
    }

    [Fact]
    public async Task MutatedChunk_IsTheOnlyDifferentCell()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(r.Path);

        using var vm = await Loaded(Region(l.Path), Region(r.Path));
        Assert.False(vm.HasError);
        Assert.Equal("r.0.0.mca", vm.Title);
        Assert.Equal(40, vm.Grid.Present);
        Assert.Equal(1, vm.Grid.Different);
        Assert.Equal(39, vm.Grid.Same);
        Assert.Equal(ChunkDiffStatus.Different, vm.Grid[3, 1].Status);
        Assert.Same(vm.Grid[3, 1], vm.Grid.Selected);
        Assert.Equal("40 chunks · 1 different · 39 same", vm.HeaderText);
    }

    [Fact]
    public async Task WithScanFingerprints_SameResult()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Recompress(ChunkRef.SchemeGZip).Write(r.Path);
        var fp = new Fingerprinter();
        var lfp = (await fp.QuickAsync(Region(l.Path), FileKind.Region)).ValueOrThrow();
        var rfp = (await fp.QuickAsync(Region(r.Path), FileKind.Region)).ValueOrThrow();

        using var vm = await Loaded(Region(l.Path), Region(r.Path), lfp, rfp);
        Assert.Equal(1, vm.Grid.Different);
        Assert.Equal(39, vm.Grid.Same);   // quick hashes all differ (recompressed) but content compare resolves them
    }

    [Fact]
    public async Task OpenSelected_PushesChunkDiff_WithInhabitedTimeOnly()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(r.Path);
        using var vm = await Loaded(Region(l.Path), Region(r.Path));

        ViewModelBase? pushed = null;
        vm.NavigationRequested += v => pushed = v;
        vm.OpenSelectedCommand.Execute(null);

        var chunk = Assert.IsType<FileCompareViewModel>(pushed);
        Assert.Equal("r.0.0.mca (3, 1)", chunk.Title);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(chunk.HasError);
        var change = Assert.Single(chunk.ChangedNodes);
        Assert.Equal("InhabitedTime", change.Path);
        Assert.Equal(DiffKind.ValueChanged, change.Kind);
        Assert.Equal("999L", change.RightValueText);
        // Siblings are collapsed/hidden: only root + the change are rows.
        Assert.Equal(2, chunk.Tree.Rows.Count);
        Assert.All(chunk.Root!.Children.Where(c => c.Path != "InhabitedTime"), c => Assert.Equal(0, c.ChangedDescendants));
    }

    [Fact]
    public async Task OpenSelected_OnEmptySlot_DoesNothing()
    {
        using var l = new TempDir();
        Base().Write(l.Path);
        using var vm = await Loaded(Region(l.Path), Region(l.Path));
        int pushes = 0;
        vm.NavigationRequested += _ => pushes++;
        vm.Grid.Select(20, 20);
        vm.OpenSelectedCommand.Execute(null);
        Assert.Equal(0, pushes);
    }

    [Fact]
    public async Task MissingRightSide_AllLeftOnly()
    {
        using var l = new TempDir();
        Base().Write(l.Path);
        using var vm = await Loaded(Region(l.Path), Path.Combine(l.Path, "nope", "r.0.0.mca"));
        Assert.False(vm.HasError);
        Assert.Equal("r.0.0.mca ↔ (missing)", vm.Title);
        Assert.Equal(40, vm.Grid.LeftOnly);
        Assert.Equal(0, vm.Grid.Same);
        Assert.Equal("(missing)", vm.RightPathText);
    }

    [Fact]
    public async Task AddedAndRemovedChunks()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.RemoveChunk(0, 0, 0, 0).AddChunk(0, 0, 20, 20)).Write(r.Path);
        using var vm = await Loaded(Region(l.Path), Region(r.Path));
        Assert.Equal(ChunkDiffStatus.LeftOnly, vm.Grid[0, 0].Status);
        Assert.Equal(ChunkDiffStatus.RightOnly, vm.Grid[20, 20].Status);
        Assert.Equal(41, vm.Grid.Present);
    }

    [Fact]
    public async Task CorruptFile_IsAnError()
    {
        using var d = new TempDir();
        Base().Write(d.Path);
        File.WriteAllBytes(d.File("bad.mca"), new byte[100]);
        using var vm = await Loaded(Region(d.Path), d.File("bad.mca"));
        Assert.True(vm.HasError);
        Assert.StartsWith("Right:", vm.ErrorMessage);
        Assert.Equal(0, vm.Grid.Present);
    }

    [Fact]
    public async Task CorruptChunk_IsAnErrorCell()
    {
        using var d = new TempDir();
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, new NbtCompound("") { new NbtInt("x", 1) }), new ChunkSpec(1, 0, new NbtCompound("") { new NbtInt("x", 2) })]);
        File.WriteAllBytes(d.File("r.0.0.mca"), bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (1000u << 8) | 1);
        File.WriteAllBytes(d.File("r.0.0-broken.mca"), bytes);

        using var vm = await Loaded(d.File("r.0.0.mca"), d.File("r.0.0-broken.mca"));
        Assert.Equal(ChunkDiffStatus.Error, vm.Grid[0, 0].Status);
        Assert.Contains("(0, 0)", vm.Grid[0, 0].Error);
        Assert.Equal(ChunkDiffStatus.Same, vm.Grid[1, 0].Status);
    }

    [Fact]
    public async Task Dispose_ReleasesFiles()
    {
        using var l = new TempDir();
        Base().Write(l.Path);
        var vm = await Loaded(Region(l.Path), Region(l.Path));
        vm.Dispose();
        Assert.True(vm.IsDisposed);
        File.Delete(Region(l.Path));   // would throw on Windows if the handle were still open
        Assert.False(File.Exists(Region(l.Path)));
    }
    [Fact]
    public async Task OpenSelected_ProvidesChangedChunkNavigation()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m =>
        {
            m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L);
            m.Chunk(0, 0, 7, 0).SetPath("InhabitedTime", 5L);
        }).Write(r.Path);
        using var vm = await Loaded(Region(l.Path), Region(r.Path));
        Assert.Equal((7, 0), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));   // first changed cell in (z, x) order

        ViewModelBase? pushed = null;
        vm.NavigationRequested += v => pushed = v;
        vm.OpenSelectedCommand.Execute(null);
        var chunk = Assert.IsType<FileCompareViewModel>(pushed);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(chunk.HasChunkNavigation);
        Assert.Equal([(7, 0), (3, 1)], chunk.Navigation!.Chunks);
        Assert.Equal("changed chunk 1 of 2", chunk.ChunkPositionText);

        chunk.NextChunkCommand.Execute(null);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("r.0.0.mca (3, 1)", chunk.Title);
        Assert.Equal("changed chunk 2 of 2", chunk.ChunkPositionText);
        Assert.Equal((3, 1), (vm.Grid.Selected!.X, vm.Grid.Selected.Z));   // grid follows
        Assert.Equal("InhabitedTime", Assert.Single(chunk.ChangedNodes).Path);
        Assert.Equal("999L", chunk.ChangedNodes[0].RightValueText);
    }
    [Fact]
    public async Task ChunkSaves_UpdateTheGrid_AndCopyingAMissingChunkDeletesIt()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        RegionWriter.Write(Region(l.Path), [new ChunkSpec(0, 0, WorldBuilder.MakeChunk(42, 0, 0)), new ChunkSpec(1, 0, WorldBuilder.MakeChunk(42, 1, 0))]);
        var changed = WorldBuilder.MakeChunk(42, 0, 0);
        changed.Add(new NbtInt("Extra", 1));
        Directory.CreateDirectory(Path.Combine(r.Path, "region"));
        RegionWriter.Write(Region(r.Path), [new ChunkSpec(0, 0, changed)]);   // (1, 0) exists only on the left

        using var vm = await Loaded(Region(l.Path), Region(r.Path));
        Assert.Equal(ChunkDiffStatus.Different, vm.Grid[0, 0].Status);
        Assert.Equal(ChunkDiffStatus.LeftOnly, vm.Grid[1, 0].Status);
        FileCompareViewModel? chunk = null;
        vm.NavigationRequested += v => chunk = (FileCompareViewModel)v;

        async Task CopyRootToLeftAndSave(int x, int z)
        {
            vm.SelectCommand.Execute(vm.Grid[x, z]);
            vm.OpenSelectedCommand.Execute(null);
            await chunk!.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
            chunk.SelectedItem = chunk.Root;
            await chunk.CopyToLeftCommand.ExecuteAsync(null);
            await chunk.SaveCommand.ExecuteAsync(null);
            await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(chunk.ErrorMessage);
        }

        await CopyRootToLeftAndSave(0, 0);
        Assert.Equal(ChunkDiffStatus.Same, vm.Grid[0, 0].Status);        // the grid is not stale

        await CopyRootToLeftAndSave(1, 0);                                // the right side has no chunk here
        Assert.Null(vm.Grid[1, 0].Status);                                // deleted: no chunk on either side
        Assert.Equal("1 chunk · 1 same", vm.HeaderText);
        using var reopened = RegionFile.Open(Region(l.Path)).ValueOrThrow();
        Assert.Null(reopened[1, 0]);
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task LegendFilters_DimCells_AndNavigationSkipsThem()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(r.Path);
        using var vm = await Loaded(Region(l.Path), Region(r.Path));

        vm.Grid.ShowSame = false;
        Assert.All(vm.Grid.Cells.Where(c => c.Status == ChunkDiffStatus.Same), c => Assert.True(c.IsDimmed));
        Assert.False(vm.Grid[3, 1].IsDimmed);
        Assert.False(vm.Grid[31, 31].IsDimmed);                        // empty cells are never dimmed

        // From (3, 1) moving left crosses dimmed same chunks and stops on the first undimmed cell.
        vm.SelectCommand.Execute(vm.Grid[3, 1]);
        vm.MoveCommand.Execute("left");
        Assert.False(vm.Grid.Selected!.IsDimmed);

        vm.Grid.ShowSame = true;
        Assert.DoesNotContain(vm.Grid.Cells, c => c.IsDimmed);
    }

    [Fact]
    public async Task PreviousNextChanged_VisitChangedChunksInOrder_SkippingFilteredStates()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Base().Write(l.Path);
        Base().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Mutate(m => m.Chunk(0, 0, 5, 1).SetPath("InhabitedTime", 7L)).Write(r.Path);
        using var vm = await Loaded(Region(l.Path), Region(r.Path));
        Assert.Same(vm.Grid[3, 1], vm.Grid.Selected);
        vm.NextChangedCommand.Execute(null);
        Assert.Same(vm.Grid[5, 1], vm.Grid.Selected);
        vm.NextChangedCommand.Execute(null);                            // no more: stays
        Assert.Same(vm.Grid[5, 1], vm.Grid.Selected);
        vm.PreviousChangedCommand.Execute(null);
        Assert.Same(vm.Grid[3, 1], vm.Grid.Selected);

        vm.Grid.ShowDifferent = false;
        Assert.False(vm.Grid.StepChanged(+1));                           // every changed chunk is filtered out
    }

    [Fact]
    public async Task SelectionPanel_ShowsCoordinates_AndSummarisesTheChangedTagsInTheBackground()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        new WorldBuilder(seed: 42).WithRegion(-1, 1, chunks: 10).Write(l.Path);
        new WorldBuilder(seed: 42).WithRegion(-1, 1, chunks: 10).Mutate(m => m.Chunk(-1, 1, 2, 0).SetPath("InhabitedTime", 999L)).Write(r.Path);
        string Rel(string dir) => Path.Combine(dir, "region", "r.-1.1.mca");
        using var vm = await Loaded(Rel(l.Path), Rel(r.Path));

        Assert.Same(vm.Grid[2, 0], vm.Grid.Selected);
        Assert.Equal("(2, 0)", vm.SelectedTitle);
        Assert.Equal(StateKind.Different, vm.SelectedState);
        Assert.Equal("Different", vm.SelectedStateText);
        Assert.Equal("(−30, 32)", vm.WorldChunkText);
        Assert.Equal("x −480…−465, z 512…527", vm.BlockRangeText);

        await WaitFor(() => vm.ChangedTagsText != "…");
        Assert.Equal("1", vm.ChangedTagsText);
        var change = Assert.Single(vm.SelectedChanges);
        Assert.Equal("InhabitedTime", change.Path);
        Assert.Equal("999L", change.Right);
        long before = long.Parse(change.Left!.TrimEnd('L'));
        Assert.Equal(999 - before >= 0 ? $"+{999 - before}" : $"−{before - 999}", change.Delta);

        vm.SelectCommand.Execute(vm.Grid.Cells.First(c => c.Status == ChunkDiffStatus.Same));
        Assert.Equal("0", vm.ChangedTagsText);
        Assert.Empty(vm.SelectedChanges);
        vm.SelectCommand.Execute(vm.Grid[2, 0]);                         // cached: no wait
        Assert.Equal("1", vm.ChangedTagsText);
    }
}
