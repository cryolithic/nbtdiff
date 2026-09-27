using fNbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

/// <summary>Issue #4: saving an SNBT file changes only the text of what changed, in the file's own dialect.</summary>
public class SnbtPatcherTests
{
    private const string Ftb =
        "# FTB Quests settings\n" +
        "{\n" +
        "\tbeta: 1b\n" +
        "\t# the chapter icon\n" +
        "\ticon: {\n" +
        "\t\tid: \"ftbquests:book\"\n" +
        "\t}\n" +
        "\tid: \"5B00676D79306EA2\"\n" +
        "\tquests: [\n" +
        "\t\t{ x: 1.0d, y: 2.0d }\n" +
        "\t\t{ x: 3.0d, y: 4.0d }\n" +
        "\t]\n" +
        "\ttags: [ ]\n" +
        "\tzoom: 5.0d\n" +
        "}\n";

    private static NbtCompound Parse(string text) => (NbtCompound)SnbtParser.Parse(text);

    private static string Edit(string text, Action<NbtCompound> change)
    {
        var tree = Parse(text);
        change(tree);
        return SnbtPatcher.Patch(text, tree);
    }

    [Theory]
    [InlineData(Ftb)]
    [InlineData("{a:1,b:[1,2,3],c:{d:\"x\"},e:[I;1,2]}")]
    [InlineData("{\r\n  a: 1,\r\n  b: \"two\"\r\n}\r\n")]
    [InlineData("# only a header\n{ }\n")]
    public void NoEdit_IsByteIdentical(string text) =>
        Assert.Equal(text, SnbtPatcher.Patch(text, Parse(text)));

    [Fact]
    public void ChangedValue_ReplacesOnlyThatToken_KeepingComments()
    {
        var result = Edit(Ftb, t => t.Get<NbtDouble>("zoom")!.Value = 7.5);
        Assert.Equal(Ftb.Replace("zoom: 5.0d", "zoom: 7.5d"), result);
    }

    [Fact]
    public void ChangedValue_InsideInlineCompoundInList()
    {
        var result = Edit(Ftb, t => ((NbtCompound)t.Get<NbtList>("quests")![1]).Get<NbtDouble>("y")!.Value = 9);
        Assert.Equal(Ftb.Replace("y: 4.0d", "y: 9.0d"), result);
    }

    [Fact]
    public void RemovedEntry_TakesItsLine()
    {
        var result = Edit(Ftb, t => t.Remove("beta"));
        Assert.Equal(Ftb.Replace("\tbeta: 1b\n", ""), result);
    }

    [Fact]
    public void RemovedEntry_CommaDialect_TakesItsComma()
    {
        Assert.Equal("{a:1,c:3}", Edit("{a:1,b:2,c:3}", t => t.Remove("b")));
        Assert.Equal("{a:1,b:2}", Edit("{a:1,b:2,c:3}", t => t.Remove("c")));
        Assert.Equal("{\n  a: 1\n}", Edit("{\n  a: 1,\n  b: 2\n}", t => t.Remove("b")));
    }

    [Fact]
    public void AddedEntry_SortedFtbCompound_GoesInKeyOrder_InTheFilesStyle()
    {
        var result = Edit(Ftb, t => t.Add(new NbtString("group", "main")));
        Assert.Equal(Ftb.Replace("\t# the chapter icon\n", "\tgroup: \"main\"\n\t# the chapter icon\n"), result);
    }

    [Fact]
    public void AddedContainer_IsWrittenMultiLineWithTabs_NoCommas()
    {
        var result = Edit(Ftb, t => t.Add(new NbtCompound("rewards") { new NbtString("type", "xp"), new NbtInt("xp", 10) }));
        Assert.Equal(Ftb.Replace("\ttags: [ ]", "\trewards: {\n\t\ttype: \"xp\"\n\t\txp: 10\n\t}\n\ttags: [ ]"), result);
    }

    [Fact]
    public void AddedEntry_CommaDialect_OneLine()
    {
        Assert.Equal("{a:1,b:2,c:3}", Edit("{a:1,b:2}", t => t.Add(new NbtInt("c", 3))));
    }

    [Fact]
    public void AddedEntry_UnsortedCompound_Appends()
    {
        Assert.Equal("{\n\tz: 1\n\ta: 2\n\tm: 3\n}", Edit("{\n\tz: 1\n\ta: 2\n}", t => t.Add(new NbtInt("m", 3))));
    }

