using fNbt;

namespace NbtDiff.Nbt;

public enum NbtFormat
{
    /// <summary>Text (stringified NBT), UTF-8.</summary>
    Snbt,
    /// <summary>Java Edition binary NBT, big-endian.</summary>
    JavaNbt,
    /// <summary>Bedrock Edition binary NBT, little-endian, no header.</summary>
    BedrockNbt,
    /// <summary>Bedrock level.dat: 8-byte header (version + length) then little-endian NBT.</summary>
    BedrockLevelDat,
}

public sealed record NbtFormatInfo(NbtFormat Format, NbtCompression Compression, bool BigEndian)
{
    public static readonly NbtFormatInfo Snbt = new(NbtFormat.Snbt, NbtCompression.None, BigEndian: true);
}
