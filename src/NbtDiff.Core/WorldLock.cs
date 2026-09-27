namespace NbtDiff.Core;

/// <summary>
/// Detects a world that Minecraft (client or server) currently has open. Minecraft holds an
/// exclusive lock on <c>session.lock</c> in the world folder for as long as the world is loaded
/// (Java's <c>FileChannel.tryLock</c>: <c>LockFileEx</c> on Windows, a POSIX <c>fcntl</c> lock on
/// Linux). Probing with <see cref="FileStream.Lock"/> uses the same mechanisms, so a held lock makes
/// the probe fail. The file merely existing means nothing: Minecraft leaves it behind.
/// </summary>
public static class WorldLock
{
    public const string LockFileName = "session.lock";

    /// <summary>The nearest folder at or above <paramref name="filePath"/>'s folder that holds a <c>level.dat</c>; null for a file outside any world.</summary>
    public static string? FindWorldRoot(string filePath)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "level.dat"))) return dir;
        }
        return null;
    }

    /// <summary>True when another process holds <c>session.lock</c> in <paramref name="worldRoot"/>.</summary>
    public static bool IsOpen(string worldRoot)
    {
        // FileStream.Lock is unsupported on macOS, which nbt-diff does not ship for.
        if (OperatingSystem.IsMacOS()) return false;
        var lockPath = Path.Combine(worldRoot, LockFileName);
        if (!File.Exists(lockPath)) return false;
        FileStream fs;
        try
        {
            fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException)
        {
            return false; // cannot probe; the save itself will report the permission problem
        }
        catch (IOException)
        {
            return true; // opened exclusively by someone else
        }
        using (fs)
        {
            try
            {
                fs.Lock(0, 1);
            }
            catch (IOException)
            {
                return true;
            }
            fs.Unlock(0, 1);
            return false;
        }
    }

    /// <summary>The world root containing <paramref name="filePath"/> if that world is open in Minecraft; otherwise null.</summary>
    public static string? FindOpenWorld(string filePath) =>
        FindWorldRoot(filePath) is { } root && IsOpen(root) ? root : null;
}