    [Fact]
    public void EmptyContainer_GainsItsFirstEntry()
    {
        var result = Edit(Ftb, t => t.Get<NbtList>("tags")!.Add(new NbtString("new")));
        Assert.Equal(Ftb.Replace("\ttags: [ ]\n", "\ttags: [\n\t\t\"new\"\n\t]\n"), result);
    }

    [Fact]
    public void ListGrowsAndShrinks_AtTheEnd()
    {
        var grown = Edit(Ftb, t => t.Get<NbtList>("quests")!.Add(new NbtCompound { new NbtDouble("x", 5), new NbtDouble("y", 6) }));
        Assert.Equal(Ftb.Replace("\t\t{ x: 3.0d, y: 4.0d }\n", "\t\t{ x: 3.0d, y: 4.0d }\n\t\t{\n\t\t\tx: 5.0d\n\t\t\ty: 6.0d\n\t\t}\n"), grown);
        var shrunk = Edit(Ftb, t => t.Get<NbtList>("quests")!.RemoveAt(1));
        Assert.Equal(Ftb.Replace("\t\t{ x: 3.0d, y: 4.0d }\n", ""), shrunk);
    }

    [Fact]
    public void CrlfFile_KeepsCrlfInNewText()
    {
        var crlf = Ftb.Replace("\n", "\r\n");
        var result = Edit(crlf, t => t.Add(new NbtCompound("rewards") { new NbtInt("xp", 10) }));
        Assert.Equal(crlf.Replace("\ttags: [ ]", "\trewards: {\r\n\t\txp: 10\r\n\t}\r\n\ttags: [ ]"), result);
    }

    [Fact]
    public void TypeChange_ReplacesTheValue()
    {
        Assert.Equal("{a:\"text\",b:2}", Edit("{a:1,b:2}", t => { t.Remove("a"); t.Add(new NbtString("a", "text")); }));
    }

    [Theory]
    [InlineData("5", "5.0")]
    [InlineData("0.1", "0.1")]
    [InlineData("-2.5", "-2.5")]
    [InlineData("0.001", "0.001")]
    [InlineData("0.0001", "1.0E-4")]
    [InlineData("1.5E-05", "1.5E-5")]
    [InlineData("1234567", "1234567.0")]
    [InlineData("10000000", "1.0E7")]
    [InlineData("1E+16", "1.0E16")]
    [InlineData("12345678.9", "1.23456789E7")]
    [InlineData("0", "0.0")]
    [InlineData("-0", "-0.0")]
    public void JavaNumber_MatchesJavasToString(string dotnetShortest, string java) =>
        Assert.Equal(java, SnbtWriter.JavaNumber(dotnetShortest));

    // ── NbtDocument.Save ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void DocumentSave_PatchesTheFile_KeepingBomLineEndingsAndComments(bool bom, string newline)
    {
        using var d = new TempDir();
        var path = d.File("chapter.snbt");
        var text = Ftb.Replace("\n", newline);
        byte[] prefix = bom ? [0xEF, 0xBB, 0xBF] : [];
        File.WriteAllBytes(path, [.. prefix, .. System.Text.Encoding.UTF8.GetBytes(text)]);

        var doc = NbtAssert.Ok(NbtDocument.Load(path));
        NbtAssert.Ok(doc.Save(doc.Root));
        Assert.Equal([.. prefix, .. System.Text.Encoding.UTF8.GetBytes(text)], File.ReadAllBytes(path));

        doc.Root.Get<NbtDouble>("zoom")!.Value = 7.5;
        NbtAssert.Ok(doc.Save(doc.Root));
        doc.Root.Get<NbtByte>("beta")!.Value = 0;
        NbtAssert.Ok(doc.Save(doc.Root));   // a second save patches the text the first one wrote
        var expected = text.Replace("zoom: 5.0d", "zoom: 7.5d").Replace("beta: 1b", "beta: 0b");
        Assert.Equal([.. prefix, .. System.Text.Encoding.UTF8.GetBytes(expected)], File.ReadAllBytes(path));
    }

