using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

/// <summary>
/// Writes a demo world pair for manual runs of the app. Only does anything when the
/// <c>NBTDIFF_DEMO_DIR</c> environment variable is set; then <c>&lt;dir&gt;/left</c> and <c>&lt;dir&gt;/right</c>
/// are (re)created and <c>nbtdiff.exe &lt;dir&gt;/left &lt;dir&gt;/right</c> shows every row state.
/// </summary>
public class DemoWorld
{
    [Fact]
    public void WriteIfRequested()
    {
        var dir = Environment.GetEnvironmentVariable("NBTDIFF_DEMO_DIR");
        if (string.IsNullOrEmpty(dir)) return;

        var left = Path.Combine(dir, "left");
        var right = Path.Combine(dir, "right");
        foreach (var d in new[] { left, right })
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);

        var world = new WorldBuilder(seed: 7)
            .WithRegion(0, 0, chunks: 40)
            .WithRegion(-1, 0, chunks: 12)
            .WithRegion(0, -1, chunks: 5)
            .WithLevelDat("Demo World")
            .WithPlayer(Guid.Parse("069a79f4-44e9-4726-a5be-fca90e38aaf5"))
            .WithFile("data/raids.dat", NbtFixtures.SampleCompound(3))
            .WithFile("data/only-left.dat", NbtFixtures.SampleCompound(4));

        world.Write(left);

        world
            .Mutate(m =>
            {
                m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L);
                m.Chunk(0, 0, 7, 0).SetPath("Status", "minecraft:light");
                m.RemoveChunk(-1, 0, 2, 0);
                m.File("level.dat").SetPath("Data/Time", 123456L);
                m.RemoveFile("data/only-left.dat");
            })
            .WithFile("data/only-right.dat", NbtFixtures.SampleCompound(5))
            .WithRegion(1, 1, chunks: 2)
            .Recompress(ChunkRef.SchemeGZip)   // r.0.-1.mca: same content, different bytes → resolved by deep verify
            .Write(right);

        // A corrupt region on the right, to show an Error row.
        var corrupt = Path.Combine(right, "region", "r.5.5.mca");
        File.WriteAllBytes(corrupt, new byte[100]);
        File.WriteAllBytes(Path.Combine(left, "region", "r.5.5.mca"), RegionWriter.Build([new ChunkSpec(0, 0, new NbtCompound("") { new NbtInt("x", 1) })]));

        // Text files: same content, different line endings.
        Directory.CreateDirectory(Path.Combine(left, "stats"));
        Directory.CreateDirectory(Path.Combine(right, "stats"));
        File.WriteAllText(Path.Combine(left, "stats", "player.json"), "{\n  \"stats\": {}\n}\n");
        File.WriteAllText(Path.Combine(right, "stats", "player.json"), "{\r\n  \"stats\": {}\r\n}\r\n");
    }
}
