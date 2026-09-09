using System.Buffers.Binary;
using System.Runtime.InteropServices;
using fNbt;

namespace NbtDiff.Nbt;

/// <param name="Bytes">Compressed bytes; may alias a caller-supplied scratch buffer.</param>
/// <param name="Scheme">Compression scheme byte with the external flag stripped.</param>
public sealed record ChunkPayload(ReadOnlyMemory<byte> Bytes, byte Scheme, bool IsExternal);

/// <summary>
/// One occupied slot of a <see cref="RegionFile"/>. Constructed from the header alone; nothing past
/// the header is read until a <c>Read*</c> method is called.
/// </summary>
public sealed class ChunkRef
{
    /// <summary>Compression scheme byte values as stored in the chunk prefix.</summary>
    public const byte SchemeGZip = 1, SchemeZLib = 2, SchemeNone = 3, SchemeLz4 = 4, SchemeCustom = 127;
    private const byte ExternalFlag = 0x80;
    internal const int PrefixSize = 5;

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
    public LoadResult<byte[]> ReadCompressedPayload() =>
        ReadCompressedPayload(scratch: null).Map(p =>
            MemoryMarshal.TryGetArray(p.Bytes, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
                ? segment.Array
                : p.Bytes.ToArray());

    /// <summary>
    /// Same as <see cref="ReadCompressedPayload()"/> but reads into <paramref name="scratch"/> when it is
    /// large enough (inline chunks are at most <see cref="MaxInlinePayload"/> bytes), so a scan can reuse
    /// one buffer per worker. The returned memory aliases the scratch buffer in that case.
    /// </summary>
    public LoadResult<ChunkPayload> ReadCompressedPayload(byte[]? scratch)
    {
        if (HeaderError is not null)
            return LoadResult<ChunkPayload>.Fail($"Chunk ({X}, {Z}): {HeaderError}");
        try
        {
            ReadPrefix();
            byte scheme = (byte)(_schemeByte & ~ExternalFlag);
            if ((_schemeByte & ExternalFlag) != 0)
                return ReadExternal().Map(bytes => new ChunkPayload(bytes, scheme, IsExternal: true));

            var buffer = scratch is not null && scratch.Length >= _payloadLength ? scratch : new byte[_payloadLength];
            _region.ReadExactly(Offset + PrefixSize, buffer.AsSpan(0, _payloadLength));
            return LoadResult<ChunkPayload>.Success(new ChunkPayload(buffer.AsMemory(0, _payloadLength), scheme, IsExternal: false));
        }
        catch (Exception e)
        {
            return LoadResult<ChunkPayload>.Fail($"Chunk ({X}, {Z}): read payload", e);
        }
    }

    /// <summary>Upper bound on an inline payload: 255 sectors minus the prefix.</summary>
    public const int MaxInlinePayload = 255 * RegionFile.SectorSize - PrefixSize;

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

        return ParseNbt(payload.Value!, compression.Value);
    }

    /// <summary>Parses an already-read payload (e.g. one obtained through the scratch-buffer overload).</summary>
    public LoadResult<NbtCompound> ParseNbt(ChunkPayload payload)
    {
        var compression = Compression;
        if (compression is null)
            return LoadResult<NbtCompound>.Fail($"Chunk ({X}, {Z}): unsupported compression scheme {SchemeByte}"
                + (SchemeByte == SchemeLz4 ? " (LZ4)" : ""));
        if (MemoryMarshal.TryGetArray(payload.Bytes, out var segment))
            return ParseNbt(segment.Array!, compression.Value, segment.Offset, segment.Count);
        return ParseNbt(payload.Bytes.ToArray(), compression.Value);
    }

    private LoadResult<NbtCompound> ParseNbt(byte[] bytes, NbtCompression compression, int offset = 0, int count = -1)
    {
        try
        {
            var file = new NbtFile { BigEndian = true };
            file.LoadFromBuffer(bytes, offset, count < 0 ? bytes.Length : count, compression);
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
        if (Offset + 4 + length > _region.Length)
            throw new InvalidDataException($"length prefix {length} at byte {Offset} runs past the end of the {_region.Length}-byte file");
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
