using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

/// <summary> Pins the behavior of the vendored fNbt binary reader/writer and file API. </summary>
[Collection("fNbt statics")]
public class FnbReaderWriterTests
{
    // A document exercising every tag type.
    // Layout (big-endian): root "root" = 7 bytes; then b=5, s=6, i=8, l=12, f=8, d=12,
    // str=10, ba=12, ia=17, la=17, list=12+12, nested=9, x=8, sub=6, y=8, 3 end bytes = 172 total.
    private static NbtCompound FullTree() => new("root")
    {
        new NbtByte("b", 7),
        new NbtShort("s", -1234),
        new NbtInt("i", 42),
        new NbtLong("l", 999999),
        new NbtFloat("f", 1.5f),
        new NbtDouble("d", 2.25),
        new NbtString("str", "hi"),
        new NbtByteArray("ba", [1, 2, 3]),
        new NbtIntArray("ia", [10, 20]),
        new NbtLongArray("la", [30]),
        new NbtList("list", NbtTagType.Int) { new NbtInt(1), new NbtInt(2), new NbtInt(3) },
        new NbtCompound("nested")
        {
            new NbtInt("x", 1),
            new NbtCompound("sub") { new NbtInt("y", 2) },
        },
    };

    private static byte[] FullDocumentBytes()
    {
        return new NbtFile(FullTree()).SaveToBuffer(NbtCompression.None);
    }

    #region NbtBinaryReader / NbtBinaryWriter

    [Fact]
    public void BinaryReader_Strings()
    {
        using (var ms = new MemoryStream(new byte[] { 0x00, 0x02, 0x68, 0x69 }))
        {
            var r = new NbtBinaryReader(ms, true);
            Assert.Equal("hi", r.ReadString());
            Assert.Equal(4, ms.Position);
        }
        using (var ms = new MemoryStream(new byte[] { 0x01, 0x2C }.Concat(Enumerable.Repeat((byte)'a', 300)).ToArray()))
        {
            // length >= 64 takes the ReadBytes path
            var r = new NbtBinaryReader(ms, true);
            var s = r.ReadString();
            Assert.Equal(new string('a', 300), s);
            Assert.Equal(302, ms.Position);
        }
        using (var ms = new MemoryStream(new byte[] { 0xFF, 0xFF }))
        {
            var ex = Assert.Throws<NbtFormatException>(() => new NbtBinaryReader(ms, true).ReadString());
            Assert.Equal("Negative string length given!", ex.Message);
        }
        using (var ms = new MemoryStream(new byte[] { 0x00, 0x05, 0x61, 0x62 }))
        {
            Assert.Throws<EndOfStreamException>(() => new NbtBinaryReader(ms, true).ReadString());
        }
    }

    [Fact]
    public void BinaryReader_TagType()
    {
        using (var ms = new MemoryStream(new byte[] { 0x0A, 0x09, 0x0C }))
        {
            var r = new NbtBinaryReader(ms, true);
            Assert.Equal(NbtTagType.Compound, r.ReadTagType());
            Assert.Equal(NbtTagType.List, r.ReadTagType());
            Assert.Equal(NbtTagType.LongArray, r.ReadTagType());
        }
        using (var ms = new MemoryStream(new byte[] { 0x7F }))
        {
            var ex = Assert.Throws<NbtFormatException>(() => new NbtBinaryReader(ms, true).ReadTagType());
            Assert.Equal("NBT tag type out of range: 127", ex.Message);
        }
        using (var ms = new MemoryStream())
        {
            Assert.Throws<EndOfStreamException>(() => new NbtBinaryReader(ms, true).ReadTagType());
        }
    }

