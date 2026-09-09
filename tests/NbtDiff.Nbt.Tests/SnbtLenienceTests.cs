using fNbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Nbt.Tests;

/// <summary>Dialect features seen in real FTB files that strict Minecraft SNBT does not have.</summary>
public class SnbtLenienceTests
{
    [Fact]
    public void HashComments_AreSkipped()
    {
        const string text = """
            # Server-specific configuration
            # second line

            {
                misc: {
                    # These tags will be considered the same block
                    merge_tags: ["minecraft:logs"]   # trailing comment
                    enabled: true
                }
                color: "#FF0000"
            }
            """;
        var c = (NbtCompound)SnbtParser.Parse(text);
        Assert.Equal(["minecraft:logs"], c.Get<NbtCompound>("misc")!.Get<NbtList>("merge_tags")!.Select(t => ((NbtString)t).Value));
        Assert.Equal(1, c.Get<NbtCompound>("misc")!.Get<NbtByte>("enabled")!.Value);
        Assert.Equal("#FF0000", c.Get<NbtString>("color")!.Value);   // '#' inside a string is not a comment
    }

    [Fact]
    public void UnescapedQuoteInsideText_IsLiteral()
    {
        // From a hand-translated FTB lang file: ... de color más "alto", la Tiza Azul.
        const string text = """
            {
                a: ["la Tiza de color más "alto", la Tiza Azul."]
                b: "say "hi" now"
                c: "plain"
            }
            """;
        var c = (NbtCompound)SnbtParser.Parse(text);
        Assert.Equal("la Tiza de color más \"alto\", la Tiza Azul.", ((NbtString)c.Get<NbtList>("a")![0]).Value);
        Assert.Equal("say \"hi\" now", c.Get<NbtString>("b")!.Value);
        Assert.Equal("plain", c.Get<NbtString>("c")!.Value);
    }

    [Fact]
    public void UnquotedKeys_MayContainNonAscii()
    {
        // From a translated FTB lang file: Cyrillic pasted into the middle of an id-based key.
        var c = (NbtCompound)SnbtParser.Parse("{\n\tchapter.48Основные410B67C276ED1F.title: \"The Bumblezone\"\n\tchapter.4B.title: \"x\"\n}");
        Assert.Equal(["chapter.48Основные410B67C276ED1F.title", "chapter.4B.title"], c.Names);
        Assert.Equal("The Bumblezone", ((NbtString)c.Tags.First()).Value);
    }

    [Fact]
    public void Document_CommentBeforeRoot_Loads()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("# Server config\n# second line\n\n{\n\tmisc: { enabled: true }\n}\n");
        var doc = NbtDocument.Load(new MemoryStream(bytes), "ftbultimine-server.snbt").ValueOrThrow();
        Assert.Equal(NbtFormat.Snbt, doc.Format.Format);
        Assert.Equal(1, doc.Root.Get<NbtCompound>("misc")!.Get<NbtByte>("enabled")!.Value);
    }

    [Fact]
    public void QuoteBeforeComma_EndsStringOnlyWhenAnEntryFollows()
    {
        // Single-line strict SNBT keeps working: the quote before ',' is followed by a key / a quoted value.
        var c = (NbtCompound)SnbtParser.Parse("{a:\"x\",b:\"y\",l:[\"p\",\"q\",r]}");
        Assert.Equal("x", c.Get<NbtString>("a")!.Value);
        Assert.Equal(["p", "q", "r"], c.Get<NbtList>("l")!.Select(t => ((NbtString)t).Value));
    }

    [Fact]
    public void StrictInput_Unchanged()
    {
        // Valid SNBT never has a quote in a position the lenience would reinterpret.
        var c = (NbtCompound)SnbtParser.Parse("{a:\"x\",b:\"y\",\"k\":[\"p\",\"q\"],e:\"\"}");
        Assert.Equal(["a", "b", "k", "e"], c.Names);
        Assert.Equal("", c.Get<NbtString>("e")!.Value);
        Assert.Throws<SnbtParseException>(() => SnbtParser.Parse("\"unterminated"));
    }
}
