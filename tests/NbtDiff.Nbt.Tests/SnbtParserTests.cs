using fNbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Nbt.Tests;

public class SnbtParserTests
{
    [Theory]
    [InlineData("1b", NbtTagType.Byte, "1")]
    [InlineData("-5B", NbtTagType.Byte, "-5")]
    [InlineData("true", NbtTagType.Byte, "1")]
    [InlineData("false", NbtTagType.Byte, "0")]
    [InlineData("300s", NbtTagType.Short, "300")]
    [InlineData("42", NbtTagType.Int, "42")]
    [InlineData("-42", NbtTagType.Int, "-42")]
    [InlineData("+7", NbtTagType.Int, "7")]
    [InlineData("9000000000L", NbtTagType.Long, "9000000000")]
    [InlineData("12l", NbtTagType.Long, "12")]
    [InlineData("1.5f", NbtTagType.Float, "1.5")]
    [InlineData("1.5", NbtTagType.Double, "1.5")]
    [InlineData(".5", NbtTagType.Double, "0.5")]
    [InlineData("1e3", NbtTagType.Double, "1000")]
    [InlineData("2d", NbtTagType.Double, "2")]
    [InlineData("hello", NbtTagType.String, "hello")]
    [InlineData("2147483648", NbtTagType.String, "2147483648")]             // too big for int, no suffix
    [InlineData("1.5.3", NbtTagType.String, "1.5.3")]
    [InlineData("1b2", NbtTagType.String, "1b2")]
    public void Scalars(string text, NbtTagType type, string value)
    {
        var tag = SnbtParser.Parse(text);
        Assert.Equal(type, tag.TagType);
        Assert.Equal(value, ScalarText(tag));
    }

