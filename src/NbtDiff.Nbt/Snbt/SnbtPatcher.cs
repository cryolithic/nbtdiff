using fNbt;

namespace NbtDiff.Nbt.Snbt;

/// <summary>
/// Writes an edited tree back into its original SNBT text by changing only the text of what changed:
/// a changed value's text is replaced, a removed entry is cut out with its line, an added entry is
/// inserted beside its siblings in the file's own layout (indentation, commas or line breaks, colon
/// spacing, line endings). Comments, blank lines, key order and number formatting elsewhere are kept
/// byte for byte, so FTB and hand-edited files survive a save. The result is re-parsed and must equal
/// the edited tree, or <see cref="Patch"/> throws rather than write something else.
/// </summary>
public static class SnbtPatcher
{
    public static string Patch(string original, NbtTag edited)
    {
        var map = new SnbtSourceMap();
        var pristine = SnbtParser.Parse(original, map);
        var style = SnbtStyle.Detect(original, map);
        var edits = new List<Edit>();
        new Walker(original, map, style, edits).Patch(pristine, edited);

        var text = Apply(original, edits);
        if (!SnbtParser.TryParse(text, out var reparsed, out _) || !TagEquals(reparsed!, edited))
            throw new InvalidOperationException("The change cannot be written into this SNBT file without altering other content");
        return text;
    }

    private readonly record struct Edit(int Start, int End, string Text);

    private static string Apply(string original, List<Edit> edits)
    {
        if (edits.Count == 0) return original;
        var sb = new System.Text.StringBuilder(original.Length + 256);
        int at = 0;
        // Stable: several inserts at one position keep the order they were added in.
        foreach (var e in edits.OrderBy(e => e.Start).ThenBy(e => e.End))
        {
            if (e.Start < at) throw new InvalidOperationException("Overlapping SNBT edits");
            sb.Append(original, at, e.Start - at).Append(e.Text);
            at = e.End;
        }
        return sb.Append(original, at, original.Length - at).ToString();
    }

    private sealed class Walker(string text, SnbtSourceMap map, SnbtStyle style, List<Edit> edits)
    {
        public void Patch(NbtTag before, NbtTag after)
        {
            if (TagEquals(before, after)) return;
            switch (before, after)
            {
                case (NbtCompound b, NbtCompound a) when b.Count > 0:
                    PatchCompound(b, a);
                    return;
                case (NbtList b, NbtList a) when b.Count > 0 && (a.Count == 0 || a.ListType == b.ListType):
                    PatchList(b, a);
                    return;
                default:
                    Replace(before, after);
                    return;
            }
        }

        private void Replace(NbtTag before, NbtTag after)
        {
            var span = map[before];
            edits.Add(new Edit(span.ValueStart, span.ValueEnd, Render(after, LineIndent(span.EntryStart))));
        }

        private void PatchCompound(NbtCompound before, NbtCompound after)
        {
            NbtTag? lastKept = null;
            var added = new List<NbtTag>();
            foreach (var child in before.Tags)
            {
                if (after.Get(child.Name!) is { } now)
                {
                    Patch(child, now);
                    lastKept = child;
                }
                else Remove(child);
            }
            foreach (var child in after.Tags)
                if (!before.Contains(child.Name!)) added.Add(child);
            if (added.Count == 0) return;
            if (lastKept is null)
            {
                // Every old entry is gone: rewrite the compound in the file's style.
                edits.RemoveAll(e => e.Start >= map[before].ValueStart && e.End <= map[before].ValueEnd);
                Replace(before, after);
                return;
            }

            // FTB writes keys sorted; keep a sorted compound sorted, otherwise append after the last entry.
            var existing = before.Tags.Where(t => after.Contains(t.Name!)).ToList();
            bool sorted = existing.Zip(existing.Skip(1)).All(p => string.CompareOrdinal(p.First.Name, p.Second.Name) < 0);
            foreach (var child in added)
            {
                NbtTag? anchorBefore = null;
                if (sorted)
                    anchorBefore = existing.FirstOrDefault(t => string.CompareOrdinal(t.Name, child.Name) > 0);
                InsertEntry(before, anchorBefore, lastKept, Entry(child, ChildIndent(before)));
            }
        }

        private void PatchList(NbtList before, NbtList after)
        {
            int common = Math.Min(before.Count, after.Count);
            for (int i = 0; i < common; i++) Patch(before[i], after[i]);
            for (int i = common; i < before.Count; i++) Remove(before[i]);
            if (after.Count <= before.Count) return;
            if (common == 0)
            {
                edits.RemoveAll(e => e.Start >= map[before].ValueStart && e.End <= map[before].ValueEnd);
                Replace(before, after);
                return;
            }
            string indent = ChildIndent(before);
            for (int i = common; i < after.Count; i++)
                InsertEntry(before, null, before[common - 1], Render(after[i], indent));
        }

        private string Entry(NbtTag child, string indent) => SnbtWriter.Key(child.Name!) + style.Colon + Render(child, indent);

        /// <summary>Inserts <paramref name="entryText"/> before <paramref name="anchorBefore"/>'s entry, or after <paramref name="after"/>'s.</summary>
        private void InsertEntry(NbtTag container, NbtTag? anchorBefore, NbtTag after, string entryText)
        {
            bool multiline = style.Indent is not null && Contains(map[container], '\n');
            string indent = ChildIndent(container);
            if (anchorBefore is not null)
            {
                int at = multiline ? AboveLeadingComments(map[anchorBefore].EntryStart) : map[anchorBefore].EntryStart;
                string text = multiline
                    ? entryText + (style.Commas ? "," : "") + style.Newline + indent
                    : entryText + ", ";
                edits.Add(new Edit(at, at, text));
            }
            else
            {
                int at = map[after].ValueEnd;
                string sep = style.Commas || !multiline ? "," : "";
                string text = multiline ? sep + style.Newline + indent + entryText : sep + (style.Indent is null ? "" : " ") + entryText;
                edits.Add(new Edit(at, at, text));
            }
        }

