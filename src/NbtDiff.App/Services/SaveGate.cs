using NbtDiff.Core;

namespace NbtDiff.App.Services;

/// <summary>Outcome of <see cref="SaveGate.CheckAsync"/>: go ahead, or stop (with a message to show when it was refused rather than cancelled).</summary>
public readonly record struct SaveCheck(bool Proceed, string? Error)
{
    public static readonly SaveCheck Go = new(true, null);
    public static readonly SaveCheck Cancelled = new(false, null);
    public static SaveCheck Refused(string error) => new(false, error);
}

/// <summary>
/// Runs before anything is written back to disk. Refuses when a target file belongs to a world that
/// Minecraft (or a server) has open — it would overwrite the change or corrupt the region — and asks
/// once per app session for confirmation that the user has a backup. One instance per session.
/// </summary>
public sealed class SaveGate(IDialogService dialogs, Func<string, string?>? findOpenWorld = null)
{
    private readonly Func<string, string?> _findOpenWorld = findOpenWorld ?? WorldLock.FindOpenWorld;

    public bool BackupConfirmed { get; private set; }

    public async Task<SaveCheck> CheckAsync(IReadOnlyCollection<string> targetPaths)
    {
        foreach (var path in targetPaths)
        {
            if (_findOpenWorld(path) is { } world)
                return SaveCheck.Refused(
                    $"This world is open in Minecraft or a server: {world}\n" +
                    "Close the world (or stop the server), then save again.");
        }
        if (!BackupConfirmed)
        {
            bool ok = await dialogs.ConfirmAsync("Back up before saving",
                "Saving writes your changes into the original files, in place.\n\n" +
                "Make sure you have a backup of the world or files before continuing. " +
                "You will not be asked again until nbt-diff is restarted.",
                confirmLabel: "Save");
            if (!ok) return SaveCheck.Cancelled;
            BackupConfirmed = true;
        }
        return SaveCheck.Go;
    }
}
