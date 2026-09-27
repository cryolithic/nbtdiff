#:project ../../tests/NbtDiff.TestFixtures/NbtDiff.TestFixtures.csproj
// Builds the real-world integration fixtures (tests/data/real/<set>/{initial,expected}) from two
// snapshots of a world, with the player's identity scrubbed. Usage (from the repo root):
//
//   dotnet run tools/fixtures/make-real-fixtures.cs -- <initial-dir> <expected-dir> <out-dir> \
//       <file-list> <player-uuid> <player-name>
//   dotnet run tools/fixtures/make-real-fixtures.cs -- verify <dir> <player-uuid> <player-name>
//
// <file-list> holds world-relative paths, one per line (# comments); "{uuid}" stands for the
// player's UUID in file names. Only files present in both snapshots are copied. The UUID is
// replaced in every encoding (dashed/plain hex text, its 8-hex-digit prefix as FTB team names use
// it, compound keys, [I;..] int arrays, most/least longs) and the player name in all text, then every output file is re-read and the
// run fails if any trace remains. Region files are rebuilt from their chunks, so no stale
// sectors survive; .snbt and .json are scrubbed as text, so their formatting is kept byte-exact.
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;
using NbtDiff.Nbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

if (args is ["verify", var verifyDir, var verifyUuid, var verifyName])
{
    // Scan any directory (e.g. the committed fixtures, or a raw snapshot as a negative control).
    var who = new Identity(Guid.Parse(verifyUuid), verifyName, Guid.Empty, "");
    int found = 0;
    foreach (var file in Directory.EnumerateFiles(verifyDir, "*", SearchOption.AllDirectories))
        foreach (var leak in Verifier.Leaks(file, who))
        {
            Console.WriteLine($"LEAK {Path.GetRelativePath(verifyDir, file)}: {leak}");
            found++;
        }
    Console.WriteLine($"{verifyDir}: {(found == 0 ? "no identity traces" : $"{found} leaks")}");
    return found == 0 ? 0 : 1;
}
if (args.Length != 6)
{
    Console.Error.WriteLine("usage: <initial-dir> <expected-dir> <out-dir> <file-list> <player-uuid> <player-name>\n" +
                            "       verify <dir> <player-uuid> <player-name>");
    return 2;
}
var (initialDir, expectedDir, outDir, listFile) = (args[0], args[1], args[2], args[3]);
var id = new Identity(Guid.Parse(args[4]), args[5], Guid.Parse("11111111-2222-4333-8444-555555555555"), "Player");

var paths = File.ReadAllLines(listFile)
    .Select(l => l.Split('#')[0].Trim())
    .Where(l => l.Length > 0)
    .Select(l => l.Replace("{uuid}", id.Real.ToString()))
    .ToList();

if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
int failures = 0;
foreach (var (side, dir) in new[] { ("initial", initialDir), ("expected", expectedDir) })
{
    foreach (var rel in paths)
    {
        string src = Path.Combine(dir, rel);
        if (!File.Exists(src) || !File.Exists(Path.Combine(side == "initial" ? expectedDir : initialDir, rel)))
        {
            Console.WriteLine($"skip (not in both snapshots): {rel}");
            continue;
        }
        string dst = Path.Combine(outDir, side, id.ScrubText(rel));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        Scrubber.Copy(src, dst, id);
    }
}
foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
{
    foreach (var leak in Verifier.Leaks(file, id))
    {
        Console.Error.WriteLine($"LEAK {Path.GetRelativePath(outDir, file)}: {leak}");
        failures++;
    }
}
long bytes = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
Console.WriteLine($"{outDir}: {bytes / 1024} KB, {(failures == 0 ? "no identity traces" : $"{failures} LEAKS")}");
return failures == 0 ? 0 : 1;

sealed class Identity
{
    public Guid Real { get; }
    public string Name { get; }
    public Guid Fake { get; }
    public string FakeName { get; }
    public int[] RealInts { get; }
    public int[] FakeInts { get; }
    public long RealMost { get; }
    public long RealLeast { get; }
    public long FakeMost { get; }
    public long FakeLeast { get; }
    private readonly (Regex Pattern, string Replacement)[] _text;

