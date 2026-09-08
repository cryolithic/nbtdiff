using System.Globalization;
using System.Text;
using fNbt;

namespace NbtDiff.Nbt.Snbt;

/// <param name="Indent">Per-level indentation for containers; null writes everything on one line.</param>
public sealed record SnbtOptions(string? Indent = null)
{
    public static readonly SnbtOptions Compact = new();
    public static readonly SnbtOptions Pretty = new(Indent: "  ");
}

/// <summary>Writes tags as SNBT that <see cref="SnbtParser"/> reads back to an equal tree.</summary>
public static class SnbtWriter
{
    public static string Write(NbtTag tag, SnbtOptions? options = null)
    {
        var sb = new StringBuilder();
        Write(sb, tag, options ?? SnbtOptions.Compact, depth: 0);
        return sb.ToString();
    }

    /// <summary>Only the value portion of a scalar, e.g. <c>12b</c> or <c>"text"</c>; containers get their full form.</summary>
    public static string WriteValue(NbtTag tag) => Write(tag, SnbtOptions.Compact);

    private static void Write(StringBuilder sb, NbtTag tag, SnbtOptions o, int depth)
    {
        switch (tag)
        {
            case NbtByte b: sb.Append(((sbyte)b.Value).ToString(CultureInfo.InvariantCulture)).Append('b'); break;
            case NbtShort s: sb.Append(s.Value.ToString(CultureInfo.InvariantCulture)).Append('s'); break;
            case NbtInt i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case NbtLong l: sb.Append(l.Value.ToString(CultureInfo.InvariantCulture)).Append('L'); break;
            case NbtFloat f: sb.Append(f.Value.ToString("R", CultureInfo.InvariantCulture)).Append('f'); break;
            case NbtDouble d: sb.Append(d.Value.ToString("R", CultureInfo.InvariantCulture)).Append('d'); break;
            case NbtString str: WriteQuoted(sb, str.Value); break;
            case NbtByteArray ba: WriteArray(sb, 'B', ba.Value.Select(v => ((sbyte)v).ToString(CultureInfo.InvariantCulture) + "b")); break;
            case NbtIntArray ia: WriteArray(sb, 'I', ia.Value.Select(v => v.ToString(CultureInfo.InvariantCulture))); break;
            case NbtLongArray la: WriteArray(sb, 'L', la.Value.Select(v => v.ToString(CultureInfo.InvariantCulture) + "L")); break;
            case NbtList list: WriteList(sb, list, o, depth); break;
            case NbtCompound compound: WriteCompound(sb, compound, o, depth); break;
            default: throw new NotSupportedException($"Cannot write tag type {tag.TagType}");
        }
    }

    private static void WriteCompound(StringBuilder sb, NbtCompound compound, SnbtOptions o, int depth)
    {
        sb.Append('{');
        bool first = true;
        foreach (var child in compound.Tags)
        {
            if (!first) sb.Append(',');
            first = false;
            NewLine(sb, o, depth + 1);
            WriteKey(sb, child.Name!);
            sb.Append(':');
            if (o.Indent is not null) sb.Append(' ');
            Write(sb, child, o, depth + 1);
        }
        if (!first) NewLine(sb, o, depth);
        sb.Append('}');
    }

    private static void WriteList(StringBuilder sb, NbtList list, SnbtOptions o, int depth)
    {
        sb.Append('[');
        bool first = true;
        foreach (var item in list)
        {
            if (!first) sb.Append(',');
            first = false;
            NewLine(sb, o, depth + 1);
            Write(sb, item, o, depth + 1);
        }
        if (!first) NewLine(sb, o, depth);
        sb.Append(']');
    }

    // Arrays stay on one line regardless of indentation: they can be thousands of elements.
    private static void WriteArray(StringBuilder sb, char kind, IEnumerable<string> items)
    {
        sb.Append('[').Append(kind).Append(';');
        sb.AppendJoin(',', items);
        sb.Append(']');
    }

    private static void WriteKey(StringBuilder sb, string key)
    {
        if (key.Length > 0 && key.All(SnbtParser.IsUnquotedChar))
            sb.Append(key);
        else
            WriteQuoted(sb, key);
    }

    private static void WriteQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }

    private static void NewLine(StringBuilder sb, SnbtOptions o, int depth)
    {
        if (o.Indent is null) return;
        sb.Append('\n');
        for (int i = 0; i < depth; i++) sb.Append(o.Indent);
    }
}
