using fNbt;

namespace NbtDiff.TestFixtures;

/// <summary>A fresh directory under the system temp path, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nbtdiff-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative) => System.IO.Path.Combine(Path, relative);
    public string Sub(string relative)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Read-only stream wrapper that counts bytes actually read, for "reads only the header" assertions.</summary>
public sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }
    public int ReadCalls { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override int ReadByte()
    {
        int b = inner.ReadByte();
        if (b >= 0) Count(1);
        return b;
    }

    private int Count(int n)
    {
        ReadCalls++;
        BytesRead += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Structural equality for tag trees: type, name, value, and children in order.</summary>
public static class NbtEquality
{
    public static bool AreEqual(NbtTag? a, NbtTag? b, out string difference)
    {
        difference = "";
        return AreEqual(a, b, "", ref difference);
    }

    private static bool AreEqual(NbtTag? a, NbtTag? b, string path, ref string difference)
    {
        if (a is null || b is null)
            return Fail(ref difference, path, a is null && b is null ? null : $"{Describe(a)} vs {Describe(b)}");
        if (a.TagType != b.TagType) return Fail(ref difference, path, $"type {a.TagType} vs {b.TagType}");
        if (a.Name != b.Name) return Fail(ref difference, path, $"name '{a.Name}' vs '{b.Name}'");

        switch (a)
        {
            case NbtCompound ca:
            {
                var cb = (NbtCompound)b;
                if (ca.Count != cb.Count) return Fail(ref difference, path, $"{ca.Count} vs {cb.Count} children");
                foreach (var (ta, tb) in ca.Tags.Zip(cb.Tags))
                    if (!AreEqual(ta, tb, $"{path}/{ta.Name}", ref difference)) return false;
                return true;
            }
            case NbtList la:
            {
                var lb = (NbtList)b;
                if (la.Count != lb.Count) return Fail(ref difference, path, $"{la.Count} vs {lb.Count} items");
                for (int i = 0; i < la.Count; i++)
                    if (!AreEqual(la[i], lb[i], $"{path}[{i}]", ref difference)) return false;
                return true;
            }
            case NbtByteArray ba: return ((NbtByteArray)b).Value.AsSpan().SequenceEqual(ba.Value) || Fail(ref difference, path, "byte array values");
            case NbtIntArray ia: return ((NbtIntArray)b).Value.AsSpan().SequenceEqual(ia.Value) || Fail(ref difference, path, "int array values");
            case NbtLongArray lla: return ((NbtLongArray)b).Value.AsSpan().SequenceEqual(lla.Value) || Fail(ref difference, path, "long array values");
            case NbtFloat fa: return BitConverter.SingleToInt32Bits(fa.Value) == BitConverter.SingleToInt32Bits(((NbtFloat)b).Value) || Fail(ref difference, path, $"{fa.Value} vs {((NbtFloat)b).Value}");
            case NbtDouble da: return BitConverter.DoubleToInt64Bits(da.Value) == BitConverter.DoubleToInt64Bits(((NbtDouble)b).Value) || Fail(ref difference, path, $"{da.Value} vs {((NbtDouble)b).Value}");
            default:
            {
                // Remaining scalars: compare through their string form.
                string va = a.ToString()!, vb = b.ToString()!;
                return va == vb || Fail(ref difference, path, $"{va} vs {vb}");
            }
        }
    }

    private static bool Fail(ref string difference, string path, string? message)
    {
        if (message is null) return true;
        difference = $"at '{path}': {message}";
        return false;
    }

    private static string Describe(NbtTag? t) => t is null ? "null" : $"{t.TagType} '{t.Name}'";
}