    [Fact]
    public void ColonInUnquotedValue_IsTrailingContent()
    {
        // Minecraft also rejects this: unquoted strings stop at ':'.
        var e = Assert.Throws<SnbtParseException>(() => SnbtParser.Parse("minecraft:stone"));
        Assert.Equal(9, e.Position);
    }

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("'single'", "single")]
    [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("'it\\'s'", "it's")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    [InlineData("\"tab\\tnew\\nline\"", "tab\tnew\nline")]
    [InlineData("\"\\u0041\\u00e9\"", "Aé")]
    [InlineData("\"has 'other' quotes\"", "has 'other' quotes")]
    [InlineData("\"\"", "")]
    public void QuotedStrings(string text, string expected)
    {
        var tag = Assert.IsType<NbtString>(SnbtParser.Parse(text));
        Assert.Equal(expected, tag.Value);
    }

    [Fact]
    public void Compound_Basic()
    {
        var c = Assert.IsType<NbtCompound>(SnbtParser.Parse("{a:1,b:\"x\",c:{d:[1,2,3]}}"));
        Assert.Equal(3, c.Count);
        Assert.Equal(1, c.Get<NbtInt>("a")!.Value);
        Assert.Equal("x", c.Get<NbtString>("b")!.Value);
        var d = c.Get<NbtCompound>("c")!.Get<NbtList>("d")!;
        Assert.Equal(NbtTagType.Int, d.ListType);
        Assert.Equal(3, d.Count);
    }

    [Theory]
    [InlineData("{ a : 1 , b : 2 }")]
    [InlineData("{\n  a: 1,\n  b: 2\n}")]
    [InlineData("\t{a:1,b:2}\n")]
    public void Compound_WhitespaceAnywhere(string text)
    {
        var c = Assert.IsType<NbtCompound>(SnbtParser.Parse(text));
        Assert.Equal(["a", "b"], c.Names);
    }

    [Fact]
    public void Compound_QuotedKeys()
    {
        var c = Assert.IsType<NbtCompound>(SnbtParser.Parse("{\"a b\":1,'c:d':2,\"\":3}"));
        Assert.Equal(["a b", "c:d", ""], c.Names);
    }

    [Fact]
    public void Compound_Empty()
    {
        Assert.Empty(Assert.IsType<NbtCompound>(SnbtParser.Parse("{}")));
        Assert.Empty(Assert.IsType<NbtCompound>(SnbtParser.Parse("{ }")));
    }

    [Fact]
    public void Compound_ChildrenCarryKeyAsName()
    {
        var c = (NbtCompound)SnbtParser.Parse("{k:{}}");
        Assert.Equal("k", c["k"].Name);
        Assert.Null(c.Name);
    }

    [Fact]
    public void List_Basic()
    {
        var l = Assert.IsType<NbtList>(SnbtParser.Parse("[1,2,3]"));
        Assert.Equal(NbtTagType.Int, l.ListType);
        Assert.Equal([1, 2, 3], l.Select(t => ((NbtInt)t).Value));
        Assert.All(l, t => Assert.Null(t.Name));
    }

    [Fact]
    public void List_Empty()
    {
        Assert.Empty(Assert.IsType<NbtList>(SnbtParser.Parse("[]")));
        Assert.Empty(Assert.IsType<NbtList>(SnbtParser.Parse("[ ]")));
    }

    [Fact]
    public void List_Nested()
    {
        var l = Assert.IsType<NbtList>(SnbtParser.Parse("[[1],[2,3],[]]"));
        Assert.Equal(NbtTagType.List, l.ListType);
        Assert.Equal([1, 2, 0], l.Select(t => ((NbtList)t).Count));
    }

    [Fact]
    public void List_OfCompounds()
    {
        var l = Assert.IsType<NbtList>(SnbtParser.Parse("[{id:\"a\"},{id:\"b\"}]"));
        Assert.Equal(NbtTagType.Compound, l.ListType);
        Assert.Equal("b", ((NbtCompound)l[1]).Get<NbtString>("id")!.Value);
    }

    [Theory]
    [InlineData("[B;1b,2b,-3b]", NbtTagType.ByteArray, 3)]
    [InlineData("[B;1,2]", NbtTagType.ByteArray, 2)]        // lenient: unsuffixed ints accepted when in range
    [InlineData("[B;]", NbtTagType.ByteArray, 0)]
    [InlineData("[I;1,2,3]", NbtTagType.IntArray, 3)]
    [InlineData("[I; 1 , 2 ]", NbtTagType.IntArray, 2)]
    [InlineData("[L;1L,2l,3]", NbtTagType.LongArray, 3)]
    [InlineData("[L;]", NbtTagType.LongArray, 0)]
    public void TypedArrays(string text, NbtTagType type, int length)
    {
        var tag = SnbtParser.Parse(text);
        Assert.Equal(type, tag.TagType);
        int actual = tag switch
        {
            NbtByteArray b => b.Value.Length,
            NbtIntArray i => i.Value.Length,
            NbtLongArray l => l.Value.Length,
            _ => -1,
        };
        Assert.Equal(length, actual);
    }

    [Fact]
    public void ByteArray_NegativeValuesRoundTrip()
    {
        var b = (NbtByteArray)SnbtParser.Parse("[B;-1b,-128b,127b]");
        Assert.Equal([unchecked((byte)-1), 0x80, 0x7F], b.Value);
    }

    [Theory]
    [InlineData("{a:1", "Unexpected end of input")]
    [InlineData("{a}", "Expected ':'")]
    [InlineData("{a:1 b:2}", "Expected ',' or '}'")]
    [InlineData("[1 2]", "Expected ',' or ']'")]
    [InlineData("{a:1,a:2}", "Duplicate key")]
    [InlineData("[1,\"a\"]", "List of Int cannot contain a String")]
    [InlineData("[1,2", "Unexpected end of input")]
    [InlineData("\"abc", "Unterminated string")]
    [InlineData("\"\\q\"", "Unknown escape")]
    [InlineData("\"\\u12\"", "Truncated \\u escape")]
    [InlineData("{a:1} x", "Unexpected trailing content")]
    [InlineData("", "Unexpected end of input")]
    [InlineData("   ", "Unexpected end of input")]
    [InlineData("}", "Unexpected character")]
    [InlineData("[B;300]", "out of range")]
    [InlineData("[B;1000b]", "does not fit")]
    [InlineData("[I;1.5]", "must be an integer")]
    [InlineData("[I;\"x\"]", "must be an integer")]
    [InlineData("99999999999999999999L", "does not fit")]
    public void Errors(string text, string messageFragment)
    {
        var e = Assert.Throws<SnbtParseException>(() => SnbtParser.Parse(text));
        Assert.Contains(messageFragment, e.Message);
    }

    [Fact]
    public void Error_ReportsPosition()
    {
        var e = Assert.Throws<SnbtParseException>(() => SnbtParser.Parse("{a:1,b:[1,\"x\"]}"));
        Assert.Equal(10, e.Position);
        Assert.Contains("position 10", e.Message);
    }

    [Fact]
    public void TryParse_ReturnsErrorInsteadOfThrowing()
    {
        Assert.False(SnbtParser.TryParse("{", out var tag, out var error));
        Assert.Null(tag);
        Assert.NotNull(error);
        Assert.True(SnbtParser.TryParse("{}", out tag, out error));
        Assert.NotNull(tag);
        Assert.Null(error);
    }

    [Fact]
    public void FtbDialect_NewlinesSeparateEntries()
    {
        // FTB Quests/Teams write SNBT with no commas at all, indented, booleans and d/L suffixes.
        const string text = """
            {
                default_hide_dependency_lines: false
                filename: "chapter"
                icon: {
                    id: "modularrouters:creative_module"
                }
                images: [
                    {
                        height: 2.0d
                        rotation: -90.0d
                    }
                    {
                        height: 1.0d
                    }
                ]
                dependencies: [
                    "1A2B"
                    "3C4D"
                ]
                progress: [I;
                    1
                    2
                ]
                empty: [ ]
            }
            """;
        var c = (NbtCompound)SnbtParser.Parse(text);
        Assert.Equal(0, c.Get<NbtByte>("default_hide_dependency_lines")!.Value);
        Assert.Equal("chapter", c.Get<NbtString>("filename")!.Value);
        Assert.Equal(2, c.Get<NbtList>("images")!.Count);
        Assert.Equal(-90.0, ((NbtCompound)c.Get<NbtList>("images")![0]).Get<NbtDouble>("rotation")!.Value);
        Assert.Equal(["1A2B", "3C4D"], c.Get<NbtList>("dependencies")!.Select(t => ((NbtString)t).Value));
        Assert.Equal([1, 2], c.Get<NbtIntArray>("progress")!.Value);
        Assert.Empty(c.Get<NbtList>("empty")!);
    }

    [Theory]
    [InlineData("{a:1,}")]
    [InlineData("[1,2,]")]
    [InlineData("[I;1,2,]")]
    public void TrailingComma_Accepted(string text)
    {
        SnbtParser.Parse(text);
    }

    [Fact]
    public void RealWorldShape()
    {
        const string text = """
            {
              id: "minecraft:chest",
              x: 10, y: 64, z: -3,
              Items: [
                {Slot: 0b, id: "minecraft:diamond", Count: 3b},
                {Slot: 1b, id: "minecraft:stick", Count: 64b, tag: {display: {Name: '{"text":"Stick"}'}}}
              ],
              LootTable: "minecraft:chests/simple_dungeon",
              LootTableSeed: -8213498271234L
            }
            """;
        var c = (NbtCompound)SnbtParser.Parse(text);
        var items = c.Get<NbtList>("Items")!;
        Assert.Equal(2, items.Count);
        Assert.Equal("{\"text\":\"Stick\"}", ((NbtCompound)items[1]).Get<NbtCompound>("tag")!.Get<NbtCompound>("display")!.Get<NbtString>("Name")!.Value);
        Assert.Equal(-8213498271234L, c.Get<NbtLong>("LootTableSeed")!.Value);
    }

    private static string ScalarText(NbtTag tag) => tag switch
    {
        NbtByte b => ((sbyte)b.Value).ToString(),
        NbtShort s => s.Value.ToString(),
        NbtInt i => i.Value.ToString(),
        NbtLong l => l.Value.ToString(),
        NbtFloat f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NbtDouble d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NbtString s => s.Value,
        _ => throw new InvalidOperationException(),
    };
}
