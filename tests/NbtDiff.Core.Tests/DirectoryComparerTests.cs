using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using static NbtDiff.Core.Tests.ScanSupport;

namespace NbtDiff.Core.Tests;

public class DirectoryComparerTests
{
    [Fact]
    public async Task Identical_AllSame()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path);
            Assert.Equal(RowStatus.Same, root.Root.Status);
            Assert.All(root.Files(), f => Assert.Equal(RowStatus.Same, f.Status));
            // level.dat, 2 regions, 1 player; session.lock excluded by default.
            Assert.Equal(4, root.Root.Counts.Total);
            Assert.Equal(4, root.Root.Counts.Same);
            Assert.DoesNotContain(root.Root.Descendants(), x => x.Name == "session.lock");
            Assert.True(root.Progress.Completed);
            Assert.False(root.IsCancelled);
            Assert.Equal(4, root.Progress.Tier1Done);
            Assert.Equal(0, root.Progress.Tier2Queued);
        }
    }

    [Fact]
    public async Task Mutate_OneDifferentRegion_CountsPropagate()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path);

            var region = root.Row("region/r.0.0.mca");
            Assert.Equal(RowStatus.Different, region.Status);
            Assert.Equal([region], root.Files().Where(f => f.Status != RowStatus.Same));

            var regionDir = root.Row("region");
            Assert.Equal(RowStatus.Different, regionDir.Status);
            Assert.Equal(new RowCounts(0, 1, 0, 1, 0, 0, 0), regionDir.Counts);
            Assert.Equal(RowStatus.Different, root.Root.Status);
            Assert.Equal(new RowCounts(0, 3, 0, 1, 0, 0, 0), root.Root.Counts);

            Assert.Equal(RowStatus.Same, root.Row("playerdata").Status);
            Assert.NotNull(region.Left!.Fingerprint);
            Assert.Equal(FingerprintTier.Deep, region.Left.Fingerprint!.Tier);
        }
    }

    [Theory]
    [InlineData("touch")]
    [InlineData("defrag")]
    [InlineData("recompress")]
    public async Task SameContent_ZeroNonSame(string variant)
    {
        var w = BaseWorld();
        var v = variant switch { "touch" => w.TouchTimestamps(), "defrag" => w.Defragment(), _ => w.Recompress(ChunkRef.SchemeGZip) };
        var (l, r) = Pair(w, v);
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path);
            Assert.Equal(0, root.Root.Counts.NonSame);
            Assert.Equal(RowStatus.Same, root.Root.Status);
            if (variant == "recompress")
                Assert.Equal(2, root.Progress.Tier2Queued);
            else
                Assert.Equal(0, root.Progress.Tier2Queued);
        }
    }

    [Fact]
    public async Task Recompress_ProbablyDifferentUntilTier2()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r)
        {
            var gated = new GatedFingerprinter(new Fingerprinter());
            gated.QuickGate.SetResult();
            var root = new DirectoryComparer(gated).Start(l.Path, r.Path);

            await WaitUntil(() => root.Progress.Tier1Complete, "tier 1");
            Assert.Equal(RowStatus.ProbablyDifferent, root.Row("region/r.0.0.mca").Status);
            Assert.Equal(RowStatus.ProbablyDifferent, root.Row("region").Status);
            Assert.Equal(RowStatus.ProbablyDifferent, root.Root.Status);
            Assert.Equal(2, root.Root.Counts.ProbablyDifferent);
            Assert.Equal(FingerprintTier.Quick, root.Row("region/r.0.0.mca").Left!.Fingerprint!.Tier);
            Assert.False(root.Completion.IsCompleted);

            gated.DeepGate.SetResult();
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(RowStatus.Same, root.Root.Status);
            Assert.Equal(0, root.Root.Counts.NonSame);
            Assert.Equal(2, root.Progress.Tier2Done);
        }
    }

    [Fact]
    public async Task DeepVerifyOff_RecompressStaysDifferent()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld().Recompress(ChunkRef.SchemeGZip));
        using (l) using (r)
        {
            var gated = new GatedFingerprinter(new Fingerprinter());
            gated.QuickGate.SetResult();
            var root = await Scan(l.Path, r.Path, new CompareOptions(DeepVerify: false), gated);
            Assert.Equal(RowStatus.Different, root.Row("region/r.0.0.mca").Status);
            Assert.Equal(2, root.Root.Counts.Different);
            Assert.Equal(0, root.Root.Counts.ProbablyDifferent);
            Assert.Equal(0, gated.DeepCalls);
        }
    }

    [Fact]
    public async Task OneSided_FilesAndDirectories()
    {
        var left = BaseWorld().WithFile("data/raids.dat", new NbtCompound("") { new NbtInt("x", 1) });
        var right = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithRegion(-1, 0, chunks: 3).WithLevelDat()
            .WithFile("extra.dat", new NbtCompound("") { new NbtInt("y", 2) });
        var (l, r) = Pair(left, right);
        using (l) using (r)
        {
            File.WriteAllText(l.File("stats.txt"), "x");
            Directory.CreateDirectory(r.File(Path.Combine("DIM1", "region")));
            File.WriteAllBytes(r.File(Path.Combine("DIM1", "region", "r.0.0.mca")), RegionWriter.Build([]));

            var root = await Scan(l.Path, r.Path);

            Assert.Equal(RowStatus.LeftOnly, root.Row("playerdata").Status);
            Assert.Equal(RowStatus.LeftOnly, root.Row("playerdata/11111111-2222-3333-4444-555555555555.dat").Status);
            Assert.Equal(RowStatus.LeftOnly, root.Row("data").Status);
            Assert.Equal(RowStatus.LeftOnly, root.Row("data/raids.dat").Status);
            Assert.Equal(RowStatus.LeftOnly, root.Row("stats.txt").Status);
            Assert.Equal(RowStatus.RightOnly, root.Row("extra.dat").Status);
            Assert.Equal(RowStatus.RightOnly, root.Row("DIM1").Status);
            Assert.Equal(RowStatus.RightOnly, root.Row("DIM1/region").Status);
            Assert.Equal(RowStatus.RightOnly, root.Row("DIM1/region/r.0.0.mca").Status);
            Assert.Null(root.Row("DIM1").Left);
            Assert.NotNull(root.Row("DIM1").Right);

            Assert.Equal(new RowCounts(0, 0, 0, 0, 0, 1, 0), root.Row("DIM1").Counts);
            Assert.Equal(new RowCounts(0, 3, 0, 0, 3, 2, 0), root.Root.Counts);
            Assert.Equal(RowStatus.Different, root.Root.Status);
        }
    }

    [Fact]
    public async Task CorruptFile_ErrorRow_ScanContinues()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            // Garbage level.dat on the right: quick differs, deep fails to parse → Error.
            File.WriteAllBytes(r.File("level.dat"), Enumerable.Range(0, 300).Select(i => (byte)(i * 13)).ToArray());
            // Region with a corrupt header slot on the left → Error via ChunkErrors.
            var regionPath = l.File(Path.Combine("region", "r.-1.0.mca"));
            var bytes = File.ReadAllBytes(regionPath);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), (5000u << 8) | 1);
            File.WriteAllBytes(regionPath, bytes);

            var root = await Scan(l.Path, r.Path);

            var level = root.Row("level.dat");
            Assert.Equal(RowStatus.Error, level.Status);
            Assert.Contains("right:", level.Error);

            var region = root.Row("region/r.-1.0.mca");
            Assert.Equal(RowStatus.Error, region.Status);
            Assert.Contains("unreadable chunk", region.Error);
            Assert.Contains("left:", region.Error);

            Assert.Equal(RowStatus.Same, root.Row("region/r.0.0.mca").Status);
            Assert.Equal(RowStatus.Same, root.Row("playerdata").Status);
            Assert.Equal(2, root.Root.Counts.Error);
            Assert.Equal(2, root.Root.Counts.Same);
            Assert.Equal(RowStatus.Error, root.Row("region").Status);
            Assert.Equal(RowStatus.Error, root.Root.Status);
            Assert.True(root.Progress.Completed);
        }
    }

    [Fact]
    public async Task DirectoryVersusFile_IsError()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Directory.CreateDirectory(l.File("thing"));
        File.WriteAllText(l.File(Path.Combine("thing", "a.txt")), "a");
        File.WriteAllText(r.File("thing"), "not a directory");

        var root = await Scan(l.Path, r.Path);
        var row = root.Row("thing");
        Assert.Equal(RowStatus.Error, row.Status);
        Assert.Contains("directory on the left", row.Error);
        Assert.Empty(row.Children);
    }

    [Fact]
    public async Task CaseSensitivePairing()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        Directory.CreateDirectory(l.File("Region"));
        File.WriteAllText(l.File(Path.Combine("Region", "a.txt")), "a");
        Directory.CreateDirectory(r.File("region"));
        File.WriteAllText(r.File(Path.Combine("region", "a.txt")), "a");
        File.WriteAllText(l.File("Level.dat"), "x");
        File.WriteAllText(r.File("level.dat"), "x");

        var root = await Scan(l.Path, r.Path);
        Assert.Equal(RowStatus.LeftOnly, root.Row("Region").Status);
        Assert.Equal(RowStatus.RightOnly, root.Row("region").Status);
        Assert.Equal(RowStatus.LeftOnly, root.Row("Level.dat").Status);
        Assert.Equal(RowStatus.RightOnly, root.Row("level.dat").Status);
        Assert.Equal(0, root.Root.Counts.Same);
    }

    [Fact]
    public async Task Cancellation_LeavesPendingAndCompletes()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            var gated = new GatedFingerprinter(new Fingerprinter());
            using var cts = new CancellationTokenSource();
            var root = new DirectoryComparer(gated, new CompareOptions(MaxParallelism: 2)).Start(l.Path, r.Path, cts.Token);

            await WaitUntil(() => gated.QuickCalls >= 2, "workers to reach the gate");
            cts.Cancel();
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(root.IsCancelled);
            Assert.True(root.Progress.Cancelled);
            Assert.True(root.Progress.Completed);
            Assert.All(root.Files(), f => Assert.Equal(RowStatus.Pending, f.Status));
            Assert.Equal(RowStatus.Pending, root.Root.Status);
            Assert.Equal(4, root.Root.Counts.Pending);

            gated.QuickGate.SetResult(); // releasing afterwards must not resurrect the scan
            await Task.Delay(100);
            Assert.All(root.Files(), f => Assert.Equal(RowStatus.Pending, f.Status));
        }
    }

    [Fact]
    public async Task CancellationBeforeStart_CompletesImmediately()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var root = new DirectoryComparer().Start(l.Path, r.Path, cts.Token);
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(root.IsCancelled);
            Assert.Equal(4, root.Root.Counts.Pending);
        }
    }

    [Fact]
    public async Task ExcludeGlobs()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            File.WriteAllText(l.File("latest.log"), "l");
            File.WriteAllText(r.File("other.log"), "r");

            var root = await Scan(l.Path, r.Path, new CompareOptions(ExcludeGlobs: ["*.log", "session.lock", "playerdata/**", "region/r.-1.*.mca"]));
            var paths = root.Root.Descendants().Select(x => x.RelativePath).ToList();
            Assert.DoesNotContain("latest.log", paths);
            Assert.DoesNotContain("other.log", paths);
            Assert.DoesNotContain("session.lock", paths);
            Assert.DoesNotContain("region/r.-1.0.mca", paths);
            Assert.Contains("region/r.0.0.mca", paths);
            Assert.Contains("playerdata", paths); // the directory row stays, its contents are excluded
            Assert.Empty(root.Row("playerdata").Children);
            Assert.Equal(RowStatus.Same, root.Row("playerdata").Status);
            Assert.Equal(2, root.Root.Counts.Total);
        }
    }

    [Fact]
    public async Task NoExcludes_SessionLockCompared()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path, new CompareOptions(ExcludeGlobs: []));
            Assert.Equal(RowStatus.Same, root.Row("session.lock").Status);
            Assert.Equal(FileKind.Text, root.Row("session.lock").Kind);
        }
    }

    [Fact]
    public async Task Ordering_DirectoriesFirst_ThenNatural()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        foreach (var d in new[] { l, r })
        {
            Directory.CreateDirectory(d.File("region"));
            foreach (var n in new[] { "r.10.0.mca", "r.2.0.mca", "r.-1.0.mca", "r.0.0.mca" })
                File.WriteAllBytes(d.File(Path.Combine("region", n)), RegionWriter.Build([]));
            Directory.CreateDirectory(d.File("zdir"));
            Directory.CreateDirectory(d.File("adir"));
            File.WriteAllText(d.File("aaa.txt"), "");
        }
        var root = await Scan(l.Path, r.Path);
        Assert.Equal(["adir", "region", "zdir", "aaa.txt"], root.Root.Children.Select(c => c.Name));
        Assert.Equal(["r.-1.0.mca", "r.0.0.mca", "r.2.0.mca", "r.10.0.mca"], root.Row("region").Children.Select(c => c.Name));
    }

    [Fact]
    public async Task RowChanged_FiresForFilesAndAncestors()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld().Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)));
        using (l) using (r)
        {
            var gated = new GatedFingerprinter(new Fingerprinter());
            var root = new DirectoryComparer(gated).Prepare(l.Path, r.Path);
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            root.RowChanged += row => seen.AddOrUpdate(row.RelativePath, 1, (_, n) => n + 1);
            var progress = new List<ScanProgress>();
            root.ProgressChanged += p => { lock (progress) progress.Add(p); };
            root.Run();
            gated.QuickGate.SetResult();
            gated.DeepGate.SetResult();
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(seen.ContainsKey("region/r.0.0.mca"));
            Assert.True(seen.ContainsKey("region"));
            Assert.True(seen.ContainsKey(""));
            Assert.True(seen["region/r.0.0.mca"] >= 2); // ProbablyDifferent, then Different
            Assert.Contains(progress, p => p.Completed);
            Assert.Equal(1.0, progress[^1].Fraction);
        }
    }

    [Fact]
    public void MissingRoot_Throws()
    {
        using var l = new TempDir();
        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryComparer().Prepare(l.Path, l.File("nope")));
        Assert.Throws<DirectoryNotFoundException>(() => new DirectoryComparer().Prepare(l.File("nope"), l.Path));
    }

    [Fact]
    public async Task EmptyDirectories_AreSame()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        var root = await Scan(l.Path, r.Path);
        Assert.Equal(RowStatus.Same, root.Root.Status);
        Assert.Equal(0, root.Root.Counts.Total);
        Assert.Equal(1.0, root.Progress.Fraction);
    }

    [Fact]
    public async Task BinaryMismatch_IsFinalAtTier1()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        File.WriteAllBytes(l.File("icon.png"), [1, 2, 3]);
        File.WriteAllBytes(r.File("icon.png"), [1, 2, 4]);
        var gated = new GatedFingerprinter(new Fingerprinter());
        gated.QuickGate.SetResult();
        var root = await Scan(l.Path, r.Path, fp: gated);
        Assert.Equal(RowStatus.Different, root.Row("icon.png").Status);
        Assert.Equal(0, gated.DeepCalls);
        Assert.Equal(0, root.Progress.Tier2Queued);
    }

    [Fact]
    public async Task Run_IsIdempotent()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            var root = new DirectoryComparer().Prepare(l.Path, r.Path);
            root.Run();
            root.Run();
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(4, root.Progress.Tier1Done);
        }
    }
}
