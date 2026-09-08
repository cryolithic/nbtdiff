namespace NbtDiff.App.Tree;

/// <summary>
/// Collects items reported from worker threads and hands them out as a de-duplicated batch when the UI
/// thread asks. A burst of changes to one row costs one refresh; nothing is ever dropped — an item added
/// after a drain is returned by the next drain.
/// </summary>
public sealed class ChangeCoalescer<T> where T : class
{
    private readonly object _gate = new();
    private HashSet<T> _pending = new(ReferenceEqualityComparer.Instance);
    private long _added;

    /// <summary>Total items ever added (including duplicates); for tests and diagnostics.</summary>
    public long Added => Interlocked.Read(ref _added);

    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0; }
    }

    public void Add(T item)
    {
        Interlocked.Increment(ref _added);
        lock (_gate) _pending.Add(item);
    }

    /// <summary>Returns everything added since the last drain, each item once, in no particular order.</summary>
    public IReadOnlyCollection<T> Drain()
    {
        HashSet<T> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            batch = _pending;
            _pending = new HashSet<T>(ReferenceEqualityComparer.Instance);
        }
        return batch;
    }
}
