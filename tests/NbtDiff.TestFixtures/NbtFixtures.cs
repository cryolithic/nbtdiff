using System.Buffers.Binary;
using System.Text;
using fNbt;
using NbtDiff.Nbt;
using NbtDiff.Nbt.Snbt;

namespace NbtDiff.TestFixtures;

/// <summary>Deterministic standalone NBT documents in every <see cref="NbtFormat"/>.</summary>
public static class NbtFixtures
{
    /// <summary>A compound exercising every tag type. Same seed, same tree.</summary>
    public static NbtCompound SampleCompound(int seed = 1, string name = "")
    {
        var rng = new Random(seed);
        var root = new NbtCompound(name)
        {
            new NbtByte("byte", (byte)rng.Next(256)),
            new NbtShort("short", (short)rng.Next(short.MinValue, short.MaxValue)),
            new NbtInt("int", rng.Next()),
            new NbtLong("long", rng.NextInt64()),
            new NbtFloat("float", (float)rng.NextDouble() * 100f),
            new NbtDouble("double", rng.NextDouble() * 1000),
            new NbtString("string", $"sample {seed} with \"quotes\" and \\backslash"),
            new NbtByteArray("bytes", Enumerable.Range(0, 16).Select(i => (byte)rng.Next(256)).ToArray()),
            new NbtIntArray("ints", Enumerable.Range(0, 8).Select(_ => rng.Next()).ToArray()),
            new NbtLongArray("longs", Enumerable.Range(0, 4).Select(_ => rng.NextInt64()).ToArray()),
            new NbtList("list", NbtTagType.String) { new NbtString("a"), new NbtString("b") },
            new NbtList("emptyList"),
            new NbtCompound("nested")
            {
                new NbtInt("depth", 1),
                new NbtList("compounds", NbtTagType.Compound)
                {
                    new NbtCompound { new NbtString("id", "minecraft:stone") },
                    new NbtCompound { new NbtString("id", "minecraft:dirt") },
                },
            },
        };
        return root;
    }

    public static byte[] ToBytes(NbtCompound root, NbtFormat format, NbtCompression compression = NbtCompression.GZip)
    {
        switch (format)
        {
            case NbtFormat.Snbt:
                return Encoding.UTF8.GetBytes(SnbtWriter.Write(root, SnbtOptions.Pretty));
            case NbtFormat.JavaNbt:
                return new NbtFile((NbtCompound)root.Clone()) { BigEndian = true }.SaveToBuffer(compression);
            case NbtFormat.BedrockNbt:
                return new NbtFile((NbtCompound)root.Clone()) { BigEndian = false }.SaveToBuffer(compression);
            case NbtFormat.BedrockLevelDat:
            {
                var payload = new NbtFile((NbtCompound)root.Clone()) { BigEndian = false }.SaveToBuffer(NbtCompression.None);
                var bytes = new byte[8 + payload.Length];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 10);               // storage version
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), payload.Length);
                payload.CopyTo(bytes, 8);
                return bytes;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static void WriteFile(string path, NbtCompound root, NbtFormat format, NbtCompression compression = NbtCompression.GZip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ToBytes(root, format, compression));
    }
}
