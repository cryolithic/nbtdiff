using System.Buffers.Binary;
using System.IO;
using System.Text;
using fNbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

/// <summary>
/// Closes the remaining coverage gaps in the NBT layer: aggregate load failures, suspicious-parse
/// recovery, region open/write error paths, and the SNBT parser/writer branches the main suites
/// leave untouched.
/// </summary>
public class NbtLayerGapTests
{
    private static NbtCompound Tiny(int x, int z) => new("") { new NbtInt("x", x), new NbtInt("z", z) };

    private static RegionFile OpenRegion(byte[] bytes) => NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));

    #region NbtDocument

    [Fact]
    public void Load_Garbage_FailsWithExactAggregateText()
    {
        // A single 0xFF byte defeats every strategy: not SNBT text, not a recognizable compression
        // header for the NBT readers, and too short for the Bedrock header.
        var failure = NbtAssert.Failed(NbtDocument.Load(new MemoryStream([0xFF]), "junk"));

        Assert.Equal("Not readable as SNBT, Java NBT, or Bedrock NBT", failure.Description);
        Assert.Equal(4, failure.Attempts.Count);
        Assert.Equal("Load as SNBT", failure.Attempts[0].Description);
        Assert.Equal("Load as JavaNbt (big-endian)", failure.Attempts[1].Description);
        Assert.Equal("Load as BedrockNbt (little-endian)", failure.Attempts[2].Description);
        Assert.Equal("Load as BedrockLevelDat (little-endian, 8-byte header)", failure.Attempts[3].Description);
        Assert.Equal("Text does not start with '{' (after optional whitespace and # comments)", failure.Attempts[0].Exception!.Message);
        Assert.Equal("Could not auto-detect compression format.", failure.Attempts[1].Exception!.Message);
        Assert.Contains("end of the stream", failure.Attempts[3].Exception!.Message);
    }

    [Fact]
    public void Load_AllSuspicious_FirstSuccessfulParseWins()
    {
        // "{}" parses as an empty compound, which is "suspicious" (a real NBT file never has an
        // empty root), but it is the only strategy that succeeds, so it still wins.
        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}")), "x.snbt"));
        Assert.Equal(NbtFormat.Snbt, doc.Format.Format);
        Assert.Equal("", doc.Root.Name);
        Assert.Equal(0, doc.Root.Count);
    }

    [Fact]
    public void Load_StringValueWithControlChar_IsSuspiciousButStillWins()
    {
        // A valid big-endian file whose string value holds a control character: the Java parse
        // succeeds but is suspicious, the little-endian parses fail, so the Java result wins.
        var sample = new NbtCompound("") { new NbtString("s", "\u0001") };
        var bytes = NbtFixtures.ToBytes(sample, NbtFormat.JavaNbt, NbtCompression.None);
        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(bytes), "level.dat"));

        Assert.Equal(NbtFormat.JavaNbt, doc.Format.Format);
        Assert.True(doc.Format.BigEndian);
        Assert.Equal("\u0001", doc.Root.Get<NbtString>("s")!.Value);
    }

    [Fact]
    public void LoadSnbt_SkipsLeadingCommentLines()
    {
        var text = "# FTB config comment\n{a:1}\n";
        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), "x.snbt"));
        Assert.Equal(NbtFormat.Snbt, doc.Format.Format);
        Assert.Equal(1, doc.Root.Get<NbtInt>("a")!.Value);
        Assert.Equal("", doc.Root.Name);
    }

    [Fact]
    public void LoadSnbt_CommentOnly_FailsWithExactText()
    {
        var text = "# only a comment, no data";
        var failure = NbtAssert.Failed(NbtDocument.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), "x.snbt"));
        Assert.Equal("Not readable as SNBT, Java NBT, or Bedrock NBT", failure.Description);
        Assert.Equal(4, failure.Attempts.Count);
        Assert.Equal("Text does not start with '{' (after optional whitespace and # comments)", failure.Attempts[0].Exception!.Message);
    }

    [Fact]
    public void LoadBinary_RootNotCompound_FailsPerStrategy()
    {
        // A top-level int tag (all-zero bytes parse identically in both endiannesses): the Java and
        // Bedrock strategies both parse it and both reject the non-compound root; the level.dat
        // strategy chokes on the 8-byte header; SNBT rejects the first byte.
        var bytes = new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0 };
        var failure = NbtAssert.Failed(NbtDocument.Load(new MemoryStream(bytes), "x.dat"));
        Assert.Equal(4, failure.Attempts.Count);
        Assert.Contains("Root tag is a Int, not a compound", failure.Attempts[1].Exception!.Message);
        Assert.Contains("Root tag is a Int, not a compound", failure.Attempts[2].Exception!.Message);
    }

    #endregion

    #region RegionFile

    [Fact]
    public void Open_FromStream_SucceedsAndReadsChunks()
    {
        var data = WorldBuilder.MakeChunk(1, 0, 0);
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, data), new ChunkSpec(1, 0, Tiny(1, 0))]);
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "r.0.0.mca"));
        Assert.Equal(2, region.ChunkCount);
        Assert.Equal(bytes.Length, region.Length);
        NbtAssert.Equal(data, NbtAssert.Ok(region[0, 0]!.ReadNbt()));
    }

    [Fact]
    public void Open_FromStream_TooShort_FailsWithExactText()
    {
        var failure = NbtAssert.Failed(RegionFile.Open(new MemoryStream(new byte[100]), "r.0.0.mca"));
        Assert.Equal("File is 100 bytes; a region file has at least a 8192-byte header", failure.Description);
    }

    [Fact]
    public void Open_FromStream_ReadError_DisposesTheStreamAndReportsIt()
    {
        var stream = new ThrowingStream();
        var failure = NbtAssert.Failed(RegionFile.Open(stream, "r.0.0.mca"));
        Assert.Equal("Open region file", failure.Description);
        Assert.IsAssignableFrom<IOException>(failure.Exception);
        Assert.Equal("stream boom", failure.Exception!.Message);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(ChunkRef.SchemeGZip)]
    [InlineData(ChunkRef.SchemeNone)]
    public void WriteChunk_KeepsTheExistingScheme(byte scheme)
    {
        // Pins ToCompression for the GZip and None arms: the rewritten payload is re-compressed with
        // the chunk's current scheme and reads back equal.
        using var dir = new TempDir();
        var path = dir.File("r.0.0.mca");
        var original = WorldBuilder.MakeChunk(1, 0, 0);
        RegionWriter.Write(path, [new ChunkSpec(0, 0, original, scheme)]);
        using var region = NbtAssert.Ok(RegionFile.Open(path));
        NbtAssert.Ok(region[0, 0]!.ReadNbt()); // the tag view reads before saving; that is what makes the scheme known

        var edited = (NbtCompound)original.Clone();
        edited.Add(new NbtString("edited", "by nbtdiff"));
        NbtAssert.Ok(region.WriteChunk(0, 0, edited));

        region.Dispose();
        using var reopened = NbtAssert.Ok(RegionFile.Open(path));
        var chunk = reopened[0, 0]!;
        NbtAssert.Equal(edited, NbtAssert.Ok(chunk.ReadNbt()));
        Assert.Equal(scheme, chunk.SchemeByte);
    }

    [Fact]
    public void WriteChunk_PayloadTooLargeForASectorCount_Fails()
    {
        // ~1.2 MB of incompressible data needs ~294 sectors; the sector count does not fit a byte.
        using var dir = new TempDir();
        var path = dir.File("r.0.0.mca");
        RegionWriter.Write(path, [new ChunkSpec(0, 0, Tiny(0, 0))]);
        using var region = NbtAssert.Ok(RegionFile.Open(path));

        var rng = new Random(42);
        var data = new byte[1_200_000];
        rng.NextBytes(data);
        var big = new NbtCompound("") { new NbtByteArray("data", data) };

        var failure = NbtAssert.Failed(region.WriteChunk(1, 0, big));
        Assert.IsAssignableFrom<IOException>(failure.Exception);
        Assert.Contains("too large for 255 sectors", failure.Exception!.Message);
    }

    [Fact]
    public void WriteChunk_ExternalChunkWithoutRegionCoords_Fails()
    {
        // The header marks the chunk external, but the region file name carries no region
        // coordinates, so the .mcc target cannot be located.
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0), External: true)],
            coords: new RegionCoords(0, 0), writeExternal: (_, _) => { });
        using var region = NbtAssert.Ok(RegionFile.Open(new MemoryStream(bytes), "weird-name.mca"));
        NbtAssert.Failed(region[0, 0]!.ReadNbt()); // the .mcc is missing, but the prefix read sets the external state

        var failure = NbtAssert.Failed(region.WriteChunk(0, 0, Tiny(9, 9)));
        Assert.IsAssignableFrom<IOException>(failure.Exception);
        Assert.Equal(
            "external chunk, but region file name 'weird-name.mca' has no region coordinates to locate the .mcc file",
            failure.Exception!.Message);
    }

    #endregion

    #region ChunkRef

    [Fact]
    public void ToString_ExactFormat()
    {
        using var region = OpenRegion(RegionWriter.Build([new ChunkSpec(5, 7, Tiny(5, 7))]));
        var chunk = region[5, 7]!;
        Assert.Equal(8192, chunk.Offset);
        Assert.Equal(1, chunk.SectorCount);
        Assert.Equal("chunk (5, 7) @ 8192 × 1 sectors", chunk.ToString());
    }

    [Fact]
    public void ReadPrefix_SchemeZero_Fails()
    {
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0))]);
        bytes[RegionFile.HeaderSize + 4] = 0; // valid length prefix, dead scheme byte
        using var region = OpenRegion(bytes);

        var failure = NbtAssert.Failed(region[0, 0]!.ReadCompressedPayload());
        Assert.Equal("Chunk (0, 0): read payload", failure.Description);
        Assert.IsAssignableFrom<InvalidDataException>(failure.Exception);
        Assert.Equal("compression scheme byte is 0", failure.Exception!.Message);
    }

    // ParseNbt(ChunkPayload)'s ToArray() copy branch (the non-array-backed case) is not reachable
    // through the public API: .NET 10's only public Memory/ReadOnlyMemory constructors are
    // array-backed, so MemoryMarshal.TryGetArray always succeeds for any payload a test can
    // build, and ReadCompressedPayload always hands out byte[]. The array-backed path itself is
    // pinned by ChunkPayloadTests (scratch buffer) and the tests below.

    [Fact]
    public void ParseNbt_RootNotCompound_FailsWithExactText()
    {
        // Overwrite the payload with a top-level int tag; the None scheme means no decompression.
        var bytes = RegionWriter.Build([new ChunkSpec(0, 0, Tiny(0, 0), ChunkRef.SchemeNone)]);
        var intTag = new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(RegionFile.HeaderSize), intTag.Length + 1);
        intTag.CopyTo(bytes, RegionFile.HeaderSize + ChunkRef.PrefixSize);
        using var region = OpenRegion(bytes);

        var failure = NbtAssert.Failed(region[0, 0]!.ReadNbt());
        Assert.Equal("Chunk (0, 0): root tag is Int, not a compound", failure.Description);
    }

    #endregion

    #region SnbtParser

    [Theory]
    [InlineData("[I;300s]", 300)]
    [InlineData("[L;12s]", 12)]
    public void Arrays_AcceptShortElements(string text, long value)
    {
        var tag = SnbtParser.Parse(text);
        long v = tag switch
        {
            NbtIntArray i => i.Value[0],
            NbtLongArray l => l.Value[0],
            _ => throw new Xunit.Sdk.XunitException($"unexpected tag type {tag.TagType}"),
        };
        int count = tag is NbtIntArray i2 ? i2.Value.Length : ((NbtLongArray)tag).Value.Length;
        Assert.Equal(1, count);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData("[B;300s", "Byte array element 300 out of range")]
    [InlineData("[I;2147483648L", "Int array element 2147483648 out of range")]
    [InlineData("[I;-2147483649L", "Int array element -2147483649 out of range")]
    public void Arrays_OutOfRangeElement_Fails(string text, string message)
    {
        var e = Assert.Throws<SnbtParseException>(() => SnbtParser.Parse(text));
        Assert.Equal(message, e.Message[..message.Length]);
    }

    [Theory]
    [InlineData("\"a\\rb\"", "a\rb")]
    [InlineData("\"a\\bb\"", "a\bb")]
    [InlineData("\"a\\fb\"", "a\fb")]
    [InlineData("\"a\\sb\"", "a b")]
    public void QuotedStrings_RemainingEscapes(string text, string expected)
    {
        var tag = Assert.IsType<NbtString>(SnbtParser.Parse(text));
        Assert.Equal(expected, tag.Value);
    }

    [Fact]
    public void QuotedString_UnescapedQuotesInsideText_AreLiteral()
    {
        // Hand-edited FTB lang files put unescaped quotes inside text; a closing quote only ends
        // the string when what follows can follow a string, so both inner quotes are literal.
        var tag = Assert.IsType<NbtString>(SnbtParser.Parse("\"hello \"world\" bye\""));
        Assert.Equal("hello \"world\" bye", tag.Value);
    }

    [Theory]
    [InlineData("127b", NbtTagType.Byte, "127")]
    [InlineData("-128b", NbtTagType.Byte, "-128")]
    [InlineData("32767s", NbtTagType.Short, "32767")]
    [InlineData("-32768s", NbtTagType.Short, "-32768")]
    [InlineData("2147483647", NbtTagType.Int, "2147483647")]
    [InlineData("-2147483648", NbtTagType.Int, "-2147483648")]
    [InlineData("9223372036854775807L", NbtTagType.Long, "9223372036854775807")]
    [InlineData("-9223372036854775808L", NbtTagType.Long, "-9223372036854775808")]
    public void Scalars_BoundaryValuesParseExactly(string text, NbtTagType type, string value)
    {
        var tag = SnbtParser.Parse(text);
        Assert.Equal(type, tag.TagType);
        Assert.Equal(value, tag switch
        {
            NbtByte b => ((sbyte)b.Value).ToString(),
            NbtShort s => s.Value.ToString(),
            NbtInt i => i.Value.ToString(),
            NbtLong l => l.Value.ToString(),
            _ => throw new Xunit.Sdk.XunitException($"unexpected tag type {tag.TagType}"),
        });
    }

    [Theory]
    [InlineData("128b", "Number 128b does not fit its type")]
    [InlineData("-129b", "Number -129b does not fit its type")]
    [InlineData("32768s", "Number 32768s does not fit its type")]
    [InlineData("9223372036854775808L", "Number 9223372036854775808L does not fit its type")]
    public void Scalars_Overflow_FailsWithExactText(string text, string message)
    {
        var e = Assert.Throws<SnbtParseException>(() => SnbtParser.Parse(text));
        Assert.Equal(0, e.Position);
        Assert.Equal($"{message} at position 0", e.Message);
    }

    #endregion

    #region SnbtWriter

    [Theory]
    [InlineData("byte", "1b")]
    [InlineData("negativeByte", "-1b")]
    [InlineData("short", "-2s")]
    [InlineData("int", "7")]
    [InlineData("long", "1099511627776L")]
    [InlineData("float", "1.5f")]
    [InlineData("double", "0.1d")]
    [InlineData("string", "\"x\"")]
    [InlineData("bytes", "[B;1b,-1b]")]
    [InlineData("list", "[1,2]")]
    public void WriteValue_OnlyTheValuePortion(string kind, string expected)
    {
        NbtTag tag = kind switch
        {
            "byte" => new NbtByte(1),
            "negativeByte" => new NbtByte(0xFF), // fNbt stores bytes unsigned; SNBT writes signed
            "short" => new NbtShort(-2),
            "int" => new NbtInt(7),
            "long" => new NbtLong(1L << 40),
            "float" => new NbtFloat(1.5f),
            "double" => new NbtDouble(0.1),
            "string" => new NbtString("x"),
            "bytes" => new NbtByteArray([1, 0xFF]),
            "list" => new NbtList(NbtTagType.Int) { new NbtInt(1), new NbtInt(2) },
            _ => throw new Xunit.Sdk.XunitException(kind),
        };
        Assert.Equal(expected, SnbtWriter.WriteValue(tag));
    }

    [Fact]
    public void Write_TagTypeWithoutACase_Throws()
    {
        // No fNbt class exists for TAG_End, so a synthetic tag pins the switch's default arm:
        // the writer must refuse a tag type it does not handle rather than emit wrong SNBT.
        var e = Assert.Throws<NotSupportedException>(() => SnbtWriter.WriteValue(new EndTag()));
        Assert.Equal("Cannot write tag type End", e.Message);
    }

    [Fact]
    public void WriteQuoted_CarriageReturnIsEscaped()
    {
        Assert.Equal("\"line\\rbreak\"", SnbtWriter.Write(new NbtString("line\rbreak")));
        // and the parser reads it back exactly
        Assert.Equal("line\rbreak", ((NbtString)SnbtParser.Parse("\"line\\rbreak\"")).Value);
    }

    #endregion

    /// <summary>Only tag type fNbt has no concrete class for; used to pin SnbtWriter's default arm.</summary>
    private sealed class EndTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.End;
        public override object Clone() => throw new NotSupportedException();
        internal override bool ReadTag(NbtBinaryReader readStream) => throw new NotSupportedException();
        internal override void WriteTag(NbtBinaryWriter writeStream) => throw new NotSupportedException();
        internal override void WriteData(NbtBinaryWriter writeStream) => throw new NotSupportedException();
        internal override void PrettyPrint(StringBuilder sb, string indentString, int indentLevel) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override bool CanTimeout => false;
        public override long Length => throw new IOException("stream boom");
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("stream boom");
        public override int Read(Span<byte> buffer) => throw new IOException("stream boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) => Disposed = true;
    }
}
