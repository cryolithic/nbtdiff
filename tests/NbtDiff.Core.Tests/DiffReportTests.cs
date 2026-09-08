using System.Text.Json;
using fNbt;
using NbtDiff.TestFixtures;
using static NbtDiff.Core.Tests.ScanSupport;

namespace NbtDiff.Core.Tests;

public class DiffReportTests
{
    private static async Task<(TempDir l, TempDir r, CompareRoot root)> MutateWorld()
    {
        var left = BaseWorld();
        var right = BaseWorld()
            .Mutate(m => { m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L); m.RemoveFile("playerdata/11111111-2222-3333-4444-555555555555.dat"); })
            .WithFile("data/raids.dat", new NbtCompound("") { new NbtInt("x", 1) });
        var (l, r) = Pair(left, right);
        var root = await Scan(l.Path, r.Path);
        return (l, r, root);
    }

    [Fact]
    public async Task Text_ListsNonSameRowsAndCounts()
    {
        var (l, r, root) = await MutateWorld();
        using (l) using (r)
        {
            var text = DiffReport.ToString(root, ReportFormat.Text);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.Equal($"Left:  {root.LeftRoot}", lines[0]);
            Assert.Equal($"Right: {root.RightRoot}", lines[1]);
            Assert.Equal("2 same · 0 probably different · 1 different · 1 left-only · 1 right-only · 0 error · 0 pending", lines[2]);
            Assert.DoesNotContain("not complete", text);

            var rows = lines.Skip(3).ToList();
            Assert.Equal(
            [
                "RightOnly          data/  (1 file)",
                "RightOnly          data/raids.dat  [- | " + new FileInfo(r.File(Path.Combine("data", "raids.dat"))).Length + "]",
                "LeftOnly           playerdata/  (1 file)",
                "LeftOnly           playerdata/11111111-2222-3333-4444-555555555555.dat  [" + new FileInfo(l.File(Path.Combine("playerdata", "11111111-2222-3333-4444-555555555555.dat"))).Length + " | -]",
            ], rows.Take(4));
            Assert.StartsWith("Different          region/r.0.0.mca  [", rows[4]);
            Assert.Equal(5, rows.Count);
            Assert.DoesNotContain("level.dat", text);
        }
    }

    [Fact]
    public async Task Json_Structure()
    {
        var (l, r, root) = await MutateWorld();
        using (l) using (r)
        {
            var json = DiffReport.ToString(root, ReportFormat.Json);
            using var doc = JsonDocument.Parse(json);
            var top = doc.RootElement;

            Assert.Equal(root.LeftRoot, top.GetProperty("Left").GetString());
            Assert.Equal(root.RightRoot, top.GetProperty("Right").GetString());
            Assert.True(top.GetProperty("Completed").GetBoolean());
            Assert.False(top.GetProperty("Cancelled").GetBoolean());

            var counts = top.GetProperty("Counts");
            Assert.Equal(2, counts.GetProperty("Same").GetInt32());
            Assert.Equal(1, counts.GetProperty("Different").GetInt32());
            Assert.Equal(1, counts.GetProperty("LeftOnly").GetInt32());
            Assert.Equal(1, counts.GetProperty("RightOnly").GetInt32());

            var rows = top.GetProperty("Rows").EnumerateArray().ToList();
            Assert.Equal(5, rows.Count);
            var byPath = rows.ToDictionary(x => x.GetProperty("Path").GetString()!);

            var region = byPath["region/r.0.0.mca"];
            Assert.Equal("Different", region.GetProperty("Status").GetString());
            Assert.Equal("Region", region.GetProperty("Kind").GetString());
            Assert.True(region.GetProperty("Left").GetProperty("Size").GetInt64() > 0);
            Assert.True(region.GetProperty("Right").GetProperty("Size").GetInt64() > 0);
            Assert.False(region.TryGetProperty("Error", out _));
            Assert.False(region.TryGetProperty("Counts", out _));

            var dataDir = byPath["data"];
            Assert.Equal("Directory", dataDir.GetProperty("Kind").GetString());
            Assert.Equal("RightOnly", dataDir.GetProperty("Status").GetString());
            Assert.False(dataDir.TryGetProperty("Left", out _));
            Assert.Equal(1, dataDir.GetProperty("Counts").GetProperty("RightOnly").GetInt32());

            Assert.Equal("LeftOnly", byPath["playerdata/11111111-2222-3333-4444-555555555555.dat"].GetProperty("Status").GetString());
            Assert.False(byPath["playerdata/11111111-2222-3333-4444-555555555555.dat"].TryGetProperty("Right", out _));
        }
    }

    [Fact]
    public async Task Text_ErrorsIncludeMessage()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            File.WriteAllBytes(r.File("level.dat"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
            var root = await Scan(l.Path, r.Path);
            var text = DiffReport.ToString(root, ReportFormat.Text);
            var line = text.Split('\n').Single(x => x.Contains("level.dat"));
            Assert.StartsWith("Error", line);
            Assert.Contains("right:", line);
        }
    }

    [Fact]
    public async Task Incomplete_IsFlagged()
    {
        var (l, r) = Pair(BaseWorld(), BaseWorld());
        using (l) using (r)
        {
            var gated = new GatedFingerprinter(new Fingerprinter());
            var root = new DirectoryComparer(gated).Start(l.Path, r.Path);
            var text = DiffReport.ToString(root, ReportFormat.Text);
            Assert.Contains("(scan not complete)", text);
            Assert.Contains("4 pending", text);
            gated.QuickGate.SetResult();
            gated.DeepGate.SetResult();
            await root.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.DoesNotContain("not complete", DiffReport.ToString(root, ReportFormat.Text));
        }
    }
}
