using System.Diagnostics;
using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using Xunit.Abstractions;

namespace NbtDiff.Core.Tests;

/// <summary>
/// Performance harness, only active when <c>NBTDIFF_PERF_DIR</c> is set. <see cref="WriteIfRequested"/>
/// writes a synthetic 64-region world pair (~65k chunks a side) unless one already exists there;
/// <see cref="ScanIfRequested"/> times a Tier-1-only scan and a full scan and appends the numbers to
/// <c>&lt;dir&gt;/perf-results.txt</c>. Run with
/// <c>NBTDIFF_PERF_DIR=%TEMP%\nbtdiff-perf dotnet test tests/NbtDiff.Core.Tests --filter PerfScan</c>.
/// </summary>
public class PerfScan(ITestOutputHelper output)
{
    private const int RegionsPerAxis = 8;
    private const int ChunksPerRegion = 1024;
    private const int Seed = 1234;

    private static string? Dir => Environment.GetEnvironmentVariable("NBTDIFF_PERF_DIR");

    [Fact]
    public void WriteIfRequested()
    {
        if (Dir is not { Length: > 0 } dir) return;
        var left = Path.Combine(dir, "left");
        var right = Path.Combine(dir, "right");
        var sw = Stopwatch.StartNew();
        WritePlayerData(left, right);
        if (Directory.Exists(Path.Combine(left, "region")) && Directory.GetFiles(Path.Combine(left, "region"), "*.mca").Length >= RegionsPerAxis * RegionsPerAxis)
        {
            output.WriteLine($"Regions already present at {dir}; not rewriting them ({sw.Elapsed.TotalSeconds:F1}s).");
            return;
        }

        foreach (var d in new[] { left, right })
        {
            Directory.CreateDirectory(Path.Combine(d, "region"));
            NbtFixtures.WriteFile(Path.Combine(d, "level.dat"), new WorldBuilder(Seed).WithLevelDat("Perf World").File("level.dat"), NbtFormat.JavaNbt);
        }

        // Left: sequential ZLib. Right: everything recompressed (GZip) so Tier 1 sees every chunk as
        // different, plus one mutated chunk in every 4th region, one region missing, one extra region.
        var coords = Enumerable.Range(0, RegionsPerAxis * RegionsPerAxis).Select(i => (rx: i % RegionsPerAxis, rz: i / RegionsPerAxis)).ToList();
        Parallel.ForEach(coords, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
        {
            var region = new RegionCoords(c.rx, c.rz);
            var chunks = new List<ChunkSpec>(ChunksPerRegion);
            for (int i = 0; i < ChunksPerRegion; i++)
            {
                int x = i % 32, z = i / 32;
                var (wx, wz) = region.ChunkAt(x, z);
                chunks.Add(new ChunkSpec(x, z, WorldBuilder.MakeChunk(Seed, wx, wz), ChunkRef.SchemeZLib, Timestamp: 1_600_000_000u + (uint)i));
            }
            RegionWriter.Write(Path.Combine(left, "region", $"r.{c.rx}.{c.rz}.mca"), chunks);

            int index = c.rz * RegionsPerAxis + c.rx;
            if (index == 5) return; // missing on the right → LeftOnly
            var rightChunks = chunks.Select(s => s with { Scheme = ChunkRef.SchemeGZip, Timestamp = s.Timestamp + 1000 }).ToList();
            if (index % 4 == 0)
            {
                var mutated = (NbtCompound)rightChunks[7].Data.Clone();
                mutated.SetPath("InhabitedTime", 999L);
                rightChunks[7] = rightChunks[7] with { Data = mutated };
            }
            RegionWriter.Write(Path.Combine(right, "region", $"r.{c.rx}.{c.rz}.mca"), rightChunks, new RegionWriteOptions(SectorOrder.Reverse));
        });
        var extra = new RegionCoords(RegionsPerAxis, 0);
        RegionWriter.Write(Path.Combine(right, "region", $"r.{RegionsPerAxis}.0.mca"),
            Enumerable.Range(0, 64).Select(i => new ChunkSpec(i % 32, i / 32, WorldBuilder.MakeChunk(Seed, extra.ChunkAt(i % 32, i / 32).X, extra.ChunkAt(i % 32, i / 32).Z))));

        long bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        output.WriteLine($"Wrote {bytes / 1024 / 1024} MB to {dir} in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>Many small files: 10k identical player files a side plus a few changed ones, to measure per-file overhead and give the UI a 10k-row folder.</summary>
    private const int PlayerFiles = 10_000;

    private static void WritePlayerData(string left, string right)
    {
        var leftDir = Path.Combine(left, "playerdata");
        if (Directory.Exists(leftDir) && Directory.GetFiles(leftDir, "*.dat").Length >= PlayerFiles) return;
        var rightDir = Path.Combine(right, "playerdata");
        Directory.CreateDirectory(leftDir);
        Directory.CreateDirectory(rightDir);
        Parallel.For(0, PlayerFiles, i =>
        {
            var id = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var root = new WorldBuilder(Seed).WithPlayer(id).File($"playerdata/{id}.dat");
            var bytes = NbtFixtures.ToBytes(root, NbtFormat.JavaNbt, NbtCompression.GZip);
            File.WriteAllBytes(Path.Combine(leftDir, $"{id}.dat"), bytes);
            if (i % 1000 == 999)
            {
                root.SetPath("foodLevel", 19);
                bytes = NbtFixtures.ToBytes(root, NbtFormat.JavaNbt, NbtCompression.GZip);
            }
            File.WriteAllBytes(Path.Combine(rightDir, $"{id}.dat"), bytes);
        });
    }

    [Fact]
    public async Task ScanIfRequested()
    {
        if (Dir is not { Length: > 0 } dir) return;
        var left = Path.Combine(dir, "left");
        var right = Path.Combine(dir, "right");
        if (!Directory.Exists(Path.Combine(left, "region"))) { output.WriteLine("No world; run WriteIfRequested first."); return; }

        long bytes = Directory.EnumerateFiles(left, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                   + Directory.EnumerateFiles(right, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var lines = new List<string> { $"--- {DateTime.Now:yyyy-MM-dd HH:mm} · {Environment.ProcessorCount} cores · {bytes / 1024 / 1024} MB on disk" };

        foreach (var (label, deep) in new[] { ("tier1 (cold)", false), ("tier1 (warm)", false), ("tier1+tier2", true) })
        {
            GC.Collect();
            long peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;
            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            var root = new DirectoryComparer(new Fingerprinter(), new CompareOptions(DeepVerify: deep)).Prepare(left, right);
            root.Run(CancellationToken.None);
            await root.Completion;
            sw.Stop();
            var counts = root.Root.Counts;
            long alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
            var p = Process.GetCurrentProcess();
            string line = $"{label,-14} {sw.Elapsed.TotalSeconds,7:F2}s  {counts.Total / sw.Elapsed.TotalSeconds,7:F0} files/s  {bytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds,7:F0} MB/s  " +
                          $"alloc {alloc / 1024 / 1024,6} MB  peak WS {p.PeakWorkingSet64 / 1024 / 1024,5} MB (was {peakBefore / 1024 / 1024})  " +
                          $"→ {counts.Same} same · {counts.Different} differ · {counts.ProbablyDifferent} probably · {counts.LeftOnly} L · {counts.RightOnly} R · {counts.Error} err";
            lines.Add(line);
            output.WriteLine(line);
        }
        File.AppendAllLines(Path.Combine(dir, "perf-results.txt"), lines);
    }
}
