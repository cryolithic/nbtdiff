using System.Globalization;
using System.Text;
using fNbt;

namespace NbtDiff.Nbt.Snbt;

/// <param name="Indent">One indent step; null writes everything on one line.</param>
/// <param name="Commas">Separate entries with commas (Minecraft); false separates them with line breaks only (FTB). Ignored, always true, on one line.</param>
/// <param name="Colon">Between a key and its value.</param>
/// <param name="BaseIndent">Prefix for every line after the first, when the value is written into an already indented position.</param>
/// <param name="SpacedEmpty">Write empty containers as <c>[ ]</c> / <c>{ }</c> (FTB) instead of <c>[]</c> / <c>{}</c>.</param>
public sealed record SnbtOptions(string? Indent = null, bool Commas = true, string? Colon = null, string Newline = "\n", string BaseIndent = "", bool SpacedEmpty = false)
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
            case NbtFloat f: sb.Append(JavaNumber(f.Value.ToString("R", CultureInfo.InvariantCulture))).Append('f'); break;
            case NbtDouble d: sb.Append(JavaNumber(d.Value.ToString("R", CultureInfo.InvariantCulture))).Append('d'); break;
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
        if (compound.Count == 0)
        {
            sb.Append(o.SpacedEmpty ? "{ }" : "{}");
            return;
        }
        sb.Append('{');
        bool first = true;
        foreach (var child in compound.Tags)
        {
            if (!first && (o.Commas || o.Indent is null)) sb.Append(',');
            first = false;
            NewLine(sb, o, depth + 1);
            WriteKey(sb, child.Name!);
            sb.Append(o.Colon ?? (o.Indent is null ? ":" : ": "));
            Write(sb, child, o, depth + 1);
        }
        NewLine(sb, o, depth);
        sb.Append('}');
    }

    private static void WriteList(StringBuilder sb, NbtList list, SnbtOptions o, int depth)
    {
        if (list.Count == 0)
        {
            sb.Append(o.SpacedEmpty ? "[ ]" : "[]");
            return;
        }
        sb.Append('[');
        bool first = true;
        foreach (var item in list)
        {
            if (!first && (o.Commas || o.Indent is null)) sb.Append(',');
            first = false;
            NewLine(sb, o, depth + 1);
            Write(sb, item, o, depth + 1);
        }
        NewLine(sb, o, depth);
        sb.Append(']');
    }

    // Arrays stay on one line regardless of indentation: they can be thousands of elements.
    private static void WriteArray(StringBuilder sb, char kind, IEnumerable<string> items)
    {
        sb.Append('[').Append(kind).Append(';');
        sb.AppendJoin(',', items);
        sb.Append(']');
    }

    /// <summary>A compound key as SNBT: bare when it can be, quoted otherwise.</summary>
    internal static string Key(string key)
    {
        var sb = new StringBuilder();
        WriteKey(sb, key);
        return sb.ToString();
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
        sb.Append(o.Newline).Append(o.BaseIndent);
        for (int i = 0; i < depth; i++) sb.Append(o.Indent);
    }

    /// <summary>
    /// Java's Double/Float.toString layout, which Minecraft and FTB write: plain decimal with at least
    /// one fractional digit for 1e-3 ≤ |v| &lt; 1e7 (<c>5.0</c>, <c>0.001</c>), otherwise
    /// <c>d.dddE±n</c> (<c>1.0E7</c>, <c>1.5E-5</c>). <paramref name="shortest"/> is .NET's shortest
    /// round-trip text of the same value, which supplies the digits.
    /// </summary>
    internal static string JavaNumber(string shortest)
    {
        if (shortest is "NaN" or "∞" or "-∞" || shortest.Contains("Infinity")) return shortest;
        bool negative = shortest.StartsWith('-');
        string body = negative ? shortest[1..] : shortest;
        int e = body.IndexOfAny(['E', 'e']);
        int exp = e < 0 ? 0 : int.Parse(body[(e + 1)..], CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? body : body[..e];
        int dot = mantissa.IndexOf('.');
        string digits = mantissa.Replace(".", "");
        int pointAt = (dot < 0 ? mantissa.Length : dot) + exp;   // decimal point position within digits
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointAt--; }
        digits = digits[lead..].TrimEnd('0');
        if (digits.Length == 0) return negative ? "-0.0" : "0.0";

        int magnitude = pointAt - 1;   // value = d.ddd × 10^magnitude
        string text;
        if (magnitude is >= -3 and < 7)
        {
            string intPart = pointAt <= 0 ? "0" : digits.PadRight(pointAt, '0')[..pointAt];
            string frac = pointAt <= 0 ? new string('0', -pointAt) + digits : digits.Length > pointAt ? digits[pointAt..] : "";
            text = intPart + "." + (frac.Length == 0 ? "0" : frac);
        }
        else
        {
            text = digits[..1] + "." + (digits.Length > 1 ? digits[1..] : "0") + "E" + magnitude.ToString(CultureInfo.InvariantCulture);
        }
        return negative ? "-" + text : text;
    }
}
