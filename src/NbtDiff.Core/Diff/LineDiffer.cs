namespace NbtDiff.Core.Diff;

public enum LineDiffKind
{
    Unchanged,
    /// <summary>A left line paired with a right line inside the same hunk.</summary>
    Changed,
    /// <summary>Left only.</summary>
    Removed,
    /// <summary>Right only.</summary>
    Added,
}

/// <summary>One side-by-side row. Line numbers are 1-based; an absent side has null number and text.</summary>
public sealed record LineDiffRow(LineDiffKind Kind, int? LeftLine, string? LeftText, int? RightLine, string? RightText);

public sealed class LineDiffResult
{
    public IReadOnlyList<LineDiffRow> Rows { get; }
    /// <summary>Indices into <see cref="Rows"/> where each run of non-Unchanged rows begins.</summary>
    public IReadOnlyList<int> HunkStarts { get; }
    public int Changed { get; }
    public int Added { get; }
    public int Removed { get; }
    public bool HasChanges => HunkStarts.Count > 0;

    internal LineDiffResult(IReadOnlyList<LineDiffRow> rows, IReadOnlyList<int> hunkStarts, int changed, int added, int removed)
    {
        Rows = rows;
        HunkStarts = hunkStarts;
        Changed = changed;
        Added = added;
        Removed = removed;
    }
}

/// <summary>
/// Line diff for the text compare view: Myers' O(ND) algorithm in its linear-space (middle snake)
/// form, so a 5k-line file with thousands of changes still finishes in well under a second without
/// an N×M table. Line endings are normalized (CRLF, CR and LF are all line breaks), so files that
/// differ only in line endings diff clean.
/// </summary>
public static class LineDiffer
{
    /// <summary>Splits on any line ending; a trailing line break does not produce an empty last line.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    public static LineDiffResult Diff(string leftText, string rightText) => Diff(SplitLines(leftText), SplitLines(rightText));

    public static LineDiffResult Diff(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        // Intern lines so the core compares ints.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string s)
        {
            if (!ids.TryGetValue(s, out int id)) ids[s] = id = ids.Count;
            return id;
        }
        var a = new int[left.Count];
        for (int i = 0; i < a.Length; i++) a[i] = Id(left[i]);
        var b = new int[right.Count];
        for (int j = 0; j < b.Length; j++) b[j] = Id(right[j]);

        var ops = new List<Op>(a.Length + b.Length);
        new Myers(a, b, ops).Run(0, a.Length, 0, b.Length);
        return BuildRows(ops, left, right);
    }

    private enum OpKind : byte { Equal, Delete, Insert }
    private readonly record struct Op(OpKind Kind, int Index, int Other);

