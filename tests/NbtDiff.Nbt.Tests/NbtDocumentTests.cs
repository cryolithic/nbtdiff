using System.Text;
using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Nbt.Tests;

public class NbtDocumentTests
{
    public static TheoryData<NbtFormat, NbtCompression> Formats => new()
    {
        { NbtFormat.Snbt, NbtCompression.None },
        { NbtFormat.JavaNbt, NbtCompression.GZip },
        { NbtFormat.JavaNbt, NbtCompression.ZLib },
        { NbtFormat.JavaNbt, NbtCompression.None },
        { NbtFormat.BedrockNbt, NbtCompression.None },
        { NbtFormat.BedrockNbt, NbtCompression.GZip },
        { NbtFormat.BedrockLevelDat, NbtCompression.None },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Load_DetectsFormatAndRoundTrips(NbtFormat format, NbtCompression compression)
    {
        var sample = NbtFixtures.SampleCompound();
        var bytes = NbtFixtures.ToBytes(sample, format, compression);

        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(bytes), "sample"));

        Assert.Equal(format, doc.Format.Format);
        Assert.Equal(compression, doc.Format.Compression);
        Assert.Equal(format == NbtFormat.JavaNbt || format == NbtFormat.Snbt, doc.Format.BigEndian);
        Assert.Equal("sample", doc.Path);
        NbtAssert.Equal(sample, doc.Root);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Load_FromPath(NbtFormat format, NbtCompression compression)
    {
        using var dir = new TempDir();
        var sample = NbtFixtures.SampleCompound(seed: 3);
        var path = dir.File("thing.dat");
        NbtFixtures.WriteFile(path, sample, format, compression);

        var doc = NbtAssert.Ok(NbtDocument.Load(path));
        Assert.Equal(path, doc.Path);
        Assert.Equal(format, doc.Format.Format);
        NbtAssert.Equal(sample, doc.Root);
    }

    [Fact]
    public void Snbt_WithBomAndLeadingWhitespace()
    {
        var text = "\n\t {a:1}";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(bytes), "x.snbt"));
        Assert.Equal(NbtFormat.Snbt, doc.Format.Format);
        Assert.Equal(1, doc.Root.Get<NbtInt>("a")!.Value);
        Assert.Equal("", doc.Root.Name);
    }

    [Fact]
    public void Snbt_RootMustBeCompound()
    {
        var failure = NbtAssert.Failed(NbtDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes("[1,2]")), "x.snbt"));
        Assert.Equal(4, failure.Attempts.Count);
    }

    [Fact]
    public void Garbage_FailsWithOneAttemptPerStrategy()
    {
        var bytes = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();
        var failure = NbtAssert.Failed(NbtDocument.Load(new MemoryStream(bytes), "junk"));
        Assert.Equal(4, failure.Attempts.Count);
        Assert.Contains("SNBT", failure.Attempts[0].Description);
        Assert.Contains("JavaNbt", failure.Attempts[1].Description);
        Assert.Contains("BedrockNbt", failure.Attempts[2].Description);
        Assert.Contains("BedrockLevelDat", failure.Attempts[3].Description);
        Assert.Contains("Not readable", failure.ToDetailedString());
    }

    [Fact]
    public void EmptyFile_Fails()
    {
        NbtAssert.Failed(NbtDocument.Load(new MemoryStream(), "empty"));
    }

    [Fact]
    public void MissingFile_Fails()
    {
        var failure = NbtAssert.Failed(NbtDocument.Load(Path.Combine(Path.GetTempPath(), "nbtdiff-does-not-exist.dat")));
        Assert.Equal("Open file", failure.Description);
        Assert.IsAssignableFrom<IOException>(failure.Exception);
    }

    [Fact]
    public void Bedrock_NotMistakenForJava()
    {
        // A little-endian file read as big-endian either throws or yields garbage names; the
        // suspicious-parse check must send it to the Bedrock strategy.
        var sample = new NbtCompound("") { new NbtString("LevelName", "Bedrock world"), new NbtInt("StorageVersion", 10) };
        var doc = NbtAssert.Ok(NbtDocument.Load(new MemoryStream(NbtFixtures.ToBytes(sample, NbtFormat.BedrockNbt, NbtCompression.None)), "level.dat"));
        Assert.Equal(NbtFormat.BedrockNbt, doc.Format.Format);
        Assert.Equal("Bedrock world", doc.Root.Get<NbtString>("LevelName")!.Value);
    }

    [Fact]
    public void NonSeekableStream_Rejected()
    {
        var stream = new NonSeekable(new MemoryStream([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => NbtDocument.Load(stream, "x"));
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
