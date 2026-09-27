using System.Diagnostics;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

public class WorldLockTests
{
    private static string MakeWorld(TempDir d)
    {
        File.WriteAllBytes(d.File("level.dat"), [0]);
        Directory.CreateDirectory(d.File("DIM-1/region"));
        File.WriteAllBytes(d.File("DIM-1/region/r.0.0.mca"), []);
        File.WriteAllText(d.File("session.lock"), "☃");
        return d.Path;
    }

    [Fact]
    public void FindWorldRoot_WalksUpToTheFolderWithLevelDat()
    {
        using var d = new TempDir();
        var root = MakeWorld(d);
        Assert.Equal(Path.GetFullPath(root), WorldLock.FindWorldRoot(d.File("DIM-1/region/r.0.0.mca")));
        Assert.Equal(Path.GetFullPath(root), WorldLock.FindWorldRoot(d.File("level.dat")));
    }

    [Fact]
    public void FindWorldRoot_LooseFile_IsNull()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("quests.snbt"), "{}");
        Assert.Null(WorldLock.FindWorldRoot(d.File("quests.snbt")));
    }

    [Fact]
    public void IsOpen_NoSessionLock_IsFalse()
    {
        using var d = new TempDir();
        MakeWorld(d);
        File.Delete(d.File("session.lock"));
        Assert.False(WorldLock.IsOpen(d.Path));
    }

    [Fact]
    public void IsOpen_UnlockedSessionLock_IsFalse_AndLeavesItUnlocked()
    {
        using var d = new TempDir();
        MakeWorld(d);
        Assert.False(WorldLock.IsOpen(d.Path));
        Assert.False(WorldLock.IsOpen(d.Path)); // the probe released its own lock
        Assert.Equal("☃", File.ReadAllText(d.File("session.lock")));
    }

    [Fact]
    public void IsOpen_LockHeldByAnotherProcessOrHandle_IsTrue()
    {
        using var d = new TempDir();
        MakeWorld(d);
        using (HoldLock(d.File("session.lock")))
            Assert.True(WorldLock.IsOpen(d.Path));
        Assert.False(WorldLock.IsOpen(d.Path));
    }

    [Fact]
    public void FindOpenWorld_ReportsTheWorldOfALockedFile_OnlyWhileLocked()
    {
        using var d = new TempDir();
        MakeWorld(d);
        var region = d.File("DIM-1/region/r.0.0.mca");
        Assert.Null(WorldLock.FindOpenWorld(region));
        using (HoldLock(d.File("session.lock")))
            Assert.Equal(Path.GetFullPath(d.Path), WorldLock.FindOpenWorld(region));
    }

    /// <summary>
    /// Holds the lock the way Minecraft does (Java's FileChannel.tryLock): LockFileEx on Windows,
    /// a POSIX fcntl lock on Linux. POSIX locks never conflict within one process, so on Linux the
    /// holder is a separate python3 process using fcntl.lockf.
    /// </summary>
    private static IDisposable HoldLock(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            fs.Lock(0, long.MaxValue);
            return fs;
        }
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import fcntl,sys\nf=open(sys.argv[1],'r+')\nfcntl.lockf(f,fcntl.LOCK_EX)\nprint('locked',flush=True)\nsys.stdin.read()");
        psi.ArgumentList.Add(path);
        var p = Process.Start(psi)!;
        Assert.Equal("locked", p.StandardOutput.ReadLine());
        return new ChildLock(p);
    }

    private sealed class ChildLock(Process p) : IDisposable
    {
        public void Dispose()
        {
            p.StandardInput.Close();
            p.WaitForExit(10_000);
            p.Dispose();
        }
    }
}