        /// <summary>
        /// Where a new entry goes so that it lands above the <c>#</c> comment lines directly above
        /// <paramref name="entryStart"/> (those comments belong to that entry): the indentation end of
        /// the first such line, or <paramref name="entryStart"/> when there are none.
        /// </summary>
        private int AboveLeadingComments(int entryStart)
        {
            int at = entryStart;
            int lineStart = entryStart;
            while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
            while (lineStart > 0)
            {
                int prevEnd = lineStart - 1;                 // the '\n' ending the previous line
                int prevStart = prevEnd;
                while (prevStart > 0 && text[prevStart - 1] != '\n') prevStart--;
                int content = prevStart;
                while (content < prevEnd && text[content] is ' ' or '\t') content++;
                if (content >= prevEnd || text[content] != '#') break;
                at = content;
                lineStart = prevStart;
            }
            return at;
        }

        private void Remove(NbtTag child)
        {
            var span = map[child];
            int start = span.EntryStart, end = span.ValueEnd;
            // A following comma goes with the entry; for the last entry, the preceding one does.
            int after = SkipSpaces(end);
            if (after < text.Length && text[after] == ',')
                end = SkipSpaces(after + 1);
            else
            {
                int back = start - 1;
                while (back >= 0 && text[back] is ' ' or '\t' or '\r' or '\n') back--;
                if (back >= 0 && text[back] == ',') start = back;
            }
            // An entry alone on its line takes the whole line (and a trailing comment) with it.
            int lineStart = start;
            while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t') lineStart--;
            bool ownLine = lineStart == 0 || text[lineStart - 1] == '\n';
            int lineEnd = end;
            while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t') lineEnd++;
            if (lineEnd < text.Length && text[lineEnd] == '#')
                while (lineEnd < text.Length && text[lineEnd] != '\n') lineEnd++;
            if (ownLine && start == span.EntryStart && (lineEnd >= text.Length || text[lineEnd] is '\n' or '\r'))
            {
                if (lineEnd < text.Length && text[lineEnd] == '\r') lineEnd++;
                if (lineEnd < text.Length && text[lineEnd] == '\n') lineEnd++;
                edits.Add(new Edit(lineStart, lineEnd, ""));
                return;
            }
            edits.Add(new Edit(start, end, ""));
        }

        private string Render(NbtTag tag, string lineIndent) => SnbtWriter.Write(tag, style.ToOptions(lineIndent));

        /// <summary>Indentation of the line an entry starts on.</summary>
        private string LineIndent(int position)
        {
            int start = position;
            while (start > 0 && text[start - 1] != '\n') start--;
            int end = start;
            while (end < position && text[end] is ' ' or '\t') end++;
            return text[start..end];
        }

        private string ChildIndent(NbtTag container)
        {
            NbtTag? first = container switch
            {
                NbtCompound c => c.Tags.FirstOrDefault(),
                NbtList l when l.Count > 0 => l[0],
                _ => null,
            };
            return first is not null && Contains(map[container], '\n')
                ? LineIndent(map[first].EntryStart)
                : LineIndent(map[container].EntryStart) + (style.Indent ?? "");
        }

        private bool Contains(SnbtSpan span, char c) => text.IndexOf(c, span.ValueStart, span.ValueEnd - span.ValueStart) >= 0;

        private int SkipSpaces(int i)
        {
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            return i;
        }
    }

    /// <summary>Content equality: compound key order and the element type of empty lists do not matter; names of the compared roots are ignored.</summary>
    public static bool TagEquals(NbtTag a, NbtTag b)
    {
        if (a.TagType != b.TagType) return false;
        switch (a)
        {
            case NbtCompound ca:
            {
                var cb = (NbtCompound)b;
                if (ca.Count != cb.Count) return false;
                foreach (var child in ca.Tags)
                    if (cb.Get(child.Name!) is not { } other || !TagEquals(child, other)) return false;
                return true;
            }
            case NbtList la:
            {
                var lb = (NbtList)b;
                if (la.Count != lb.Count) return false;
                if (la.Count > 0 && la.ListType != lb.ListType) return false;
                for (int i = 0; i < la.Count; i++)
                    if (!TagEquals(la[i], lb[i])) return false;
                return true;
            }
            case NbtByteArray x: return x.Value.AsSpan().SequenceEqual(((NbtByteArray)b).Value);
            case NbtIntArray x: return x.Value.AsSpan().SequenceEqual(((NbtIntArray)b).Value);
            case NbtLongArray x: return x.Value.AsSpan().SequenceEqual(((NbtLongArray)b).Value);
            case NbtFloat x: return BitConverter.SingleToInt32Bits(x.Value) == BitConverter.SingleToInt32Bits(((NbtFloat)b).Value);
            case NbtDouble x: return BitConverter.DoubleToInt64Bits(x.Value) == BitConverter.DoubleToInt64Bits(((NbtDouble)b).Value);
            case NbtString x: return x.Value == ((NbtString)b).Value;
            case NbtByte x: return x.Value == ((NbtByte)b).Value;
            case NbtShort x: return x.Value == ((NbtShort)b).Value;
            case NbtInt x: return x.Value == ((NbtInt)b).Value;
            case NbtLong x: return x.Value == ((NbtLong)b).Value;
            default: return false;
        }
    }
}
