using fNbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

public class SnbtWriterTests
{
    private static NbtCompound Small() => new("")
    {
        new NbtString("name", "x"),
        new NbtInt("n", 5),
        new NbtByte("flag", 1),
        new NbtShort("s", -2),
        new NbtLong("big", 1L << 40),
        new NbtFloat("f", 1.5f),
        new NbtDouble("d", 0.1),
        new NbtList("list", NbtTagType.Byte) { new NbtByte(1), new NbtByte(2) },
        new NbtByteArray("bytes", [1, 0xFF]),
        new NbtIntArray("ints", [1, 2]),
        new NbtLongArray("longs", [3]),
        new NbtCompound("empty"),
        new NbtList("emptyList"),
    };

    [Fact]
    public void Compact()
    {
        Assert.Equal(
            "{name:\"x\",n:5,flag:1b,s:-2s,big:1099511627776L,f:1.5f,d:0.1d,list:[1b,2b],bytes:[B;1b,-1b],ints:[I;1,2],longs:[L;3L],empty:{},emptyList:[]}",
            SnbtWriter.Write(Small()));
    }

    [Fact]
    public void Pretty()
    {
        var c = new NbtCompound("")
        {
            new NbtInt("a", 1),
            new NbtCompound("b") { new NbtList("l", NbtTagType.Int) { new NbtInt(1), new NbtInt(2) }, new NbtIntArray("arr", [1, 2, 3]) },
            new NbtCompound("empty"),
        };
        const string expected = """
            {
              a: 1,
              b: {
                l: [
                  1,
                  2
                ],
                arr: [I;1,2,3]
              },
              empty: {}
            }
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), SnbtWriter.Write(c, SnbtOptions.Pretty));
    }

    [Theory]
    [InlineData("a b", "{\"a b\":1}")]
    [InlineData("", "{\"\":1}")]
    [InlineData("has:colon", "{\"has:colon\":1}")]
    [InlineData("q\"uote", "{\"q\\\"uote\":1}")]
    [InlineData("safe_key-1.2+", "{safe_key-1.2+:1}")]
    public void Keys_QuotedOnlyWhenNeeded(string key, string expected)
    {
        Assert.Equal(expected, SnbtWriter.Write(new NbtCompound("") { new NbtInt(key, 1) }));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("line\nbreak\ttab", "\"line\\nbreak\\ttab\"")]
    [InlineData("it's", "\"it's\"")]
    public void Strings_Escaped(string value, string expected)
    {
        Assert.Equal(expected, SnbtWriter.Write(new NbtString(value)));
    }

    [Fact]
    public void Floats_RoundTripExactly()
    {
        float f = 0.1f + 0.2f;
        double d = Math.PI;
        var parsedF = (NbtFloat)SnbtParser.Parse(SnbtWriter.Write(new NbtFloat(f)));
        var parsedD = (NbtDouble)SnbtParser.Parse(SnbtWriter.Write(new NbtDouble(d)));
        Assert.Equal(f, parsedF.Value);
        Assert.Equal(d, parsedD.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void RoundTrip_Sample(int seed)
    {
        var original = NbtFixtures.SampleCompound(seed);
        foreach (var options in new[] { SnbtOptions.Compact, SnbtOptions.Pretty })
        {
            var text = SnbtWriter.Write(original, options);
            var parsed = SnbtParser.Parse(text);
            parsed.Name = "";
            NbtAssert.Equal(original, parsed);
        }
    }

    [Fact]
    public void RoundTrip_Chunk()
    {
        var chunk = WorldBuilder.MakeChunk(seed: 7, cx: 3, cz: -4);
        var parsed = SnbtParser.Parse(SnbtWriter.Write(chunk, SnbtOptions.Pretty));
        parsed.Name = "";
        NbtAssert.Equal(chunk, parsed);
    }
}
