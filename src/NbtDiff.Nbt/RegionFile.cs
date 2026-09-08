using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace NbtDiff.Nbt;

/// <summary>
/// Header-only view of an Anvil (or pre-Anvil <c>.mcr</c>) region file. Opening one reads exactly the
/// two 4 KiB header tables; chunk bytes are read on demand through <see cref="ChunkRef"/>.
/// Thread-safe for concurrent chunk reads.
/// </summary>
public sealed class RegionFile : IDisposable
{
    public const int SectorSize = 4096;
    public const int HeaderSize = 2 * SectorSize;
    private const int Slots = RegionCoords.ChunksPerAxis * RegionCoords.ChunksPerAxis;

    private readonly IRandomReader _reader;
    private readonly ChunkRef?[] _slots = new ChunkRef?[Slots];

    public string Path { get; }
    public RegionCoords? Coords { get; }
    public long Length => _reader.Length;

    /// <summary>Present slots (including ones whose header entry is invalid), ordered by z then x.</summary>
    public IReadOnlyList<ChunkRef> Chunks { get; }
    public int ChunkCount => Chunks.Count;

    /// <summary>The slot at local (x, z) in 0..31, or null when no chunk is stored there.</summary>
    public ChunkRef? this[int x, int z]
    {
        get
        {
            if ((uint)x >= RegionCoords.ChunksPerAxis || (uint)z >= RegionCoords.ChunksPerAxis)
                throw new ArgumentOutOfRangeException(x is < 0 or >= RegionCoords.ChunksPerAxis ? nameof(x) : nameof(z));
            return _slots[z * RegionCoords.ChunksPerAxis + x];
        }
    }

    private RegionFile(string path, IRandomReader reader, ReadOnlySpan<byte> header)
    {
        Path = path;
        _reader = reader;
        Coords = RegionCoords.FromFileName(path);

        var present = new List<ChunkRef>();
        for (int i = 0; i < Slots; i++)
        {
            uint location = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(i * 4, 4));
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(SectorSize + i * 4, 4));
            int sectorOffset = (int)(location >> 8);
            int sectorCount = (int)(location & 0xFF);
            if (sectorOffset == 0 && sectorCount == 0)
                continue;

            int x = i % RegionCoords.ChunksPerAxis;
            int z = i / RegionCoords.ChunksPerAxis;
            long byteOffset = (long)sectorOffset * SectorSize;
            string? headerError = null;
            if (sectorCount == 0)
                headerError = "header entry has zero sectors";
            else if (byteOffset < HeaderSize)
                headerError = $"header entry points at byte {byteOffset}, inside the header tables";
            else if (byteOffset + (long)sectorCount * SectorSize > reader.Length)
                headerError = $"header entry claims {sectorCount} sector(s) at byte {byteOffset} but the file is only {reader.Length} bytes";

            var chunk = new ChunkRef(this, x, z, byteOffset, sectorCount, timestamp, headerError);
            _slots[i] = chunk;
            present.Add(chunk);
        }
        Chunks = present;
    }

    public static LoadResult<RegionFile> Open(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Open(path, new HandleReader(handle));
        }
        catch (Exception e)
        {
            handle?.Dispose();
            return LoadResult<RegionFile>.Fail("Open region file", e);
        }
    }

    /// <summary>Opens from a seekable stream; the region takes ownership and disposes it.</summary>
    public static LoadResult<RegionFile> Open(Stream stream, string displayPath)
    {
        try
        {
            return Open(displayPath, new StreamReader(stream));
        }
        catch (Exception e)
        {
            stream.Dispose();
            return LoadResult<RegionFile>.Fail("Open region file", e);
        }
    }

    private static LoadResult<RegionFile> Open(string path, IRandomReader reader)
    {
        long length = reader.Length;
        // Minecraft itself leaves 0-byte region files behind (a region touched but never written);
        // they are legitimately empty, not corrupt. Anything else under a full header is truncated.
        if (length == 0)
            return LoadResult<RegionFile>.Success(new RegionFile(path, reader, new byte[HeaderSize]));
        if (length < HeaderSize)
        {
            reader.Dispose();
            return LoadResult<RegionFile>.Fail(
                $"File is {length} bytes; a region file has at least a {HeaderSize}-byte header");
        }
        Span<byte> header = new byte[HeaderSize];
        reader.ReadExactly(0, header);
        return LoadResult<RegionFile>.Success(new RegionFile(path, reader, header));
    }

    internal void ReadExactly(long offset, Span<byte> buffer) => _reader.ReadExactly(offset, buffer);

    public void Dispose() => _reader.Dispose();

    /// <summary>Positional reads, so concurrent chunk reads need no shared cursor.</summary>
    internal interface IRandomReader : IDisposable
    {
        long Length { get; }
        void ReadExactly(long offset, Span<byte> buffer);
    }

    private sealed class HandleReader(SafeFileHandle handle) : IRandomReader
    {
        public long Length { get; } = RandomAccess.GetLength(handle);

        public void ReadExactly(long offset, Span<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int n = RandomAccess.Read(handle, buffer, offset);
                if (n <= 0) throw new EndOfStreamException($"Unexpected end of file at byte {offset}");
                buffer = buffer[n..];
                offset += n;
            }
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class StreamReader(Stream stream) : IRandomReader
    {
        private readonly object _gate = new();

        public long Length => stream.Length;

        public void ReadExactly(long offset, Span<byte> buffer)
        {
            lock (_gate)
            {
                stream.Position = offset;
                stream.ReadExactly(buffer);
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
