using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;

namespace NbtDiff.Nbt.Snbt;

/// <summary>
/// Recursive-descent parser for stringified NBT as written by Java Edition (commands, .snbt files):
/// <c>{key:value,...}</c>, <c>[a,b]</c>, <c>[B;1b,2b]</c>, <c>[I;...]</c>, <c>[L;...]</c>, quoted
/// strings with backslash escapes, numeric suffixes b/s/l/f/d, and <c>true</c>/<c>false</c>.
/// Tags in lists and arrays are unnamed; compound children carry their key as the name.
/// </summary>
public sealed partial class SnbtParser
{
    private readonly string _text;
    private readonly SnbtSourceMap? _map;
    private int _pos;

    private SnbtParser(string text, SnbtSourceMap? map = null)
    {
        _text = text;
        _map = map;
    }

    /// <summary>Parses a complete SNBT value; trailing content other than whitespace is an error.</summary>
    /// <exception cref="SnbtParseException">On any syntax error.</exception>
    public static NbtTag Parse(string text)
    {
        var parser = new SnbtParser(text);
        parser.SkipWhitespace();
        var tag = parser.ParseValue(name: null, entryStart: parser._pos);
        parser.SkipWhitespace();
        if (parser._pos != text.Length)
            parser.Fail("Unexpected trailing content");
        return tag;
    }

    /// <summary>Parses like <see cref="Parse(string)"/> and records where every value sits in <paramref name="text"/>.</summary>
    internal static NbtTag Parse(string text, SnbtSourceMap map)
    {
        var parser = new SnbtParser(text, map);
        parser.SkipWhitespace();
        var tag = parser.ParseValue(name: null, entryStart: parser._pos);
        parser.SkipWhitespace();
        if (parser._pos != text.Length)
            parser.Fail("Unexpected trailing content");
        return tag;
    }

    public static bool TryParse(string text, out NbtTag? tag, out SnbtParseException? error)
    {
        try
        {
            tag = Parse(text);
            error = null;
            return true;
        }
        catch (SnbtParseException e)
        {
            tag = null;
            error = e;
            return false;
        }
    }

    /// <param name="entryStart">Where the whole entry begins: the key of a compound entry, else the value itself.</param>
    private NbtTag ParseValue(string? name, int entryStart)
    {
        if (AtEnd) Fail("Unexpected end of input, expected a value");
        int start = _pos;
        NbtTag tag = Peek switch
        {
            '{' => ParseCompound(name),
            '[' => ParseListOrArray(name),
            '"' or '\'' => new NbtString(name, ParseQuotedString()),
            _ => ParseScalar(name),
        };
        _map?.Record(tag, new SnbtSpan(entryStart, start, _pos));
        return tag;
    }

    private NbtCompound ParseCompound(string? name)
    {
        Expect('{');
        var compound = new NbtCompound(name);
        SkipWhitespace();
        if (TryConsume('}')) return compound;

        while (true)
        {
            SkipWhitespace();
            int entryStart = _pos;
            string key = ParseKey();
            int keyEnd = _pos;
            SkipWhitespace();
            Expect(':');
            SkipWhitespace();
            _map?.RecordColon(_text[keyEnd.._pos]);
            var child = ParseValue(key, entryStart);
            if (compound.Contains(key)) Fail($"Duplicate key '{key}'");
            compound.Add(child);
            if (ClosedAfterSeparator('}')) return compound;
        }
    }

    private string ParseKey()
    {
        if (AtEnd) Fail("Unexpected end of input, expected a key");
        if (Peek is '"' or '\'') return ParseQuotedString();
        // Keys are not numbers, so they may contain anything up to the ':' except whitespace and
        // structural characters — real FTB lang files have non-ASCII letters pasted into keys.
        int start = _pos;
        while (!AtEnd && !char.IsWhiteSpace(Peek) && Peek is not (':' or '{' or '}' or '[' or ']' or ',' or '"' or '\''))
            _pos++;
        string key = _text[start.._pos];
        if (key.Length == 0) Fail("Expected a key");
        return key;
    }

