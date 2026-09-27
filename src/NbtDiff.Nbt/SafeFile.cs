namespace NbtDiff.Nbt;

/// <summary>
/// Crash-safe file writes. A whole-file write goes to a temporary sibling, is flushed to disk, and
/// then renamed over the target, so the target is always either the old or the new content; a
/// failure removes the temporary and leaves the original untouched.
/// </summary>
internal static class SafeFile
{
    public const string TempSuffix = ".nbtdiff-tmp";
    public const string BackupSuffix = ".bak";

    public static void WriteAtomic(string path, Action<Stream> write)
    {
        string temp = path + TempSuffix;
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            // A rename creates a new inode on Unix; carry the original's permissions over to it.
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Copies <paramref name="path"/> to <c>&lt;path&gt;.bak</c> unless that backup already exists,
    /// so the backup is always the file as it was before nbt-diff first changed it. Reads with
    /// sharing, because a region file is held open by the view that is saving into it.
    /// </summary>
    public static void BackupOnce(string path)
    {
        string backup = path + BackupSuffix;
        if (!File.Exists(path) || File.Exists(backup)) return;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        WriteAtomic(backup, source.CopyTo);
    }
}
