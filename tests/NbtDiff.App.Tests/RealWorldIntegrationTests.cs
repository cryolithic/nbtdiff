using fNbt;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Core.Diff;
using NbtDiff.Nbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

/// <summary>
/// Issue #5: edit-and-save against real worlds. tests/data/real/&lt;set&gt; holds two snapshots of one
/// world, both written by Minecraft — "initial", then "expected" after some play — with the player's
/// identity scrubbed (tools/fixtures/make-real-fixtures.cs). A copy of "initial" is edited through the
/// same view models the UI uses until it should match "expected"; Minecraft itself produced the
/// expected result, so the test checks nbt-diff's writes against the game rather than against
/// nbt-diff's own reader.
/// </summary>
[Trait("Category", "Integration")]
public class RealWorldIntegrationTests
{
    private static string DataRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "data", "real"));

    private static readonly CompareOptions DeepCompare = new(DeepVerify: true, ExcludeGlobs: ["*.bak"]);

    private static async Task<CompareRoot> Compare(string left, string right)
    {
        var root = new DirectoryComparer(DeepCompare).Prepare(left, right);
        root.Run();
        await root.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        return root;
    }

    private static IEnumerable<CompareRow> Files(CompareRow row) =>
        row.IsDirectory ? row.Children.SelectMany(Files) : [row];

    private static async Task<FileCompareViewModel> Loaded(FileCompareViewModel vm)
    {
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    /// <summary>Selects the root row, copies the expected (right) side over the working (left) side, saves.</summary>
    private static async Task CopyWholeRightToLeftAndSave(FileCompareViewModel vm, string what)
    {
        vm.SelectedItem = vm.Root;
        await vm.CopyToLeftCommand.ExecuteAsync(null);
        Assert.True(vm.LeftModified, $"{what}: copy did not modify the left side");
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(vm.ErrorMessage is null, $"{what}: {vm.ErrorMessage}");
        Assert.False(vm.HasUnsavedEdits, $"{what}: still has unsaved edits");
    }

    /// <summary>Copies every changed or right-only chunk of a region from expected into the working copy.</summary>
    private static async Task<int> MergeRegion(string work, string expected, SaveGate gate)
    {
        using var region = new RegionCompareViewModel(work, expected, null, null, new ImmediateUiDispatcher(), saveGate: gate);
        await region.Load().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Null(region.ErrorMessage);
        FileCompareViewModel? chunk = null;
        region.NavigationRequested += v => chunk = (FileCompareViewModel)v;
        int leftOnly = 0;
        foreach (var cell in region.Grid.Cells.Where(c => c.IsPresent && c.Status != ChunkDiffStatus.Same).ToList())
        {
            // Copying the absent right side over a left-only chunk deletes it (#6).
            if (cell.Status == ChunkDiffStatus.LeftOnly) leftOnly++;
            region.SelectCommand.Execute(cell);
            region.OpenSelectedCommand.Execute(null);
            await Loaded(chunk!);
            await CopyWholeRightToLeftAndSave(chunk!, $"{work} ({cell.X}, {cell.Z})");
        }
        return leftOnly;
    }

    [Theory]
    [InlineData("vanilla", 6)]    // six entity chunks despawned between the snapshots: deleted by the merge
    [InlineData("neoforge", 0)]
    public async Task MergingEveryDifference_MakesTheWorkingCopyMatchExpected(string set, int expectedLeftOnlyChunks)
    {
        string initial = Path.Combine(DataRoot(), set, "initial");
        string expected = Path.Combine(DataRoot(), set, "expected");
        using var work = new TempDir();
        foreach (var file in Directory.EnumerateFiles(initial, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(work.Path, Path.GetRelativePath(initial, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var dialogs = new FakeDialogService();
        var gate = new SaveGate(dialogs);   // real session-lock check; the fixture worlds are not open
        var before = await Compare(work.Path, expected);
        var saved = new List<string>();
        int leftOnlyChunks = 0;
        foreach (var row in Files(before.Root).Where(r => r.Status is RowStatus.Different or RowStatus.ProbablyDifferent))
        {
            string left = Path.Combine(work.Path, row.RelativePath), right = Path.Combine(expected, row.RelativePath);
            switch (row.Kind)
            {
                case FileKind.Region:
                    leftOnlyChunks += await MergeRegion(left, right, gate);
                    saved.Add(row.RelativePath);
                    break;
                case FileKind.Nbt or FileKind.Snbt:
                    var vm = await Loaded(new FileCompareViewModel(new FileDiffSource(left, right), new ImmediateUiDispatcher(), saveGate: gate));
                    await CopyWholeRightToLeftAndSave(vm, row.RelativePath);
                    saved.Add(row.RelativePath);
                    break;
                // Json / text: the app compares these but does not edit them.
            }
        }
        Assert.NotEmpty(saved);
        Assert.Equal(["confirm:Back up before saving"], dialogs.Requests);   // asked once for the whole session

        // Content now matches what Minecraft wrote, except text files (the app does not edit text).
        var after = await Compare(work.Path, expected);
        var remaining = Files(after.Root).Where(r => r.Status != RowStatus.Same).ToList();
        Assert.All(remaining, r => Assert.True(r.Kind is FileKind.Json or FileKind.Text, $"{r.RelativePath} is still {r.Status}"));
        Assert.Equal(expectedLeftOnlyChunks, leftOnlyChunks);

        // Every saved file kept its original as .bak; files that were not edited are byte-identical.
        foreach (var file in Directory.EnumerateFiles(initial, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(initial, file);
            string workFile = Path.Combine(work.Path, rel);
            if (saved.Contains(rel.Replace('\\', '/')))   // scan paths use '/' on every OS
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(workFile + ".bak"));
                AssertSameEncoding(file, workFile);
            }
            else
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(workFile));
        }
        Assert.Empty(Directory.EnumerateFiles(work.Path, "*.nbtdiff-tmp", SearchOption.AllDirectories));
    }

    /// <summary>A save keeps the file's format, compression and endianness, and each chunk's compression scheme.</summary>
    private static void AssertSameEncoding(string original, string written)
    {
        if (FileClassifier.Classify(original) == FileKind.Region)
        {
            using var before = RegionFile.Open(original).ValueOrThrow();
            using var after = RegionFile.Open(written).ValueOrThrow();
            foreach (var chunk in before.Chunks)
            {
                chunk.ReadCompressedPayload().ValueOrThrow();
                if (after[chunk.X, chunk.Z] is not { } now) continue;   // deleted by the merge
                now.ReadCompressedPayload().ValueOrThrow();
                Assert.True(chunk.SchemeByte == now.SchemeByte, $"{written} ({chunk.X}, {chunk.Z}): scheme {chunk.SchemeByte} became {now.SchemeByte}");
            }
            return;
        }
        var formatBefore = NbtDocument.Load(original).ValueOrThrow().Format;
        var formatAfter = NbtDocument.Load(written).ValueOrThrow().Format;
        Assert.True(formatBefore == formatAfter, $"{written}: {formatBefore} became {formatAfter}");
    }

    [Theory]
    [InlineData("vanilla", "dimensions/minecraft/overworld/region/r.0.0.mca")]
    [InlineData("neoforge", "entities/r.0.0.mca")]
    public async Task CopyingOneValue_ChangesOnlyThatValue(string set, string regionPath)
    {
        using var work = new TempDir();
        string left = work.File("r.mca");
        File.Copy(Path.Combine(DataRoot(), set, "initial", regionPath), left);
        string right = Path.Combine(DataRoot(), set, "expected", regionPath);

        using var region = new RegionCompareViewModel(left, right, null, null, new ImmediateUiDispatcher());
        await region.Load().WaitAsync(TimeSpan.FromSeconds(60));
        FileCompareViewModel? chunk = null;
        region.NavigationRequested += v => chunk = (FileCompareViewModel)v;
        // The first changed chunk that has a changed scalar (some only gain or lose tags).
        ChunkCellItem? cell = null;
        DiffNodeItem? leaf = null;
        foreach (var candidate in region.Grid.Cells.Where(c => c.Status == ChunkDiffStatus.Different))
        {
            region.SelectCommand.Execute(candidate);
            region.OpenSelectedCommand.Execute(null);
            await Loaded(chunk!);
            chunk!.ExpandAllCommand.Execute(null);
            leaf = chunk.Tree.Rows.FirstOrDefault(i => i.Kind == DiffKind.ValueChanged && i.Node.Left is not NbtContainerTag);
            if (leaf is not null) { cell = candidate; break; }
        }
        Assert.NotNull(leaf);
        var path = leaf.Node.Path;
        var expectedValue = (NbtTag)leaf.Node.Right!.Clone();
        var originalLeft = (NbtCompound)chunk!.Root!.Node.Left!.Clone();
        chunk!.SelectedItem = leaf;
        await chunk.CopyToLeftCommand.ExecuteAsync(null);
        await chunk.SaveCommand.ExecuteAsync(null);
        await chunk.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Null(chunk.ErrorMessage);
        region.Dispose();

        using var reopened = RegionFile.Open(left).ValueOrThrow();
        var written = reopened[cell!.X, cell.Z]!.ReadNbt().ValueOrThrow();
        // Against the original, the only difference is the copied value, which now equals expected's.
        var diff = NbtDiffer.Diff(originalLeft, written, new DiffOptions(ListAligner: KeyedAligner.Default, IgnoredTags: TagIgnoreSet.Default));
        var changed = diff.Descendants().Where(n => n.Kind != DiffKind.Unchanged && n.Children.Count == 0).ToList();
        var single = Assert.Single(changed);
        Assert.Equal(path, single.Path);
        Assert.Equal(SnbtWriter.WriteValue(expectedValue), SnbtWriter.WriteValue(single.Right!));
    }
}
