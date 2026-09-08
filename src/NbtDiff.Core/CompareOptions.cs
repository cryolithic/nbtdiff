namespace NbtDiff.Core;

/// <param name="DeepVerify">Run Tier 2 on rows whose bytes differ, so recompression does not show as a change.</param>
/// <param name="CompoundOrderMatters">Treat reordered compound keys as a difference.</param>
/// <param name="ExcludeGlobs">Patterns to skip; null means the default (<c>session.lock</c>). See <see cref="GlobMatcher"/>.</param>
/// <param name="MaxParallelism">Worker count; 0 uses the processor count.</param>
/// <param name="IgnoredTags">Tag paths that are not content (see <see cref="TagIgnoreSet"/>); null means <see cref="TagIgnoreSet.DefaultPaths"/> (<c>LastUpdate</c>).</param>
public sealed record CompareOptions(
    bool DeepVerify = true,
    bool CompoundOrderMatters = false,
    IReadOnlyList<string>? ExcludeGlobs = null,
    int MaxParallelism = 0,
    IReadOnlyList<string>? IgnoredTags = null)
{
    public static readonly IReadOnlyList<string> DefaultExcludes = ["session.lock"];

    public IReadOnlyList<string> EffectiveExcludes => ExcludeGlobs ?? DefaultExcludes;
    public TagIgnoreSet EffectiveIgnoredTags => IgnoredTags is null ? TagIgnoreSet.Default : TagIgnoreSet.Parse(IgnoredTags);
    public int EffectiveParallelism => MaxParallelism > 0 ? MaxParallelism : Environment.ProcessorCount;
}

/// <summary>Snapshot of scan progress. Tier 2 totals grow while Tier 1 runs.</summary>
public readonly record struct ScanProgress(int Files, int Tier1Done, int Tier2Queued, int Tier2Done, bool Completed, bool Cancelled)
{
    public bool Tier1Complete => Tier1Done >= Files;
    /// <summary>0..1, counting Tier 2 work as it is discovered.</summary>
    public double Fraction
    {
        get
        {
            int total = Files + Tier2Queued;
            return total == 0 ? 1 : (double)(Tier1Done + Tier2Done) / total;
        }
    }
}

/// <summary>
/// Minimal globs for excludes: <c>*</c> and <c>?</c> on a file name; a pattern containing <c>/</c> is
/// matched against the whole relative path, where <c>**</c> spans directories. Case-insensitive.
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string name, string relativePath)
    {
        bool pathPattern = pattern.Contains('/');
        var regex = ToRegex(pattern, pathPattern);
        return regex.IsMatch(pathPattern ? relativePath : name);
    }

    public static bool IsExcluded(IEnumerable<string> patterns, string name, string relativePath)
    {
        foreach (var p in patterns)
            if (IsMatch(p, name, relativePath)) return true;
        return false;
    }

    private static readonly Dictionary<(string, bool), System.Text.RegularExpressions.Regex> Cache = new();

    private static System.Text.RegularExpressions.Regex ToRegex(string pattern, bool pathPattern)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((pattern, pathPattern), out var cached)) return cached;
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                switch (c)
                {
                    case '*' when pathPattern && i + 1 < pattern.Length && pattern[i + 1] == '*':
                        sb.Append(".*");
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/') i++; // "**/" also matches zero segments
                        break;
                    case '*': sb.Append(pathPattern ? "[^/]*" : ".*"); break;
                    case '?': sb.Append(pathPattern ? "[^/]" : "."); break;
                    default: sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString())); break;
                }
            }
            sb.Append('$');
            var regex = new System.Text.RegularExpressions.Regex(sb.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            Cache[(pattern, pathPattern)] = regex;
            return regex;
        }
    }
}

/// <summary>Ordinal comparison except that digit runs compare numerically: <c>r.2.0</c> before <c>r.10.0</c>.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var dx = x.AsSpan(si, i - si).TrimStart('0');
                var dy = y.AsSpan(sj, j - sj).TrimStart('0');
                if (dx.Length != dy.Length) return dx.Length.CompareTo(dy.Length);
                int c = dx.SequenceCompareTo(dy);
                if (c != 0) return c;
                // Equal values: fewer leading zeros first, for a stable total order.
                if (i - si != j - sj) return (i - si).CompareTo(j - sj);
            }
            else
            {
                int c = x[i].CompareTo(y[j]);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