    [Fact]
    public void BinaryReader_Endianness()
    {
        using (var ms = new MemoryStream(new byte[] { 0x2A, 0x00, 0x00, 0x00 }))
        {
            Assert.Equal(42, new NbtBinaryReader(ms, false).ReadInt32());
        }
        using (var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x2A }))
        {
            Assert.Equal(42, new NbtBinaryReader(ms, true).ReadInt32());
        }
        using (var ms = new MemoryStream(new byte[] { 0xFD, 0xFF }))
        {
            Assert.Equal((short)-3, new NbtBinaryReader(ms, false).ReadInt16());
        }
        using (var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0xC0, 0x3F }))
        {
            Assert.Equal(1.5f, new NbtBinaryReader(ms, false).ReadSingle());
        }
        using (var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x40 }))
        {
            Assert.Equal(2.25, new NbtBinaryReader(ms, false).ReadDouble());
        }
    }

    [Fact]
    public void BinaryReader_Validation()
    {
        Assert.Throws<ArgumentNullException>(() => new NbtBinaryReader(null!, true));
        var ex = Assert.Throws<ArgumentException>(() => new NbtBinaryReader(new NoReadStream(), true));
        Assert.Null(ex.ParamName);
    }

    [Fact]
    public void BinaryWriter_Strings()
    {
        using (var ms = new MemoryStream())
        {
            new NbtBinaryWriter(ms, true).Write("hi");
            Assert.Equal(new byte[] { 0x00, 0x02, 0x68, 0x69 }, ms.ToArray());
        }
        using (var ms = new MemoryStream())
        {
            new NbtBinaryWriter(ms, true).Write("");
            Assert.Equal(new byte[] { 0x00, 0x00 }, ms.ToArray());
        }
        using (var ms = new MemoryStream())
        {
            new NbtBinaryWriter(ms, true).Write("é");
            Assert.Equal(new byte[] { 0x00, 0x02, 0xC3, 0xA9 }, ms.ToArray());
        }
        using (var ms = new MemoryStream())
        {
            // 300 bytes > 256-byte buffer: exercises the encoder loop
            new NbtBinaryWriter(ms, true).Write(new string('a', 300));
            var bytes = ms.ToArray();
            Assert.Equal(302, bytes.Length);
            Assert.Equal(0x01, bytes[0]);
            Assert.Equal(0x2C, bytes[1]);
            for (int i = 2; i < 302; i++)
            {
                Assert.Equal((byte)'a', bytes[i]);
            }
        }
        using (var ms = new MemoryStream())
        {
            new NbtBinaryWriter(ms, false).Write("hi");
            Assert.Equal(new byte[] { 0x02, 0x00, 0x68, 0x69 }, ms.ToArray());
        }
        Assert.Throws<ArgumentNullException>(() => new NbtBinaryWriter(new MemoryStream(), true).Write(null!));
    }

    [Fact]
    public void BinaryWriter_Primitives()
    {
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        w.Write((byte)0x2A);
        w.Write((short)-2);
        w.Write(42);
        w.Write(0x0102030405060708L);
        w.Write(1.5f);
        w.Write(2.25);
        w.Write(NbtTagType.Compound);
        Assert.Equal(new byte[]
        {
            0x2A,
            0xFF, 0xFE,
            0x00, 0x00, 0x00, 0x2A,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x3F, 0xC0, 0x00, 0x00,
            0x40, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x0A,
        }, ms.ToArray());
        Assert.Same(ms, w.BaseStream);
    }

    [Fact]
    public void BinaryWriter_Validation()
    {
        Assert.Throws<ArgumentNullException>(() => new NbtBinaryWriter(null!, true));
        var ex = Assert.Throws<ArgumentException>(() => new NbtBinaryWriter(new NoWriteStream(), true));
        Assert.Equal("Given stream must be writable (Parameter 'input')", ex.Message);
    }

    #endregion

    #region NbtFile

    [Fact]
    public void File_LoadFromBuffer()
    {
        var sample = NbtFixtures.SampleCompound(seed: 9);
        var payload = new NbtFile { RootTag = sample }.SaveToBuffer(NbtCompression.None);

        var f = new NbtFile();
        Assert.Equal(payload.Length, f.LoadFromBuffer(payload, 0, payload.Length, NbtCompression.None));
        NbtAssert.Equal(sample, f.RootTag);

        // offset: leading garbage is ignored
        var withPrefix = new byte[] { 0xAA, 0xBB }.Concat(payload).ToArray();
        var f2 = new NbtFile();
        Assert.Equal(payload.Length, f2.LoadFromBuffer(withPrefix, 2, payload.Length, NbtCompression.None));
        NbtAssert.Equal(sample, f2.RootTag);

        // one byte short of the document
        var f3 = new NbtFile();
        Assert.Throws<EndOfStreamException>(() => f3.LoadFromBuffer(payload, 0, payload.Length - 1, NbtCompression.None));

        // index + length beyond the buffer
        var f4 = new NbtFile();
        Assert.Throws<ArgumentException>(() => f4.LoadFromBuffer(payload, 1, payload.Length, NbtCompression.None));
        Assert.Throws<ArgumentNullException>(() => f4.LoadFromBuffer(null!, 0, 0, NbtCompression.None));
    }

    [Fact]
    public void File_SaveToBuffer()
    {
        var sample = NbtFixtures.SampleCompound(seed: 11);
        var f = new NbtFile { RootTag = sample };
        var payload = f.SaveToBuffer(NbtCompression.None);
        var f2 = new NbtFile();
        f2.LoadFromBuffer(payload, 0, payload.Length, NbtCompression.None);
        NbtAssert.Equal(sample, f2.RootTag);

        var ex = Assert.Throws<ArgumentException>(() => f.SaveToBuffer(NbtCompression.AutoDetect));
        Assert.Equal("AutoDetect is not a valid NbtCompression value for saving.", ex.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.SaveToBuffer((NbtCompression)77));
    }

    [Fact]
    public void File_CompressionRoundTrips()
    {
        var sample = NbtFixtures.SampleCompound(seed: 13);
        var f = new NbtFile { RootTag = sample };

        // None: exact byte accounting both ways
        byte[] none;
        using (var ms = new MemoryStream())
        {
            long written = f.SaveToStream(ms, NbtCompression.None);
            none = ms.ToArray();
            Assert.Equal(none.Length, written);
        }
        using (var ms = new MemoryStream(none))
        {
            var f2 = new NbtFile();
            Assert.Equal(none.Length, f2.LoadFromStream(ms, NbtCompression.None));
            NbtAssert.Equal(sample, f2.RootTag);
        }

        // GZip: round trip with auto-detection; the reader consumes the whole gzip stream
        using (var ms = new MemoryStream())
        {
            long written = f.SaveToStream(ms, NbtCompression.GZip);
            ms.Position = 0;
            var f2 = new NbtFile();
            Assert.Equal(written, f2.LoadFromStream(ms, NbtCompression.AutoDetect));
            Assert.Equal(NbtCompression.GZip, f2.FileCompression);
            NbtAssert.Equal(sample, f2.RootTag);
        }

        // ZLib: header pinned; round trip; the reader stops before the 4-byte Adler32 checksum
        using (var ms = new MemoryStream())
        {
            long written = f.SaveToStream(ms, NbtCompression.ZLib);
            var bytes = ms.ToArray();
            Assert.Equal(0x78, bytes[0]);
            Assert.Equal(0x01, bytes[1]);
            ms.Position = 0;
            var f2 = new NbtFile();
            long read = f2.LoadFromStream(ms, NbtCompression.AutoDetect);
            Assert.Equal(NbtCompression.ZLib, f2.FileCompression);
            NbtAssert.Equal(sample, f2.RootTag);
            Assert.Equal(written, read);
        }
    }

    [Fact]
    public void File_LoadFromStream_Errors()
    {
        var sample = NbtFixtures.SampleCompound(seed: 17);
        byte[] gzip;
        using (var ms = new MemoryStream())
        {
            new NbtFile { RootTag = sample }.SaveToStream(ms, NbtCompression.GZip);
            gzip = ms.ToArray();
        }

        Assert.Throws<NotSupportedException>(() => new NbtFile().LoadFromStream(new NonSeekableStream(gzip), NbtCompression.AutoDetect));
        Assert.Throws<EndOfStreamException>(() => new NbtFile().LoadFromStream(new MemoryStream(), NbtCompression.AutoDetect));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NbtFile().LoadFromStream(new MemoryStream(), (NbtCompression)77));
        Assert.Throws<ArgumentNullException>(() => new NbtFile().LoadFromStream(null!, NbtCompression.None));

        // non-seekable stream with explicit compression: byte accounting via ByteCountingStream
        using (var ms = new MemoryStream())
        {
            new NbtFile { RootTag = sample }.SaveToStream(ms, NbtCompression.None);
            var payload = ms.ToArray();
            var f = new NbtFile();
            Assert.Equal(payload.Length, f.LoadFromStream(new NonSeekableStream(payload), NbtCompression.None));
            NbtAssert.Equal(sample, f.RootTag);
        }

        // GZip payload forced through the ZLib path
        using (var ms = new MemoryStream(gzip))
        {
            var ex = Assert.Throws<InvalidDataException>(() => new NbtFile().LoadFromStream(ms, NbtCompression.ZLib));
            Assert.Equal("Unrecognized ZLib header. Expected 0x78", ex.Message);
        }
    }

    [Fact]
    public void File_SaveToStream_NonSeekable()
    {
        var sample = NbtFixtures.SampleCompound(seed: 19);
        byte[] payload;
        using (var ms = new MemoryStream())
        {
            new NbtFile { RootTag = sample }.SaveToStream(ms, NbtCompression.None);
            payload = ms.ToArray();
        }
        var f = new NbtFile { RootTag = sample };
        Assert.Throws<ArgumentException>(() => f.SaveToStream(new NonSeekableStream(), NbtCompression.None));
    }

    [Fact]
    public void File_SaveToStream_UnnamedRoot_Throws()
    {
        var root = new NbtCompound("x") { new NbtInt("a", 1) };
        var f = new NbtFile { RootTag = root };
        root.Name = null; // allowed while detached; now the root is unnamed
        var ex = Assert.Throws<NbtFormatException>(() => f.SaveToBuffer(NbtCompression.None));
        Assert.Equal("Cannot save NbtFile: Root tag is not named. Its name may be an empty string, but not null.", ex.Message);
    }

    [Fact]
    public void File_GetRootTagAndRootTagValidation()
    {
        var root = new NbtCompound("r") { new NbtInt("a", 1) };
        var f = new NbtFile { RootTag = root };
        Assert.Same(root, f.GetRootTag<NbtCompound>());
        Assert.Null(f.GetRootTag<NbtList>());

        Assert.Throws<ArgumentNullException>(() => f.RootTag = null!);
        var ex = Assert.Throws<ArgumentException>(() => f.RootTag = new NbtCompound());
        Assert.Equal("Root tag must be named.", ex.Message);
    }

    [Fact]
    public void File_BufferSizeValidation()
    {
        var f = new NbtFile();
        Assert.Equal(8192, f.BufferSize);
        f.BufferSize = 0;
        Assert.Equal(0, f.BufferSize);
        f.BufferSize = 123456;
        Assert.Equal(123456, f.BufferSize);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => f.BufferSize = -1);
        Assert.Equal("BufferSize cannot be negative. (Parameter 'value')\nActual value was -1.", ex.Message);
    }

    [Fact]
    public void File_DefaultBufferSize_ControlsNewInstances()
    {
        int original = NbtFile.DefaultBufferSize;
        try
        {
            NbtFile.DefaultBufferSize = 1234;
            Assert.Equal(1234, new NbtFile().BufferSize);
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NbtFile.DefaultBufferSize = -1);
            Assert.Equal("DefaultBufferSize cannot be negative. (Parameter 'value')\nActual value was -1.", ex.Message);
            Assert.Equal(1234, NbtFile.DefaultBufferSize); // the throw did not change the value
        }
        finally
        {
            NbtFile.DefaultBufferSize = original;
        }
    }

    [Fact]
    public void File_ToString()
    {
        var f = new NbtFile { RootTag = new NbtCompound("root") { new NbtInt("a", 1) } };
        Assert.Equal("TAG_Compound(\"root\"): 1 entries {\n  TAG_Int(\"a\"): 1\n}", f.ToString());
        Assert.Equal("TAG_Compound(\"root\"): 1 entries {\n-TAG_Int(\"a\"): 1\n}", f.ToString("-"));
    }

    [Fact]
    public void File_DefaultState()
    {
        var f = new NbtFile();
        Assert.Equal("", f.RootTag.Name);
        Assert.Equal(0, ((NbtCompound)f.RootTag).Count);
        Assert.True(f.BigEndian);
        Assert.Equal(8192, f.BufferSize);
        Assert.Equal(NbtCompression.AutoDetect, f.FileCompression);
    }

    #endregion

    private sealed class NoReadStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private sealed class NoWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public NonSeekableStream() : this(Array.Empty<byte>()) { }

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => _inner.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
