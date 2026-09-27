using System.Buffers.Binary;
using fNbt;
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
    // Swapped (not cleared) on write, so a reader mid-enumeration keeps a consistent snapshot.
    private List<ChunkRef> _chunks = [];
    // Serializes this instance's writes (append offset + header rewrite must be atomic together).
    // Two separate RegionFile instances on the same path are not coordinated — the UI never opens one.
    private readonly object _writeGate = new();

    public string Path { get; }
    public RegionCoords? Coords { get; }
    public long Length => _reader.Length;

    /// <summary>Present slots (including ones whose header entry is invalid), ordered by z then x.</summary>
    public IReadOnlyList<ChunkRef> Chunks => _chunks;
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
            // Only the 5-byte prefix has to fit here: Minecraft does not pad the last chunk of a file
            // to a full sector, so the declared sector count routinely runs past EOF. The payload length
            // is checked against the file when the prefix is read.
            else if (byteOffset + ChunkRef.PrefixSize > reader.Length)
                headerError = $"header entry points at byte {byteOffset} but the file is only {reader.Length} bytes";

            var chunk = new ChunkRef(this, x, z, byteOffset, sectorCount, timestamp, headerError);
            _slots[i] = chunk;
            present.Add(chunk);
        }
        _chunks.AddRange(present);
    }

    public static LoadResult<RegionFile> Open(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            // ReadWrite sharing so a later WriteChunk can open the same file for writing (Minecraft's own share mode).
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write);
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

    /// <summary>
    /// Writes <paramref name="root"/> as chunk (x, z). The payload is compressed with the chunk's
    /// current scheme (ZLib for a new or unreadable slot) and appended as whole sectors at the end of
    /// the file — Minecraft's own strategy; the bytes an earlier copy occupied stay behind as dead
    /// sectors. The location and timestamp tables are updated and the in-memory slot replaced, so
    /// reads through this instance see the new content. External chunks (prefix bit 7) rewrite their
    /// <c>.mcc</c> file instead of touching the region file's data area.
    /// </summary>
    public LoadResult<ChunkRef> WriteChunk(int x, int z, NbtCompound root)
    {
        if ((uint)x >= RegionCoords.ChunksPerAxis || (uint)z >= RegionCoords.ChunksPerAxis)
            return LoadResult<ChunkRef>.Fail($"Chunk coordinates ({x}, {z}) are outside the region");
        return LoadResult<ChunkRef>.Try($"Write chunk ({x}, {z}) of {Path}", () =>
        {
            // An absent or never-read slot has unknown scheme/external state; ZLib inline is what
            // Minecraft writes for a new chunk, and appending inline is correct even when the old
            // bytes happened to live in a .mcc file.
            var existing = this[x, z];
            byte? existingScheme = existing?.SchemeByte;
            byte scheme = existingScheme is ChunkRef.SchemeGZip or ChunkRef.SchemeZLib or ChunkRef.SchemeNone
                ? existingScheme.GetValueOrDefault()
                : ChunkRef.SchemeZLib;

            var rootClone = (NbtCompound)root.Clone();
            rootClone.Name ??= "";
            var payload = new NbtFile(rootClone) { BigEndian = true }.SaveToBuffer(ToCompression(scheme));

            return existing is { IsExternal: true }
                ? WriteExternalChunk(existing, payload)
                : AppendInlineChunk(x, z, scheme, payload);
        });
    }

    /// <summary>
    /// Removes the chunk at local (x, z): its location and timestamp entries are cleared, which is how
    /// Minecraft deletes a chunk (the sectors become dead space), and an external <c>.mcc</c> payload
    /// file is removed. The region (and the .mcc) get the same one-time <c>.bak</c> as any write. An
    /// absent chunk is not an error.
    /// </summary>
    public LoadResult<RegionFile> DeleteChunk(int x, int z)
    {
        if ((uint)x >= RegionCoords.ChunksPerAxis || (uint)z >= RegionCoords.ChunksPerAxis)
            return LoadResult<RegionFile>.Fail($"Chunk coordinates ({x}, {z}) are outside the region");
        return LoadResult<RegionFile>.Try($"Delete chunk ({x}, {z}) of {Path}", () =>
        {
            lock (_writeGate)
            {
                var existing = this[x, z];
                if (existing is null) return this;
                if (existing.IsExternal is null) existing.ReadCompressedPayload();   // learn whether it lives in a .mcc
                SafeFile.BackupOnce(Path);
                using (var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write))
                {
                    int slot = z * RegionCoords.ChunksPerAxis + x;
                    Span<byte> zero = stackalloc byte[4];
                    RandomAccess.Write(handle, zero, slot * 4);
                    RandomAccess.Write(handle, zero, SectorSize + slot * 4);
                    RandomAccess.FlushToDisk(handle);
                }
                if (existing.IsExternal == true && existing.WorldCoords is { } coords)
                {
                    string mcc = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", $"c.{coords.X}.{coords.Z}.mcc");
                    SafeFile.BackupOnce(mcc);
                    File.Delete(mcc);
                }
                _slots[z * RegionCoords.ChunksPerAxis + x] = null;
                _chunks = _slots.Where(s => s is not null).Cast<ChunkRef>().ToList();
                return this;
            }
        });
    }

    private static NbtCompression ToCompression(byte scheme) => scheme switch
    {
        ChunkRef.SchemeGZip => NbtCompression.GZip,
        ChunkRef.SchemeNone => NbtCompression.None,
        _ => NbtCompression.ZLib,
    };

    private ChunkRef AppendInlineChunk(int x, int z, byte scheme, byte[] payload)
    {
        lock (_writeGate)
        {
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int sectorCount = (ChunkRef.PrefixSize + payload.Length + SectorSize - 1) / SectorSize;
            if (sectorCount > byte.MaxValue)
                throw new IOException($"the compressed chunk is {payload.Length:N0} bytes, too large for {byte.MaxValue} sectors");

            SafeFile.BackupOnce(Path);
            using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
            long dataStart = Math.Max((RandomAccess.GetLength(handle) + SectorSize - 1) / SectorSize * SectorSize, HeaderSize);
            var sector = new byte[sectorCount * SectorSize];
            BinaryPrimitives.WriteInt32BigEndian(sector, payload.Length + 1); // counts the scheme byte
            sector[4] = scheme;
            payload.CopyTo(sector, ChunkRef.PrefixSize);
            RandomAccess.Write(handle, sector, dataStart);
            // The chunk must be on disk before the header points at it. A crash before the location
            // entry is written leaves the old entry, and with it the old chunk, intact (appending never
            // overwrites live sectors); the entry itself is one aligned 4-byte write.
            RandomAccess.FlushToDisk(handle);

            int slot = z * RegionCoords.ChunksPerAxis + x;
            Span<byte> entry = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)((dataStart / SectorSize) << 8 | (uint)sectorCount));
            RandomAccess.Write(handle, entry, slot * 4);
            BinaryPrimitives.WriteUInt32BigEndian(entry, now);
            RandomAccess.Write(handle, entry, SectorSize + slot * 4);
            RandomAccess.FlushToDisk(handle);

            return UpdateSlot(x, z, dataStart, sectorCount, now);
        }
    }

    private ChunkRef WriteExternalChunk(ChunkRef existing, byte[] payload)
    {
        lock (_writeGate)
        {
            if (existing.WorldCoords is not { } coords)
                throw new IOException($"external chunk, but region file name '{System.IO.Path.GetFileName(Path)}' has no region coordinates to locate the .mcc file");
            string mcc = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", $"c.{coords.X}.{coords.Z}.mcc");
            SafeFile.BackupOnce(Path);
            SafeFile.BackupOnce(mcc);
            SafeFile.WriteAtomic(mcc, s => s.Write(payload));

            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
            Span<byte> entry = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, now);
            RandomAccess.Write(handle, entry, SectorSize + (existing.Z * RegionCoords.ChunksPerAxis + existing.X) * 4);
            return UpdateSlot(existing.X, existing.Z, existing.Offset, existing.SectorCount, now);
        }
    }

    /// <summary>Swaps the slot's ChunkRef for one pointing at the new bytes and rebuilds the ordered list.</summary>
    private ChunkRef UpdateSlot(int x, int z, long offset, int sectorCount, uint timestamp)
    {
        var updated = new ChunkRef(this, x, z, offset, sectorCount, timestamp, headerError: null);
        _slots[z * RegionCoords.ChunksPerAxis + x] = updated;
        var fresh = new List<ChunkRef>();
        foreach (var slot in _slots)
            if (slot is not null) fresh.Add(slot);
        _chunks = fresh;
        return updated;
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>Positional reads, so concurrent chunk reads need no shared cursor.</summary>
    internal interface IRandomReader : IDisposable
    {
        long Length { get; }
        void ReadExactly(long offset, Span<byte> buffer);
    }

    private sealed class HandleReader(SafeFileHandle handle) : IRandomReader
    {
        // Dynamic: WriteChunk appends to the file a handle created by Open is looking at.
        public long Length => RandomAccess.GetLength(handle);

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
