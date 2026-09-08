using System.Buffers.Binary;
using fNbt;

namespace NbtDiff.Nbt;

/// <summary>
/// One occupied slot of a <see cref="RegionFile"/>. Constructed from the header alone; nothing past
/// the header is read until a <c>Read*</c> method is called.
/// </summary>
public sealed class ChunkRef
{
    /// <summary>Compression scheme byte values as stored in the chunk prefix.</summary>
    public const byte SchemeGZip = 1, SchemeZLib = 2, SchemeNone = 3, SchemeLz4 = 4, SchemeCustom = 127;
    private const byte ExternalFlag = 0x80;
    private const int PrefixSize = 5;

    private readonly RegionFile _region;
    private byte _schemeByte;   // 0 until the prefix has been read
    private int _payloadLength = -1;

    public int X { get; }
    public int Z { get; }
    /// <summary>Byte offset of the 5-byte prefix in the region file.</summary>
    public long Offset { get; }
    public int SectorCount { get; }
    /// <summary>Last-modified time from the header table (seconds since epoch). Never part of equality.</summary>
    public uint Timestamp { get; }
    /// <summary>Why the header entry is unusable, or null. When set, every read fails with this reason.</summary>
    public string? HeaderError { get; }
    public bool IsCorruptHeader => HeaderError is not null;

    /// <summary>Raw scheme byte with the external flag stripped; null until a read has happened.</summary>
    public byte? SchemeByte => _schemeByte == 0 ? null : (byte)(_schemeByte & ~ExternalFlag);
    /// <summary>Payload lives in <c>c.&lt;x&gt;.&lt;z&gt;.mcc</c> next to the region (prefix bit 7 set). Null until read.</summary>
    public bool? IsExternal => _schemeByte == 0 ? null : (_schemeByte & ExternalFlag) != 0;
    /// <summary>fNbt compression for the payload; null until read or when the scheme is unsupported (LZ4, custom).</summary>
    public NbtCompression? Compression => SchemeByte switch
    {
        SchemeGZip => NbtCompression.GZip,
        SchemeZLib => NbtCompression.ZLib,
        SchemeNone => NbtCompression.None,
        _ => null,
    };

    internal ChunkRef(RegionFile region, int x, int z, long offset, int sectorCount, uint timestamp, string? headerError)
    {
        _region = region;
        X = x;
        Z = z;
        Offset = offset;
        SectorCount = sectorCount;
        Timestamp = timestamp;
        HeaderError = headerError;
    }

    /// <summary>Absolute chunk coordinates, when the region file name carries region coordinates.</summary>
    public (int X, int Z)? WorldCoords => _region.Coords?.ChunkAt(X, Z);

    /// <summary>
    /// The still-compressed payload: for inline chunks the bytes after the 5-byte prefix; for external
    /// chunks the whole <c>.mcc</c> file. Cheap relative to <see cref="ReadNbt"/> — no decompression.
    /// </summary>
    public LoadResult<byte[]> ReadCompressedPayload()
    {
        if (HeaderError is not null)
            return LoadResult<byte[]>.Fail($"Chunk ({X}, {Z}): {HeaderError}");
        try
        {
            ReadPrefix();
            if ((_schemeByte & ExternalFlag) != 0)
                return ReadExternal();

            var payload = new byte[_payloadLength];
            _region.ReadExactly(Offset + PrefixSize, payload);
            return LoadResult<byte[]>.Success(payload);
        }
        catch (Exception e)
        {
            return LoadResult<byte[]>.Fail($"Chunk ({X}, {Z}): read payload", e);
        }
    }

    /// <summary>Decompresses and parses the chunk. The root must be a compound.</summary>
    public LoadResult<NbtCompound> ReadNbt()
    {
        var payload = ReadCompressedPayload();
        if (!payload.Ok)
            return LoadResult<NbtCompound>.Fail(payload.Failure!);

        var compression = Compression;
        if (compression is null)
            return LoadResult<NbtCompound>.Fail($"Chunk ({X}, {Z}): unsupported compression scheme {SchemeByte}"
                + (SchemeByte == SchemeLz4 ? " (LZ4)" : ""));

        try
        {
            var file = new NbtFile { BigEndian = true };
            file.LoadFromBuffer(payload.Value!, 0, payload.Value!.Length, compression.Value);
            if (file.RootTag is not NbtCompound root)
                return LoadResult<NbtCompound>.Fail($"Chunk ({X}, {Z}): root tag is {file.RootTag?.TagType}, not a compound");
            return LoadResult<NbtCompound>.Success(root);
        }
        catch (Exception e)
        {
            return LoadResult<NbtCompound>.Fail($"Chunk ({X}, {Z}): parse NBT", e);
        }
    }

    private void ReadPrefix()
    {
        if (_schemeByte != 0) return;

        Span<byte> prefix = stackalloc byte[PrefixSize];
        _region.ReadExactly(Offset, prefix);
        int length = BinaryPrimitives.ReadInt32BigEndian(prefix);   // counts the scheme byte
        byte scheme = prefix[4];

        int maxLength = SectorCount * RegionFile.SectorSize - 4;
        if (length < 1 || length > maxLength)
            throw new InvalidDataException($"length prefix {length} is outside 1..{maxLength} for {SectorCount} sector(s)");
        if (scheme == 0)
            throw new InvalidDataException("compression scheme byte is 0");

        _payloadLength = length - 1;
        _schemeByte = scheme;
    }

    private LoadResult<byte[]> ReadExternal()
    {
        var coords = WorldCoords;
        if (coords is null)
            return LoadResult<byte[]>.Fail($"Chunk ({X}, {Z}): external chunk, but region file name '{Path.GetFileName(_region.Path)}' has no region coordinates to locate the .mcc file");
        var dir = Path.GetDirectoryName(_region.Path) ?? "";
        var mcc = Path.Combine(dir, $"c.{coords.Value.X}.{coords.Value.Z}.mcc");
        if (!File.Exists(mcc))
            return LoadResult<byte[]>.Fail($"Chunk ({X}, {Z}): external chunk file '{mcc}' is missing");
        return LoadResult<byte[]>.Success(File.ReadAllBytes(mcc));
    }

    public override string ToString() => $"chunk ({X}, {Z}) @ {Offset} × {SectorCount} sectors";
}