    public Identity(Guid real, string name, Guid fake, string fakeName)
    {
        (Real, Name, Fake, FakeName) = (real, name, fake, fakeName);
        (RealInts, RealMost, RealLeast) = Split(real);
        (FakeInts, FakeMost, FakeLeast) = Split(fake);
        string Hex(Guid g) => g.ToString("N");
        _text =
        [
            (new Regex(Regex.Escape(real.ToString("D")), RegexOptions.IgnoreCase), fake.ToString("D")),
            (new Regex(Regex.Escape(Hex(real)), RegexOptions.IgnoreCase), Hex(fake)),
            (new Regex(@"\[I;\s*" + string.Join(@"\s*,\s*", RealInts) + @"\s*\]"), "[I; " + string.Join(", ", FakeInts) + "]"),
            // FTB names a player's team "<name>#<first 8 hex digits of the UUID>": scrub that prefix wherever it stands alone.
            (new Regex(@"(?<![0-9A-Fa-f])" + Regex.Escape(Hex(real)[..8]) + @"(?![0-9A-Fa-f])", RegexOptions.IgnoreCase), Hex(fake)[..8]),
            (new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase), fakeName),
        ];
    }

    /// <summary>Minecraft's UUID layout: the 128 bits big-endian, as four ints or most/least longs.</summary>
    private static (int[] Ints, long Most, long Least) Split(Guid g)
    {
        var bytes = Convert.FromHexString(g.ToString("N"));
        var ints = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4))).ToArray();
        return (ints, BinaryPrimitives.ReadInt64BigEndian(bytes), BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8)));
    }

    public string ScrubText(string s)
    {
        foreach (var (pattern, replacement) in _text) s = pattern.Replace(s, replacement);
        return s;
    }

    /// <summary>
    /// Any known encoding, or any 8-character run of the UUID's hex digits (catches partial copies
    /// in shapes the scrubber does not know about, not just FTB's team-name prefix).
    /// </summary>
    public bool TextLeaks(string s)
    {
        if (_text.Any(t => t.Pattern.IsMatch(s))) return true;
        string hex = Real.ToString("N");
        for (int i = 0; i + 8 <= hex.Length; i++)
            if (s.Contains(hex.Substring(i, 8), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public byte[] RealBytes => Convert.FromHexString(Real.ToString("N"));
}

static class Scrubber
{
    public static void Copy(string src, string dst, Identity id)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext is ".snbt" or ".json" or ".txt")
        {
            File.WriteAllText(dst, id.ScrubText(File.ReadAllText(src)));
            return;
        }
        if (ext is ".mca" or ".mcr")
        {
            CopyRegion(src, dst, id);
            return;
        }
        var doc = NbtDocument.Load(src).ValueOrThrow();
        Scrub(doc.Root, id);
        new NbtFile(doc.Root) { BigEndian = doc.Format.BigEndian }.SaveToFile(dst, doc.Format.Compression);
    }

    private static void CopyRegion(string src, string dst, Identity id)
    {
        if (new FileInfo(src).Length == 0)
        {
            File.WriteAllBytes(dst, []);
            return;
        }
        using var region = RegionFile.Open(src).ValueOrThrow();
        var specs = new List<ChunkSpec>();
        foreach (var chunk in region.Chunks)
        {
            var root = chunk.ReadNbt().ValueOrThrow();
            if (chunk.IsExternal == true) throw new InvalidOperationException($"{src}: external chunks are not supported by this tool");
            Scrub(root, id);
            specs.Add(new ChunkSpec(chunk.X, chunk.Z, root, chunk.SchemeByte ?? ChunkRef.SchemeZLib, chunk.Timestamp));
        }
        RegionWriter.Write(dst, specs, new RegionWriteOptions(PadLastSector: false));
    }

    public static void Scrub(NbtTag tag, Identity id)
    {
        switch (tag)
        {
            case NbtString s:
                s.Value = id.ScrubText(s.Value);
                break;
            case NbtIntArray a when a.Value.SequenceEqual(id.RealInts):
                a.Value = (int[])id.FakeInts.Clone();
                break;
            case NbtLong l when l.Value == id.RealMost:
                l.Value = id.FakeMost;
                break;
            case NbtLong l when l.Value == id.RealLeast:
                l.Value = id.FakeLeast;
                break;
            case NbtCompound c:
                foreach (var child in c.Tags.ToList())
                {
                    string renamed = id.ScrubText(child.Name!);
                    if (renamed != child.Name) child.Name = renamed;
                    Scrub(child, id);
                }
                break;
            case NbtList list:
                foreach (var child in list) Scrub(child, id);
                break;
        }
    }
}

static class Verifier
{
    public static IEnumerable<string> Leaks(string file, Identity id)
    {
        var raw = File.ReadAllBytes(file);
        if (id.TextLeaks(Encoding.UTF8.GetString(raw))) yield return "identity text in raw bytes";
        if (raw.AsSpan().IndexOf(id.RealBytes) >= 0) yield return "UUID bytes in raw bytes";
        if (id.TextLeaks(Path.GetFileName(file))) yield return "identity in file name";

        string ext = Path.GetExtension(file).ToLowerInvariant();
        IEnumerable<NbtTag> roots = ext switch
        {
            ".json" or ".txt" => [],
            ".snbt" => SnbtParser.TryParse(File.ReadAllText(file), out var snbt, out _) ? [snbt!] : [], // e.g. comment-only: raw scan only
            ".mca" or ".mcr" => raw.Length == 0 ? [] : RegionRoots(file),
            _ => NbtDocument.Load(file) is { Ok: true } doc ? [doc.Value!.Root] : [], // not NBT: raw scan only
        };
        foreach (var root in roots)
            foreach (var leak in Walk(root, id, ""))
                yield return leak;
    }

    private static List<NbtTag> RegionRoots(string file)
    {
        using var region = RegionFile.Open(file).ValueOrThrow();
        return region.Chunks.Select(c => (NbtTag)c.ReadNbt().ValueOrThrow()).ToList();
    }

    private static IEnumerable<string> Walk(NbtTag tag, Identity id, string path)
    {
        string here = path.Length == 0 ? tag.Name ?? "" : $"{path}/{tag.Name}";
        if (tag.Name is { } name && id.TextLeaks(name)) yield return $"{here}: key";
        switch (tag)
        {
            case NbtString s when id.TextLeaks(s.Value):
                yield return $"{here}: string";
                break;
            case NbtIntArray a when a.Value.SequenceEqual(id.RealInts):
                yield return $"{here}: int[4] UUID";
                break;
            case NbtLong l when l.Value == id.RealMost || l.Value == id.RealLeast:
                yield return $"{here}: UUID half as long";
                break;
            case NbtByteArray b when b.Value.AsSpan().IndexOf(id.RealBytes) >= 0:
                yield return $"{here}: UUID bytes";
                break;
            case NbtCompound c:
                foreach (var child in c.Tags)
                    foreach (var leak in Walk(child, id, here)) yield return leak;
                break;
            case NbtList list:
                for (int i = 0; i < list.Count; i++)
                    foreach (var leak in Walk(list[i], id, $"{here}/[{i}]")) yield return leak;
                break;
        }
    }
}
