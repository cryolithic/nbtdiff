namespace NbtDiff.Core;

public enum RowStatus
{
    Pending,
    Same,
    /// <summary>Bytes differ; content not yet verified (Tier 2 pending).</summary>
    ProbablyDifferent,
    Different,
    LeftOnly,
    RightOnly,
    Error,
}

/// <summary>One side of a row. Fingerprints are filled in by the scan.</summary>
public sealed class FileSide(string fullPath, long size, DateTime modified)
{
    public string FullPath { get; } = fullPath;
    public long Size { get; } = size;
    /// <summary>Last write time, UTC. Never part of equality.</summary>
    public DateTime Modified { get; } = modified;
    public FileFingerprint? Fingerprint { get; internal set; }
}

/// <summary>File counts by status. Directories are not counted, only the files under them.</summary>
public readonly record struct RowCounts(int Pending, int Same, int ProbablyDifferent, int Different, int LeftOnly, int RightOnly, int Error)
{
    public int Total => Pending + Same + ProbablyDifferent + Different + LeftOnly + RightOnly + Error;
    public int NonSame => ProbablyDifferent + Different + LeftOnly + RightOnly + Error;

    public static RowCounts Of(RowStatus status) => status switch
    {
        RowStatus.Pending => new(1, 0, 0, 0, 0, 0, 0),
        RowStatus.Same => new(0, 1, 0, 0, 0, 0, 0),
        RowStatus.ProbablyDifferent => new(0, 0, 1, 0, 0, 0, 0),
        RowStatus.Different => new(0, 0, 0, 1, 0, 0, 0),
        RowStatus.LeftOnly => new(0, 0, 0, 0, 1, 0, 0),
        RowStatus.RightOnly => new(0, 0, 0, 0, 0, 1, 0),
        RowStatus.Error => new(0, 0, 0, 0, 0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RowCounts operator +(RowCounts a, RowCounts b) =>
        new(a.Pending + b.Pending, a.Same + b.Same, a.ProbablyDifferent + b.ProbablyDifferent, a.Different + b.Different,
            a.LeftOnly + b.LeftOnly, a.RightOnly + b.RightOnly, a.Error + b.Error);

    public static RowCounts operator -(RowCounts a, RowCounts b) =>
        new(a.Pending - b.Pending, a.Same - b.Same, a.ProbablyDifferent - b.ProbablyDifferent, a.Different - b.Different,
            a.LeftOnly - b.LeftOnly, a.RightOnly - b.RightOnly, a.Error - b.Error);

    public override string ToString() =>
        $"{Same} same · {ProbablyDifferent} probably different · {Different} different · {LeftOnly} left-only · {RightOnly} right-only · {Error} error · {Pending} pending";
}

/// <summary>
/// A file or directory present on at least one side. Rows are built once, then mutated in place by
/// the scan; observe <see cref="CompareRoot.RowChanged"/> for updates. Status and counts of a
/// directory row are derived from the files beneath it.
/// </summary>
public sealed class CompareRow
{
    private readonly object _gate = new();
    private readonly List<CompareRow> _children = [];
    private RowStatus _status;
    private string? _error;
    private RowCounts _counts;

    /// <summary>Relative to the roots, forward slashes; "" for the root row.</summary>
    public string RelativePath { get; }
    public string Name { get; }
    public FileKind Kind { get; }
    public bool IsDirectory => Kind == FileKind.Directory;
    public FileSide? Left { get; }
    public FileSide? Right { get; }
    public CompareRow? Parent { get; }
    public IReadOnlyList<CompareRow> Children => _children;

    public RowStatus Status { get { lock (_gate) return _status; } }
    public string? Error { get { lock (_gate) return _error; } }
    /// <summary>For a file, its own status as counts; for a directory, the sum over all descendant files.</summary>
    public RowCounts Counts { get { lock (_gate) return _counts; } }

    internal CompareRow(CompareRow? parent, string relativePath, string name, FileKind kind, FileSide? left, FileSide? right, RowStatus initial, string? error = null)
    {
        Parent = parent;
        RelativePath = relativePath;
        Name = name;
        Kind = kind;
        Left = left;
        Right = right;
        _status = initial;
        _error = error;
        _counts = kind == FileKind.Directory ? default : RowCounts.Of(initial);
    }

    internal void AddChild(CompareRow child) => _children.Add(child);

    /// <summary>Called once the subtree is complete: folds children into this directory's counts and status.</summary>
    internal void SealDirectory()
    {
        var counts = default(RowCounts);
        foreach (var c in _children) counts += c.Counts;
        _counts = counts;
        if (_status != RowStatus.Error) _status = DeriveDirectoryStatus();
    }

    /// <summary>
    /// Sets a file row's final (or intermediate) status and propagates the count delta up the tree.
    /// Returns the rows whose visible state changed, this row first, so the caller can raise events
    /// outside any lock.
    /// </summary>
    internal List<CompareRow> SetStatus(RowStatus status, string? error = null)
    {
        var changed = new List<CompareRow>(4);
        RowCounts delta;
        lock (_gate)
        {
            if (_status == status && _error == error) return changed;
            delta = RowCounts.Of(status) - RowCounts.Of(_status);
            _status = status;
            _error = error;
            _counts = RowCounts.Of(status);
        }
        changed.Add(this);

        for (var dir = Parent; dir is not null; dir = dir.Parent)
        {
            bool visible;
            lock (dir._gate)
            {
                dir._counts += delta;
                var before = dir._status;
                if (dir._status != RowStatus.Error && dir.Left is not null && dir.Right is not null)
                    dir._status = dir.DeriveDirectoryStatus();
                visible = true; // counts changed, so the row's display changes even if the status didn't
                _ = before;
            }
            if (visible) changed.Add(dir);
        }
        return changed;
    }

    private RowStatus DeriveDirectoryStatus()
    {
        if (Left is null) return RowStatus.RightOnly;
        if (Right is null) return RowStatus.LeftOnly;
        var c = _counts;
        if (c.Different > 0 || c.LeftOnly > 0 || c.RightOnly > 0) return RowStatus.Different;
        if (c.ProbablyDifferent > 0) return RowStatus.ProbablyDifferent;
        if (c.Error > 0) return RowStatus.Error;
        if (c.Pending > 0) return RowStatus.Pending;
        return RowStatus.Same;
    }

    /// <summary>Depth-first, this row excluded.</summary>
    public IEnumerable<CompareRow> Descendants()
    {
        foreach (var child in _children)
        {
            yield return child;
            foreach (var d in child.Descendants()) yield return d;
        }
    }

    public override string ToString() => $"{(IsDirectory ? "dir " : "")}{(RelativePath.Length == 0 ? "<root>" : RelativePath)}: {Status}";
}
