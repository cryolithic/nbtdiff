using fNbt;

namespace NbtDiff.Nbt.Snbt;

/// <summary>Where one parsed value sits in the source text. <c>End</c> is exclusive.</summary>
/// <param name="EntryStart">Start of the whole entry: the key of a compound entry, else <paramref name="ValueStart"/>.</param>
internal readonly record struct SnbtSpan(int EntryStart, int ValueStart, int ValueEnd);

/// <summary>Source positions and layout facts recorded while parsing, for splicing edits back into the text.</summary>
internal sealed class SnbtSourceMap
{
    private readonly Dictionary<NbtTag, SnbtSpan> _spans = new(ReferenceEqualityComparer.Instance);

    /// <summary>A comma followed by a line break: Minecraft's multi-line layout.</summary>
    public bool SawCommaLineBreak { get; private set; }
    /// <summary>A line break with no comma: FTB's multi-line layout. (Both use commas inside one-line containers.)</summary>
    public bool SawBareLineBreak { get; private set; }
    /// <summary>The text between the first key and its value (":" or ": "); null before any compound entry.</summary>
    public string? Colon { get; private set; }

    public SnbtSpan this[NbtTag tag] => _spans[tag];

    public void Record(NbtTag tag, SnbtSpan span) => _spans[tag] = span;

    public void RecordSeparator(bool comma, bool lineBreak)
    {
        if (!lineBreak) return;
        if (comma) SawCommaLineBreak = true;
        else SawBareLineBreak = true;
    }

    public void RecordColon(string colon) => Colon ??= colon;
}

/// <summary>How a particular SNBT file is laid out, so text written into it matches its neighbours.</summary>
internal sealed record SnbtStyle(string? Indent, bool Commas, string Colon, string Newline, bool SpacedEmpty)
{
    public static SnbtStyle Detect(string text, SnbtSourceMap map)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        // The smallest leading whitespace of an indented line is one indent step.
        string? indent = null;
        foreach (var line in text.Split('\n'))
        {
            int n = 0;
            while (n < line.Length && line[n] is ' ' or '\t') n++;
            if (n == 0 || n == line.Length || line[n] == '\r') continue;
            if (indent is null || n < indent.Length) indent = line[..n];
        }
        if (indent is not null && indent.Contains('\t')) indent = "\t";
        bool commas = map.SawCommaLineBreak || !map.SawBareLineBreak;
        bool spacedEmpty = text.Contains("[ ]") || text.Contains("{ }");
        return new SnbtStyle(indent, commas, map.Colon ?? (indent is null ? ":" : ": "), newline, spacedEmpty);
    }

    public SnbtOptions ToOptions(string baseIndent) =>
        new(Indent: Indent, Commas: Commas || Indent is null, Colon: Colon, Newline: Newline, BaseIndent: baseIndent, SpacedEmpty: SpacedEmpty);
}
