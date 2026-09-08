using System.Text.Json;
using System.Text.Json.Serialization;
using NbtDiff.Core;

namespace NbtDiff.App.Services;

public sealed record RecentPair(string Left, string Right)
{
    [JsonIgnore]
    public string Display => $"{Left}  ↔  {Right}";
}

public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

/// <summary>Everything persisted between runs. Plain properties so System.Text.Json round-trips it.</summary>
public sealed class AppSettings
{
    public const int MaxRecent = 10;

    public List<RecentPair> RecentPairs { get; set; } = [];
    public bool DeepVerify { get; set; } = true;
    public bool CompoundOrderMatters { get; set; }
    public bool UseKeyedAligner { get; set; }
    public List<string> ExcludeGlobs { get; set; } = ["session.lock"];
    public WindowPlacement? Window { get; set; }

    /// <summary>Puts the pair first, dropping an earlier entry for the same pair and anything past <see cref="MaxRecent"/>.</summary>
    public void AddRecent(string left, string right)
    {
        var pair = new RecentPair(left, right);
        RecentPairs.RemoveAll(p => string.Equals(p.Left, left, StringComparison.Ordinal) && string.Equals(p.Right, right, StringComparison.Ordinal));
        RecentPairs.Insert(0, pair);
        if (RecentPairs.Count > MaxRecent) RecentPairs.RemoveRange(MaxRecent, RecentPairs.Count - MaxRecent);
    }

    public CompareOptions ToCompareOptions() => new(DeepVerify, CompoundOrderMatters, ExcludeGlobs);

    /// <summary>Repairs anything a hand-edited or partial file left null or out of range.</summary>
    internal void Sanitize()
    {
        RecentPairs ??= [];
        RecentPairs.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Left) || string.IsNullOrWhiteSpace(p.Right));
        if (RecentPairs.Count > MaxRecent) RecentPairs.RemoveRange(MaxRecent, RecentPairs.Count - MaxRecent);
        ExcludeGlobs ??= ["session.lock"];
        ExcludeGlobs.RemoveAll(string.IsNullOrWhiteSpace);
        if (Window is { } w && (w.Width < 200 || w.Height < 150 || double.IsNaN(w.Width) || double.IsNaN(w.Height)))
            Window = null;
    }
}

/// <summary>JSON file in the per-user config directory. A missing or unreadable file yields defaults; saves are atomic.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nbtdiff", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings();
            settings.Sanitize();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes via a temp file and rename so a crash mid-write cannot leave a truncated file. Returns false on I/O failure.</summary>
    public bool TrySave(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The live settings object shared by the view models, plus a way to persist it.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
}

public sealed class SettingsService : ISettingsService
{
    private readonly SettingsStore? _store;

    public AppSettings Current { get; }

    /// <param name="store">Null keeps settings in memory only (tests, or a config dir that cannot be written).</param>
    public SettingsService(SettingsStore? store)
    {
        _store = store;
        Current = store?.Load() ?? new AppSettings();
    }

    public void Save() => _store?.TrySave(Current);
}