    // Deletes and inserts inside one hunk are paired up as Changed rows; leftovers are one-sided.
    private static LineDiffResult BuildRows(List<Op> ops, IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var rows = new List<LineDiffRow>(ops.Count);
        var hunks = new List<int>();
        int changed = 0, added = 0, removed = 0;
        var dels = new List<int>();
        var ins = new List<int>();

        void FlushHunk()
        {
            if (dels.Count == 0 && ins.Count == 0) return;
            hunks.Add(rows.Count);
            int paired = Math.Min(dels.Count, ins.Count);
            for (int k = 0; k < paired; k++)
                rows.Add(new LineDiffRow(LineDiffKind.Changed, dels[k] + 1, left[dels[k]], ins[k] + 1, right[ins[k]]));
            for (int k = paired; k < dels.Count; k++)
                rows.Add(new LineDiffRow(LineDiffKind.Removed, dels[k] + 1, left[dels[k]], null, null));
            for (int k = paired; k < ins.Count; k++)
                rows.Add(new LineDiffRow(LineDiffKind.Added, null, null, ins[k] + 1, right[ins[k]]));
            changed += paired;
            removed += dels.Count - paired;
            added += ins.Count - paired;
            dels.Clear();
            ins.Clear();
        }

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.Equal:
                    FlushHunk();
                    rows.Add(new LineDiffRow(LineDiffKind.Unchanged, op.Index + 1, left[op.Index], op.Other + 1, right[op.Other]));
                    break;
                case OpKind.Delete:
                    dels.Add(op.Index);
                    break;
                case OpKind.Insert:
                    ins.Add(op.Index);
                    break;
            }
        }
        FlushHunk();
        return new LineDiffResult(rows, hunks, changed, added, removed);
    }

    /// <summary>Linear-space Myers: strip common prefix/suffix, find the middle snake, recurse on both halves.</summary>
    private sealed class Myers
    {
        private readonly int[] _a, _b;
        private readonly List<Op> _ops;
        private readonly int[] _vf, _vb;
        private readonly int _offset;

        public Myers(int[] a, int[] b, List<Op> ops)
        {
            _a = a;
            _b = b;
            _ops = ops;
            int max = a.Length + b.Length + 1;
            _offset = max;
            _vf = new int[2 * max + 2];
            _vb = new int[2 * max + 2];
        }

        public void Run(int a0, int a1, int b0, int b1)
        {
            while (a0 < a1 && b0 < b1 && _a[a0] == _b[b0])
            {
                _ops.Add(new Op(OpKind.Equal, a0, b0));
                a0++; b0++;
            }
            int suffix = 0;
            while (a0 < a1 - suffix && b0 < b1 - suffix && _a[a1 - 1 - suffix] == _b[b1 - 1 - suffix])
                suffix++;
            int a1c = a1 - suffix, b1c = b1 - suffix;

            if (a0 == a1c)
            {
                for (int j = b0; j < b1c; j++) _ops.Add(new Op(OpKind.Insert, j, a0));
            }
            else if (b0 == b1c)
            {
                for (int i = a0; i < a1c; i++) _ops.Add(new Op(OpKind.Delete, i, b0));
            }
            else
            {
                var (x, y, u, v) = MiddleSnake(a0, a1c, b0, b1c);
                Run(a0, x, b0, y);
                for (int k = 0; k < u - x; k++) _ops.Add(new Op(OpKind.Equal, x + k, y + k));
                Run(u, a1c, v, b1c);
            }

            for (int k = 0; k < suffix; k++) _ops.Add(new Op(OpKind.Equal, a1c + k, b1c + k));
        }

        // Returns the middle snake (x, y) → (u, v) in absolute coordinates. Forward search uses
        // y = x - k; the backward search runs the same way on the reversed sequences, where a
        // forward diagonal k corresponds to backward diagonal delta - k.
        private (int X, int Y, int U, int V) MiddleSnake(int a0, int a1, int b0, int b1)
        {
            int n = a1 - a0, m = b1 - b0;
            int delta = n - m;
            bool odd = (delta & 1) != 0;
            int max = (n + m + 1) / 2 + 1;
            int[] vf = _vf, vb = _vb;
            int off = _offset;
            vf[off + 1] = 0;
            vb[off + 1] = 0;

            for (int d = 0; d <= max; d++)
            {
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && vf[off + k - 1] < vf[off + k + 1]) ? vf[off + k + 1] : vf[off + k - 1] + 1;
                    int y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && _a[a0 + x] == _b[b0 + y]) { x++; y++; }
                    vf[off + k] = x;
                    if (odd)
                    {
                        int kr = delta - k;
                        if (kr >= -(d - 1) && kr <= d - 1 && x + vb[off + kr] >= n)
                            return (a0 + x0, b0 + y0, a0 + x, b0 + y);
                    }
                }
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && vb[off + k - 1] < vb[off + k + 1]) ? vb[off + k + 1] : vb[off + k - 1] + 1;
                    int y = x - k;
                    int x0 = x, y0 = y;
                    while (x < n && y < m && _a[a1 - 1 - x] == _b[b1 - 1 - y]) { x++; y++; }
                    vb[off + k] = x;
                    if (!odd)
                    {
                        int kf = delta - k;
                        if (kf >= -d && kf <= d && x + vf[off + kf] >= n)
                            return (a1 - x, b1 - y, a1 - x0, b1 - y0);
                    }
                }
            }
            throw new InvalidOperationException("Myers middle snake not found");
        }
    }
}