    private NbtTag ParseListOrArray(string? name)
    {
        Expect('[');
        SkipWhitespace();
        if (_pos + 1 < _text.Length && _text[_pos + 1] == ';' && Peek is 'B' or 'I' or 'L')
        {
            char kind = Peek;
            _pos += 2;
            return ParseArray(name, kind);
        }

        var list = new NbtList(name);
        if (TryConsume(']')) return list;
        while (true)
        {
            SkipWhitespace();
            int itemStart = _pos;
            var item = ParseValue(name: null, itemStart);
            if (list.Count > 0 && item.TagType != list.ListType)
                Fail($"List of {list.ListType} cannot contain a {item.TagType}", itemStart);
            list.Add(item);
            if (ClosedAfterSeparator(']')) return list;
        }
    }

    private NbtTag ParseArray(string? name, char kind)
    {
        var values = new List<long>();
        SkipWhitespace();
        if (!TryConsume(']'))
        {
            while (true)
            {
                SkipWhitespace();
                int itemStart = _pos;
                var item = ParseValue(name: null, itemStart);
                long v = item switch
                {
                    NbtByte b => (sbyte)b.Value,   // fNbt stores bytes unsigned; SNBT bytes are signed
                    NbtShort s => s.Value,
                    NbtInt i => i.Value,
                    NbtLong l => l.Value,
                    _ => Fail<long>($"Array element must be an integer, got {item.TagType}", itemStart),
                };
                switch (kind)
                {
                    case 'B' when v is < sbyte.MinValue or > sbyte.MaxValue:
                        Fail($"Byte array element {v} out of range", itemStart); break;
                    case 'I' when v is < int.MinValue or > int.MaxValue:
                        Fail($"Int array element {v} out of range", itemStart); break;
                }
                values.Add(v);
                if (ClosedAfterSeparator(']')) break;
            }
        }
        return kind switch
        {
            'B' => new NbtByteArray(name, values.Select(v => unchecked((byte)(sbyte)v)).ToArray()),
            'I' => new NbtIntArray(name, values.Select(v => (int)v).ToArray()),
            _ => new NbtLongArray(name, values.ToArray()),
        };
    }