    // ── Real files ──────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> RealSnbtFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(RealDataRoot(), "*.snbt", SearchOption.AllDirectories).Order())
            data.Add(Path.GetRelativePath(RealDataRoot(), f));
        return data;
    }

    private static string RealDataRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "data", "real"));

    [Theory]
    [MemberData(nameof(RealSnbtFiles))]
    public void RealFile_NoEdit_IsByteIdentical(string file)
    {
        var text = File.ReadAllText(Path.Combine(RealDataRoot(), file));
        Assert.Equal(text, SnbtPatcher.Patch(text, SnbtParser.Parse(text)));
    }

    [Fact]
    public void RealQuestProgress_PatchedToExpected_KeepsEveryUntouchedLine()
    {
        // FTB writes this file from hash maps, so its key order changes between saves and its exact
        // bytes cannot be reproduced. What must hold: the content becomes the expected content, and
        // every entry that exists on both sides keeps its original line, in its original order.
        var dir = Path.Combine(RealDataRoot(), "neoforge");
        var name = Path.Combine("ftbquests", "11111111-2222-4333-8444-555555555555.snbt");
        var initial = File.ReadAllText(Path.Combine(dir, "initial", name));
        var expectedTree = SnbtParser.Parse(File.ReadAllText(Path.Combine(dir, "expected", name)));

        var result = SnbtPatcher.Patch(initial, expectedTree);

        Assert.True(SnbtPatcher.TagEquals(SnbtParser.Parse(result), expectedTree));
        var resultLines = result.Split('\n');
        var surviving = LeafLines((NbtCompound)SnbtParser.Parse(initial), expectedTree).ToList();
        Assert.NotEmpty(surviving);
        var positions = surviving.Select(l => Array.IndexOf(resultLines, l)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);   // untouched entries keep their original order
    }

    /// <summary>Lines of <c>key: value</c> entries under task_progress that are identical on both sides.</summary>
    private static IEnumerable<string> LeafLines(NbtCompound initial, NbtTag expected)
    {
        var before = initial.Get<NbtCompound>("task_progress")!;
        var after = ((NbtCompound)expected).Get<NbtCompound>("task_progress")!;
        foreach (var tag in before.Tags)
            if (after.Get(tag.Name!) is { } now && SnbtPatcher.TagEquals(tag, now))
                yield return $"\t\t{tag.Name}: {SnbtWriter.WriteValue(tag)}";
    }

    [Fact]
    public void Corpus_NoEdit_IsByteIdentical_AndOneValueEditTouchesOneLine()
    {
        // NBTDIFF_SNBT_CORPUS=<dir> runs this over a modpack's SNBT (e.g. an instance's config/ftbquests).
        var corpus = Environment.GetEnvironmentVariable("NBTDIFF_SNBT_CORPUS");
        if (string.IsNullOrEmpty(corpus)) return;
        int files = 0;
        foreach (var file in Directory.EnumerateFiles(corpus, "*.snbt", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!SnbtParser.TryParse(text, out var tree, out _)) continue;   // comment-only etc.
            files++;
            Assert.True(text == SnbtPatcher.Patch(text, tree!), $"no-edit changed {file}");

            var scalar = FirstScalar(tree!);
            if (scalar is null) continue;
            Bump(scalar);
            var edited = SnbtPatcher.Patch(text, tree!);
            var before = text.Split('\n');
            var after = edited.Split('\n');
            Assert.True(before.Length == after.Length, $"line count changed in {file}");
            Assert.True(1 == before.Zip(after).Count(p => p.First != p.Second), $"more than one line changed in {file}");
        }
        Assert.True(files > 0, "corpus had no parseable .snbt files");
    }

    private static NbtTag? FirstScalar(NbtTag tag) => tag switch
    {
        NbtCompound c => c.Tags.Select(FirstScalar).FirstOrDefault(t => t is not null),
        NbtList l => l.Select(FirstScalar).FirstOrDefault(t => t is not null),
        NbtString or NbtInt or NbtLong or NbtDouble or NbtByte => tag,
        _ => null,
    };

    private static void Bump(NbtTag tag)
    {
        switch (tag)
        {
            case NbtString s: s.Value += "!"; break;
            case NbtInt i: i.Value++; break;
            case NbtLong l: l.Value++; break;
            case NbtDouble d: d.Value += 1; break;
            case NbtByte b: b.Value = (byte)(b.Value == 0 ? 1 : 0); break;
        }
    }
}
