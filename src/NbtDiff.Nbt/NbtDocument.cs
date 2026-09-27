using System.Text;
using fNbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Nbt;

/// <summary>A parsed standalone NBT file (level.dat, player data, .snbt, ...). Immutable after load.</summary>
public sealed class NbtDocument
{
    private const int BedrockHeaderSize = 8;

    public string Path { get; }
    public NbtCompound Root { get; }
    public NbtFormatInfo Format { get; }

    private NbtDocument(string path, NbtCompound root, NbtFormatInfo format)
    {
        Path = path;
        Root = root;
        Format = format;
    }

    public static LoadResult<NbtDocument> Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream, path);
        }
        catch (Exception e)
        {
            return LoadResult<NbtDocument>.Fail("Open file", e);
        }
    }

    /// <summary>
    /// Tries, in order: SNBT, Java NBT, Bedrock NBT, Bedrock level.dat. The first parse that is not
    /// suspicious wins; if all are suspicious, the first successful one; if all fail, an aggregate
    /// failure listing every attempt. "Suspicious" catches a file that happens to decode under the
    /// wrong endianness: an empty root, or names/strings with control characters.
    /// </summary>
    public static LoadResult<NbtDocument> Load(Stream stream, string displayPath)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(stream));

        var attempts = new List<(LoadResult<NbtDocument> result, bool suspicious)>();
        foreach (var strategy in Strategies)
        {
            stream.Position = 0;
            var result = strategy(stream, displayPath);
            bool suspicious = result.Ok && LooksSuspicious(result.Value!.Root);
            if (result.Ok && !suspicious)
                return result;
            attempts.Add((result, suspicious));
        }

        foreach (var (result, _) in attempts)
            if (result.Ok) return result;

        return LoadResult<NbtDocument>.Fail(LoadFailure.Aggregate(
            "Not readable as SNBT, Java NBT, or Bedrock NBT",
            attempts.Select(a => a.result.Failure!)));
    }

    private static readonly Func<Stream, string, LoadResult<NbtDocument>>[] Strategies =
    [
        LoadSnbt,
        (s, p) => LoadBinary(s, p, NbtFormat.JavaNbt, bigEndian: true, skipHeader: false),
        (s, p) => LoadBinary(s, p, NbtFormat.BedrockNbt, bigEndian: false, skipHeader: false),
        (s, p) => LoadBinary(s, p, NbtFormat.BedrockLevelDat, bigEndian: false, skipHeader: true),
    ];

    private static LoadResult<NbtDocument> LoadSnbt(Stream stream, string path) =>
        LoadResult<NbtDocument>.Try("Load as SNBT", () =>
        {
            // Cheap rejection before reading a possibly huge binary file as text.
            int first = stream.ReadByte();
            if (first == 0xEF) // UTF-8 BOM
            {
                stream.ReadByte(); stream.ReadByte();
                first = stream.ReadByte();
            }
            while (true)
            {
                if (first is ' ' or '\t' or '\r' or '\n') { first = stream.ReadByte(); continue; }
                if (first == '#')   // FTB config files open with '#' comment lines
                {
                    do first = stream.ReadByte(); while (first is not (-1 or '\n'));
                    continue;
                }
                break;
            }
            if (first != '{')
                throw new FormatException("Text does not start with '{' (after optional whitespace and # comments)");

            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var tag = SnbtParser.Parse(reader.ReadToEnd());
            if (tag is not NbtCompound root)
                throw new FormatException($"SNBT root is a {tag.TagType}, not a compound");
            root.Name = "";
            return new NbtDocument(path, root, NbtFormatInfo.Snbt);
        });

    private static LoadResult<NbtDocument> LoadBinary(Stream stream, string path, NbtFormat format, bool bigEndian, bool skipHeader) =>
        LoadResult<NbtDocument>.Try($"Load as {format} ({(bigEndian ? "big" : "little")}-endian{(skipHeader ? ", 8-byte header" : "")})", () =>
        {
            if (skipHeader)
            {
                Span<byte> header = stackalloc byte[BedrockHeaderSize];
                stream.ReadExactly(header);
            }
            var file = new NbtFile { BigEndian = bigEndian };
            file.LoadFromStream(stream, NbtCompression.AutoDetect);
            if (file.RootTag is not NbtCompound root)
                throw new FormatException($"Root tag is a {file.RootTag?.TagType}, not a compound");
            return new NbtDocument(path, root, new NbtFormatInfo(format, file.FileCompression, bigEndian));
        });

    /// <summary>
    /// Writes <paramref name="root"/> back to <see cref="Path"/> in the format and compression the
    /// file was loaded with (SNBT files are rewritten pretty-printed). The first save of a path
    /// copies the pre-nbtdiff file to <c>&lt;path&gt;.bak</c>; later saves leave that backup alone.
    /// The file is replaced atomically, so a failed save leaves it as it was.
    /// Bedrock variants are refused rather than risk corrupting their headers.
    /// </summary>
    public LoadResult<NbtDocument> Save(NbtCompound root)
    {
        if (Format.Format is not (NbtFormat.Snbt or NbtFormat.JavaNbt))
            return LoadResult<NbtDocument>.Fail($"Saving {Format.Format} files is not supported yet; write the change by hand instead");
        return LoadResult<NbtDocument>.Try($"Save {Path}", () =>
        {
            SafeFile.BackupOnce(Path);
            if (Format.Format == NbtFormat.Snbt)
            {
                var text = System.Text.Encoding.UTF8.GetBytes(SnbtWriter.Write(root, SnbtOptions.Pretty));
                SafeFile.WriteAtomic(Path, s => s.Write(text));
            }
            else
            {
                var file = new NbtFile(root) { BigEndian = Format.BigEndian };
                SafeFile.WriteAtomic(Path, s => file.SaveToStream(s, Format.Compression));
            }
            return this;
        });
    }

    private static bool LooksSuspicious(NbtCompound root)
    {
        if (root.Count == 0) return true;
        if (HasControlChars(root.Name)) return true;
        foreach (var tag in root.Tags)
        {
            if (HasControlChars(tag.Name)) return true;
            if (tag is NbtString s && HasControlChars(s.Value)) return true;
        }
        return false;
    }

    private static bool HasControlChars(string? s)
    {
        if (s is null) return false;
        foreach (char c in s)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r')) return true;
            if (c == '�') return true;
        }
        return false;
    }
}
