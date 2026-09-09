using System.Buffers.Binary;
using fNbt;
using NbtDiff.Nbt;

namespace NbtDiff.TestFixtures;

/// <param name="Scheme">Chunk compression scheme byte (<see cref="ChunkRef.SchemeZLib"/> etc.).</param>
/// <param name="External">Store the payload in <c>c.&lt;x&gt;.&lt;z&gt;.mcc</c> next to the region.</param>
public sealed record ChunkSpec(int X, int Z, NbtCompound Data, byte Scheme = ChunkRef.SchemeZLib, uint Timestamp = 0, bool External = false);

public enum SectorOrder
{
    /// <summary>Chunks stored in (z, x) order — a freshly generated world.</summary>
    Sequential,
    /// <summary>Reverse (z, x) order — same content, every chunk at a different sector.</summary>
    Reverse,
}

/// <param name="GapSectors">Empty sectors left between chunks, to simulate fragmentation.</param>
/// <param name="PadLastSector">False writes the file as Minecraft does: it ends right after the last chunk's bytes, not at a sector boundary.</param>
public sealed record RegionWriteOptions(SectorOrder Order = SectorOrder.Sequential, int GapSectors = 0, bool PadLastSector = true);

/// <summary>Writes valid Anvil region files from chunk compounds.</summary>
public static class RegionWriter
{
    private const int SectorSize = RegionFile.SectorSize;

    public static void Write(string path, IEnumerable<ChunkSpec> chunks, RegionWriteOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var coords = RegionCoords.FromFileName(path);
        var dir = Path.GetDirectoryName(path)!;
        File.WriteAllBytes(path, Build(chunks, options, coords, (name, bytes) => File.WriteAllBytes(Path.Combine(dir, name), bytes)));
    }

    /// <summary>Builds the region bytes. External chunk payloads are handed to <paramref name="writeExternal"/> as (file name, bytes).</summary>
    public static byte[] Build(IEnumerable<ChunkSpec> chunks, RegionWriteOptions? options = null, RegionCoords? coords = null, Action<string, byte[]>? writeExternal = null)
    {
        options ??= new RegionWriteOptions();
        var ordered = chunks.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
        if (options.Order == SectorOrder.Reverse) ordered.Reverse();

        using var ms = new MemoryStream();
        ms.SetLength(RegionFile.HeaderSize);
        ms.Position = RegionFile.HeaderSize;
        var header = new byte[RegionFile.HeaderSize];
        long lastChunkEnd = 0;

        foreach (var chunk in ordered)
        {
            if ((uint)chunk.X >= 32 || (uint)chunk.Z >= 32)
                throw new ArgumentOutOfRangeException(nameof(chunks), $"chunk ({chunk.X}, {chunk.Z}) is outside 0..31");

            byte[] payload = Compress(chunk.Data, chunk.Scheme);
            byte[] inline;
            if (chunk.External)
            {
                if (coords is null) throw new ArgumentException("External chunks need region coords for the .mcc file name");
                if (writeExternal is null) throw new ArgumentException("External chunks need a writeExternal callback");
                var (wx, wz) = coords.Value.ChunkAt(chunk.X, chunk.Z);
                writeExternal($"c.{wx}.{wz}.mcc", payload);
                inline = new byte[5];
                BinaryPrimitives.WriteInt32BigEndian(inline, 1);
                inline[4] = (byte)(chunk.Scheme | 0x80);
            }
            else
            {
                inline = new byte[5 + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(inline, payload.Length + 1);
                inline[4] = chunk.Scheme;
                payload.CopyTo(inline, 5);
            }

            int sector = checked((int)(ms.Position / SectorSize));
            int sectorCount = (inline.Length + SectorSize - 1) / SectorSize;
            if (sectorCount > 255) throw new InvalidOperationException($"chunk ({chunk.X}, {chunk.Z}) is too large for a region file");

            int slot = chunk.Z * 32 + chunk.X;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(slot * 4), (uint)(sector << 8 | sectorCount));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(SectorSize + slot * 4), chunk.Timestamp);

            ms.Write(inline);
            long end = ms.Position;
            long padded = (long)(sector + sectorCount + options.GapSectors) * SectorSize;
            ms.SetLength(padded);
            ms.Position = padded;
            lastChunkEnd = end;
        }

        if (!options.PadLastSector && lastChunkEnd > 0)
            ms.SetLength(lastChunkEnd);
        ms.Position = 0;
        ms.Write(header);
        return ms.ToArray();
    }

    /// <summary>The compressed payload for a chunk exactly as it would sit in the file after the 5-byte prefix.</summary>
    public static byte[] Compress(NbtCompound data, byte scheme)
    {
        var compression = scheme switch
        {
            ChunkRef.SchemeGZip => NbtCompression.GZip,
            ChunkRef.SchemeZLib => NbtCompression.ZLib,
            ChunkRef.SchemeNone => NbtCompression.None,
            // Unsupported schemes still need bytes on disk; ZLib stands in so the file is well-formed.
            _ => NbtCompression.ZLib,
        };
        var root = (NbtCompound)data.Clone();
        root.Name ??= "";
        return new NbtFile(root) { BigEndian = true }.SaveToBuffer(compression);
    }
}
