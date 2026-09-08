namespace NbtDiff.Core;

public enum FileKind
{
    /// <summary>Anvil / pre-Anvil region file (<c>.mca</c>, <c>.mcr</c>).</summary>
    Region,
    /// <summary>Standalone binary NBT (<c>.dat</c>, <c>.nbt</c>, schematics).</summary>
    Nbt,
    /// <summary>Stringified NBT text.</summary>
    Snbt,
    Json,
    Text,
    /// <summary>Anything else, compared byte for byte. Includes external chunk files (<c>.mcc</c>).</summary>
    Binary,
    Directory,
}

/// <summary>Maps a file name to how it is compared. Extension only — no content sniffing.</summary>
public static class FileClassifier
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mca"] = FileKind.Region,
        [".mcr"] = FileKind.Region,
        [".dat"] = FileKind.Nbt,
        [".dat_old"] = FileKind.Nbt,
        [".dat_mcr"] = FileKind.Nbt,
        [".nbt"] = FileKind.Nbt,
        [".schematic"] = FileKind.Nbt,
        [".schem"] = FileKind.Nbt,
        [".litematic"] = FileKind.Nbt,
        [".snbt"] = FileKind.Snbt,
        [".json"] = FileKind.Json,
        [".mcmeta"] = FileKind.Json,
        [".txt"] = FileKind.Text,
        [".lock"] = FileKind.Text,
        [".properties"] = FileKind.Text,
        [".log"] = FileKind.Text,
        [".toml"] = FileKind.Text,
        [".cfg"] = FileKind.Text,
        [".conf"] = FileKind.Text,
        [".yml"] = FileKind.Text,
        [".yaml"] = FileKind.Text,
        [".md"] = FileKind.Text,
        [".csv"] = FileKind.Text,
    };

    public static FileKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Binary;
    }
}
