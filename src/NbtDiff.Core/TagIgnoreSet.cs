namespace NbtDiff.Core;

/// <summary>
/// Tag paths that are not content: they are skipped by the canonical hasher and the differ alike, so
/// hash/diff agreement is preserved. A path is compound keys joined by <c>/</c>, matched from the
/// root; lists are transparent (list items are matched as if they were their parent), and <c>*</c>
/// matches any single key. Default: <c>LastUpdate</c> — the world tick a chunk was last saved, which
/// Minecraft rewrites on every save even when nothing in the chunk changed — at the root (1.18+)
/// and under <c>Level</c> (older worlds).
/// </summary>
public sealed class TagIgnoreSet
{
    public static readonly IReadOnlyList<string> DefaultPaths = ["LastUpdate", "Level/LastUpdate"];
    public static readonly TagIgnoreSet Empty = new([]);
    public static readonly TagIgnoreSet Default = Parse(DefaultPaths);

    public IReadOnlyList<string> Paths { get; }
    public Node? Root { get; }
    public bool IsEmpty => Root is null;

    private TagIgnoreSet(IReadOnlyList<string> paths)
    {
        Paths = paths;
        if (paths.Count == 0) return;
        var root = new Node();
        foreach (var path in paths)
        {
            var node = root;
            foreach (var segment in path.Split('/'))
                node = node.GetOrAdd(segment);
            node.Ignored = true;
        }
        Root = root;
    }

    /// <summary>Blank entries are dropped, segments trimmed; an empty result is <see cref="Empty"/>.</summary>
    public static TagIgnoreSet Parse(IEnumerable<string>? paths)
    {
        if (paths is null) return Empty;
        var clean = paths
            .Select(p => string.Join('/', (p ?? "").Split('/').Select(s => s.Trim()).Where(s => s.Length > 0)))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return clean.Length == 0 ? Empty : new TagIgnoreSet(clean);
    }

    /// <summary>Splits a comma- or newline-separated user string.</summary>
    public static TagIgnoreSet ParseList(string? text) =>
        Parse((text ?? "").Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries));

    public override string ToString() => string.Join(", ", Paths);

    /// <summary>One trie level. Walk with <see cref="Child"/> at each compound key; a null result means nothing below is ignored.</summary>
    public sealed class Node
    {
        private Dictionary<string, Node>? _children;
        private Node? _wildcard;

        /// <summary>The key at this exact path is ignored (its whole subtree).</summary>
        public bool Ignored { get; internal set; }

        public Node? Child(string? key)
        {
            if (key is not null && _children is not null && _children.TryGetValue(key, out var n)) return n;
            return _wildcard;
        }

        /// <summary>True when <paramref name="key"/> under this node should be skipped entirely.</summary>
        public bool Ignores(string? key) => Child(key) is { Ignored: true };

        internal Node GetOrAdd(string segment)
        {
            if (segment == "*") return _wildcard ??= new Node();
            _children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
            if (!_children.TryGetValue(segment, out var n))
                _children[segment] = n = new Node();
            return n;
        }
    }
}