    private NbtTag ParseScalar(string? name)
    {
        int start = _pos;
        string run = ReadUnquotedRun();
        if (run.Length == 0) Fail($"Unexpected character '{Peek}'");

        if (run == "true") return new NbtByte(name, 1);
        if (run == "false") return new NbtByte(name, 0);

        var m = NumberPattern().Match(run);
        if (m.Success)
        {
            string digits = m.Groups["num"].Value;
            char suffix = m.Groups["suffix"].Success ? char.ToLowerInvariant(m.Groups["suffix"].Value[0]) : '\0';
            bool isFloatSyntax = digits.Contains('.') || digits.Contains('e') || digits.Contains('E');
            try
            {
                switch (suffix)
                {
                    case 'b': return new NbtByte(name, unchecked((byte)sbyte.Parse(digits, CultureInfo.InvariantCulture)));
                    case 's': return new NbtShort(name, short.Parse(digits, CultureInfo.InvariantCulture));
                    case 'l': return new NbtLong(name, long.Parse(digits, CultureInfo.InvariantCulture));
                    case 'f': return new NbtFloat(name, float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture));
                    case 'd': return new NbtDouble(name, double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture));
                    default:
                        if (isFloatSyntax)
                            return new NbtDouble(name, double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture));
                        if (int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i))
                            return new NbtInt(name, i);
                        break; // too big for an int and no suffix: Minecraft treats it as a string
                }
            }
            catch (OverflowException)
            {
                Fail($"Number {run} does not fit its type", start);
            }
        }
        return new NbtString(name, run);
    }

    private string ParseQuotedString()
    {
        char quote = Next();
        var sb = new StringBuilder();
        while (true)
        {
            if (AtEnd) Fail("Unterminated string");
            char c = Next();
            if (c == quote)
            {
                if (StringEndsHere()) return sb.ToString();
                sb.Append(c);   // unescaped quote inside the text (see StringEndsHere)
                continue;
            }
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (AtEnd) Fail("Unterminated escape sequence");
            char e = Next();
            switch (e)
            {
                case '\\' or '"' or '\'': sb.Append(e); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 's': sb.Append(' '); break;
                case 'u':
                    if (_pos + 4 > _text.Length) Fail("Truncated \\u escape");
                    sb.Append((char)int.Parse(_text.AsSpan(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _pos += 4;
                    break;
                default:
                    Fail($"Unknown escape '\\{e}'", _pos - 2);
                    break;
            }
        }
    }

    private string ReadUnquotedRun()
    {
        int start = _pos;
        while (!AtEnd && IsUnquotedChar(Peek)) _pos++;
        return _text[start.._pos];
    }

    /// <summary>True for characters allowed in an unquoted SNBT string or key.</summary>
    public static bool IsUnquotedChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.' or '+';

    private bool AtEnd => _pos >= _text.Length;
    private char Peek => _text[_pos];
    private char Next() => _text[_pos++];

    /// <summary>Skips whitespace; true when a line break was crossed (a separator in FTB's dialect).</summary>
    private bool SkipWhitespace()
    {
        bool newline = false;
        while (!AtEnd)
        {
            if (char.IsWhiteSpace(Peek))
            {
                if (Peek is '\n' or '\r') newline = true;
                _pos++;
            }
            else if (Peek == '#')
            {
                // FTB config files carry '#' line comments; '#' cannot start a value, so this is unambiguous.
                while (!AtEnd && Peek is not ('\n' or '\r')) _pos++;
                newline = true;
            }
            else break;
        }
        return newline;
    }

    // A closing quote is only the end of the string when what follows (after spaces) can follow a
    // string: a separator, a closing bracket, ':' (it was a key), a comment, a line break, or the end.
    // Hand-edited FTB lang files contain unescaped quotes inside text; strict SNBT never has a quote in
    // any other position, so treating those as literal changes nothing for valid input.
    private bool StringEndsHere()
    {
        int i = SkipSpaces(_pos);
        if (i >= _text.Length) return true;
        char c = _text[i];
        if (c is ']' or '}' or ':' or '#' or '\n' or '\r') return true;
        if (c != ',') return false;

        // After a comma the next thing must look like an entry: a quoted or nested value, a close, a
        // line break, or a bare token that is a key (followed by ':') or a whole value (followed by a
        // separator). `"alto", la Tiza Azul."` fails this — `la` is followed by more words — so the
        // quote before the comma is text.
        i = SkipSpaces(i + 1);
        if (i >= _text.Length) return true;
        c = _text[i];
        if (c is '"' or '\'' or '{' or '[' or ']' or '}' or '\n' or '\r' or '#') return true;
        while (i < _text.Length && !char.IsWhiteSpace(_text[i]) && _text[i] is not (':' or ',' or ']' or '}' or '"' or '\''))
            i++;
        i = SkipSpaces(i);
        return i >= _text.Length || _text[i] is ':' or ',' or ']' or '}' or '\n' or '\r' or '#';
    }

    private int SkipSpaces(int i)
    {
        while (i < _text.Length && (_text[i] == ' ' || _text[i] == '\t')) i++;
        return i;
    }

    /// <summary>
    /// After an entry: returns true if the container closed. Otherwise requires a separator — a comma
    /// (Minecraft) or a line break (FTB Quests/Teams write comma-less SNBT) — and leaves the position at
    /// the next entry. A trailing comma before the closing bracket is accepted.
    /// </summary>
    private bool ClosedAfterSeparator(char close)
    {
        bool newline = SkipWhitespace();
        if (AtEnd) Fail($"Unexpected end of input, expected '{close}'");
        if (TryConsume(close)) return true;
        if (TryConsume(','))
        {
            bool lineBreak = SkipWhitespace();
            _map?.RecordSeparator(comma: true, lineBreak);
            return TryConsume(close);
        }
        if (!newline) Fail($"Expected ',' or '{close}'");
        _map?.RecordSeparator(comma: false, lineBreak: true);
        return false;
    }

    private bool TryConsume(char c)
    {
        if (AtEnd || Peek != c) return false;
        _pos++;
        return true;
    }

    private void Expect(char c)
    {
        if (AtEnd) Fail($"Unexpected end of input, expected '{c}'");
        if (Peek != c) Fail($"Expected '{c}' but found '{Peek}'");
        _pos++;
    }

    [DoesNotReturn]
    private void Fail(string message, int? position = null) =>
        throw new SnbtParseException(message, position ?? _pos);

    [DoesNotReturn]
    private T Fail<T>(string message, int position) => throw new SnbtParseException(message, position);

    // Sign, then either an integer or a float body (digits with optional fraction, or leading dot),
    // optional exponent, optional one-letter type suffix. Must match the whole unquoted run.
    [GeneratedRegex(@"^(?<num>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)(?<suffix>[bBsSlLfFdD])?$")]
    private static partial Regex NumberPattern();
}
