using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using fNbt;

namespace NbtDiff.Core;

/// <summary>
/// Content hash of a tag tree that ignores compound key order (unless asked not to) and is otherwise
/// exact: floats by bit pattern, lists and arrays in stored order, names included. The walk order
/// here is the contract the differ must agree with — equal hashes ⇔ empty diff.
/// </summary>
public static class NbtCanonicalHasher
{
    /// <param name="ignoredTags">Compound keys to skip (default <see cref="TagIgnoreSet.Default"/>); pass <see cref="TagIgnoreSet.Empty"/> to hash everything.</param>
    /// <param name="keyedLists">When given, compound list items are hashed in key order (see <see cref="KeyedAligner.CanonicalItems"/>) so a reordered entity list hashes equal; must match the differ's aligner.</param>
    public static ulong Hash(NbtTag tag, bool compoundOrderMatters = false, TagIgnoreSet? ignoredTags = null, KeyedAligner? keyedLists = null)
    {
        var h = new XxHash64();
        Append(h, tag, new Walk(compoundOrderMatters, keyedLists), (ignoredTags ?? TagIgnoreSet.Default).Root);
        return h.GetCurrentHashAsUInt64();
    }

    private readonly record struct Walk(bool Ordered, KeyedAligner? Keyed);

    /// <summary>Children of a compound in canonical (ordinal key) order — shared with the differ.</summary>
    public static IEnumerable<NbtTag> CanonicalChildren(NbtCompound compound, bool compoundOrderMatters = false) =>
        compoundOrderMatters ? compound.Tags : compound.Tags.OrderBy(t => t.Name, StringComparer.Ordinal);

    private static void Append(XxHash64 h, NbtTag tag, Walk w, TagIgnoreSet.Node? ignore)
    {
        Span<byte> scratch = stackalloc byte[8];
        scratch[0] = (byte)tag.TagType;
        h.Append(scratch[..1]);
        AppendString(h, tag.Name);

        switch (tag)
        {
            case NbtByte b:
                scratch[0] = b.Value;
                h.Append(scratch[..1]);
                break;
            case NbtShort s:
                BinaryPrimitives.WriteInt16LittleEndian(scratch, s.Value);
                h.Append(scratch[..2]);
                break;
            case NbtInt i:
                BinaryPrimitives.WriteInt32LittleEndian(scratch, i.Value);
                h.Append(scratch[..4]);
                break;
            case NbtLong l:
                BinaryPrimitives.WriteInt64LittleEndian(scratch, l.Value);
                h.Append(scratch);
                break;
            case NbtFloat f:
                BinaryPrimitives.WriteInt32LittleEndian(scratch, BitConverter.SingleToInt32Bits(f.Value));
                h.Append(scratch[..4]);
                break;
            case NbtDouble d:
                BinaryPrimitives.WriteInt64LittleEndian(scratch, BitConverter.DoubleToInt64Bits(d.Value));
                h.Append(scratch);
                break;
            case NbtString str:
                AppendString(h, str.Value);
                break;
            case NbtByteArray ba:
                AppendLength(h, ba.Value.Length);
                h.Append(ba.Value);
                break;
            case NbtIntArray ia:
                AppendLength(h, ia.Value.Length);
                h.Append(MemoryMarshal.AsBytes(ia.Value.AsSpan()));
                break;
            case NbtLongArray la:
                AppendLength(h, la.Value.Length);
                h.Append(MemoryMarshal.AsBytes(la.Value.AsSpan()));
                break;
            case NbtList list:
                // An empty list's element type is not content: SNBT yields Unknown, binary yields End
                // (which is what Minecraft writes). Normalize so both hash alike; the differ must too.
                scratch[0] = (byte)(list.Count == 0 ? NbtTagType.End : list.ListType);
                h.Append(scratch[..1]);
                AppendLength(h, list.Count);
                foreach (var item in w.Keyed is null ? list : w.Keyed.CanonicalItems(list))
                    Append(h, item, w, ignore);   // lists are transparent to ignore paths
                break;
            case NbtCompound compound:
            {
                // Ignored keys are left out of the count as well, so "present but ignored" equals "absent".
                var kept = ignore is null
                    ? CanonicalChildren(compound, w.Ordered)
                    : CanonicalChildren(compound, w.Ordered).Where(c => !ignore.Ignores(c.Name));
                var children = kept as IReadOnlyCollection<NbtTag> ?? kept.ToList();
                AppendLength(h, children.Count);
                foreach (var child in children)
                    Append(h, child, w, ignore?.Child(child.Name));
                break;
            }
            default:
                throw new NotSupportedException($"Tag type {tag.TagType}");
        }
    }

    private static void AppendLength(XxHash64 h, int length)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, length);
        h.Append(buf);
    }

    // Null and "" hash differently: a list item has no name, a compound child always has one.
    private static void AppendString(XxHash64 h, string? s)
    {
        if (s is null)
        {
            AppendLength(h, -1);
            return;
        }
        int byteCount = Encoding.UTF8.GetByteCount(s);
        AppendLength(h, byteCount);
        if (byteCount == 0) return;
        byte[]? rented = null;
        Span<byte> buf = byteCount <= 256 ? stackalloc byte[256] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int n = Encoding.UTF8.GetBytes(s, buf);
            h.Append(buf[..n]);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
