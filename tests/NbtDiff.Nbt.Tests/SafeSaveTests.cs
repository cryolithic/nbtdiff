using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

/// <summary>Issue #3: a failed or interrupted save never destroys the original, and regions get a backup.</summary>
public class SafeSaveTests
{
    private static NbtCompound Tiny(int x, int z) => new("") { new NbtInt("x", x), new NbtInt("z", z) };

    // ── SafeFile ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WriteAtomic_ReplacesTheFile_AndLeavesNoTemp()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("f.txt"), "old");
        SafeFile.WriteAtomic(d.File("f.txt"), s => s.Write("new"u8));
        Assert.Equal("new", File.ReadAllText(d.File("f.txt")));
        Assert.Equal(["f.txt"], Directory.GetFiles(d.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void WriteAtomic_FailureMidWrite_KeepsTheOriginal_AndLeavesNoTemp()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("f.txt"), "old");
        var e = Assert.Throws<IOException>(() => SafeFile.WriteAtomic(d.File("f.txt"), s =>
        {
            s.Write("partial"u8);
            throw new IOException("disk full");
        }));
        Assert.Equal("disk full", e.Message);
        Assert.Equal("old", File.ReadAllText(d.File("f.txt")));
        Assert.Equal(["f.txt"], Directory.GetFiles(d.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void WriteAtomic_KeepsUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var d = new TempDir();
        File.WriteAllText(d.File("f.txt"), "old");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(d.File("f.txt"), mode);
        SafeFile.WriteAtomic(d.File("f.txt"), s => s.Write("new"u8));
        Assert.Equal(mode, File.GetUnixFileMode(d.File("f.txt")));
    }

    [Fact]
    public void BackupOnce_CopiesTheFirstTimeOnly()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("f.dat"), "v1");
        SafeFile.BackupOnce(d.File("f.dat"));
        File.WriteAllText(d.File("f.dat"), "v2");
        SafeFile.BackupOnce(d.File("f.dat"));
        Assert.Equal("v1", File.ReadAllText(d.File("f.dat.bak")));
    }

    [Fact]
    public void BackupOnce_MissingFile_DoesNothing()
    {
        using var d = new TempDir();
        SafeFile.BackupOnce(d.File("none.dat"));
        Assert.Empty(Directory.GetFiles(d.Path));
    }

    // ── NbtDocument.Save ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DocumentSave_SerializationFailsPartway_OriginalIntact_NoTemp()
    {
        using var d = new TempDir();
        var path = d.File("level.dat");
        var original = new NbtCompound("") { new NbtInt("k", 1) };
        NbtFixtures.WriteFile(path, original, NbtFormat.JavaNbt);
        var before = File.ReadAllBytes(path);
        var doc = NbtAssert.Ok(NbtDocument.Load(path));

        // An unnamed root cannot be serialized; fNbt throws once the temp file is already open.
        var bad = new NbtCompound { new NbtInt("k", 2) };
        NbtAssert.Failed(doc.Save(bad));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SafeFile.TempSuffix));
    }

    [Theory]
    [InlineData(NbtFormat.JavaNbt)]
    [InlineData(NbtFormat.Snbt)]
    public void DocumentSave_WritesAndKeepsAOneTimeBackup(NbtFormat format)
    {
        using var d = new TempDir();
        var path = d.File(format == NbtFormat.Snbt ? "a.snbt" : "a.dat");
        NbtFixtures.WriteFile(path, new NbtCompound("") { new NbtInt("k", 1) }, format);
        var before = File.ReadAllBytes(path);
        var doc = NbtAssert.Ok(NbtDocument.Load(path));

        NbtAssert.Ok(doc.Save(new NbtCompound("") { new NbtInt("k", 2) }));
        NbtAssert.Ok(doc.Save(new NbtCompound("") { new NbtInt("k", 3) }));

        Assert.Equal(3, NbtAssert.Ok(NbtDocument.Load(path)).Root.Get<NbtInt>("k")!.Value);
        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        Assert.False(File.Exists(path + SafeFile.TempSuffix));
    }

    // ── RegionFile.WriteChunk ───────────────────────────────────────────────────────────────

    [Fact]
    public void RegionWrite_FirstWriteBacksUpTheRegion_LaterWritesKeepThatBackup()
    {
        using var d = new TempDir();
        var path = d.File("r.0.0.mca");
        RegionWriter.Write(path, [new ChunkSpec(0, 0, Tiny(0, 0)), new ChunkSpec(1, 0, Tiny(1, 0))]);
        var before = File.ReadAllBytes(path);

        using (var region = NbtAssert.Ok(RegionFile.Open(path)))
        {
            NbtAssert.Ok(region.WriteChunk(0, 0, Tiny(7, 7)));
            NbtAssert.Ok(region.WriteChunk(1, 0, Tiny(8, 8)));
        }

        Assert.Equal(before, File.ReadAllBytes(path + ".bak"));
        using var reopened = NbtAssert.Ok(RegionFile.Open(path));
        NbtAssert.Equal(Tiny(7, 7), NbtAssert.Ok(reopened[0, 0]!.ReadNbt()));
        NbtAssert.Equal(Tiny(8, 8), NbtAssert.Ok(reopened[1, 0]!.ReadNbt()));
        using var backup = NbtAssert.Ok(RegionFile.Open(path + ".bak"));
        NbtAssert.Equal(Tiny(0, 0), NbtAssert.Ok(backup[0, 0]!.ReadNbt()));
    }

    [Fact]
    public void RegionWrite_ExternalChunk_BacksUpTheMccAndReplacesItAtomically()
    {
        using var d = new TempDir();
        var path = d.File("r.0.0.mca");
        RegionWriter.Write(path, [new ChunkSpec(0, 0, Tiny(0, 0), External: true)]);
        var mcc = d.File("c.0.0.mcc");
        var mccBefore = File.ReadAllBytes(mcc);

        using (var region = NbtAssert.Ok(RegionFile.Open(path)))
        {
            NbtAssert.Ok(region[0, 0]!.ReadNbt());   // reading sets the external state
            NbtAssert.Ok(region.WriteChunk(0, 0, Tiny(5, 5)));
        }

        Assert.Equal(mccBefore, File.ReadAllBytes(mcc + ".bak"));
        Assert.True(File.Exists(path + ".bak"));
        Assert.False(File.Exists(mcc + SafeFile.TempSuffix));
        using var reopened = NbtAssert.Ok(RegionFile.Open(path));
        NbtAssert.Equal(Tiny(5, 5), NbtAssert.Ok(reopened[0, 0]!.ReadNbt()));
    }
}
