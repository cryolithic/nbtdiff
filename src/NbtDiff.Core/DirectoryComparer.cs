using System.Threading.Channels;

namespace NbtDiff.Core;

/// <summary>
/// Compares two directory trees. <see cref="Prepare"/> builds the row tree from directory listings
/// alone (cheap, synchronous); <see cref="CompareRoot.Run"/> then streams fingerprints through two
/// tiers with bounded parallelism. <see cref="Start"/> does both.
/// </summary>
public sealed class DirectoryComparer
{
    private readonly IFingerprinter _fingerprinter;
    private readonly CompareOptions _options;

    public DirectoryComparer(CompareOptions? options = null)
        : this(Fingerprinter.For(options ?? new CompareOptions()), options) { }

    public DirectoryComparer(IFingerprinter fingerprinter, CompareOptions? options = null)
    {
        _fingerprinter = fingerprinter;
        _options = options ?? new CompareOptions();
    }

    public CompareOptions Options => _options;

    /// <summary>Builds the tree and starts scanning. Subscribe to <see cref="CompareRoot.RowChanged"/> and then re-read statuses: rows may already have moved.</summary>
    public CompareRoot Start(string leftRoot, string rightRoot, CancellationToken ct = default)
    {
        var root = Prepare(leftRoot, rightRoot);
        root.Run(ct);
        return root;
    }

    /// <summary>Builds the full row tree from directory listings. One-sided rows are final; paired files are <see cref="RowStatus.Pending"/>.</summary>
    public CompareRoot Prepare(string leftRoot, string rightRoot)
    {
        if (!Directory.Exists(leftRoot)) throw new DirectoryNotFoundException($"Left directory not found: {leftRoot}");
        if (!Directory.Exists(rightRoot)) throw new DirectoryNotFoundException($"Right directory not found: {rightRoot}");

        leftRoot = Path.GetFullPath(leftRoot);
        rightRoot = Path.GetFullPath(rightRoot);
        var root = new CompareRow(null, "", "", FileKind.Directory, DirSide(leftRoot), DirSide(rightRoot), RowStatus.Pending);
        var pending = new List<CompareRow>();
        Populate(root, leftRoot, rightRoot, pending);
        return new CompareRoot(root, leftRoot, rightRoot, pending, _fingerprinter, _options);
    }

    private void Populate(CompareRow dir, string? leftDir, string? rightDir, List<CompareRow> pending)
    {
        var entries = new SortedDictionary<string, (Entry? left, Entry? right)>(StringComparer.Ordinal);
        if (leftDir is not null)
            foreach (var e in List(leftDir)) entries[e.Name] = (e, null);
        if (rightDir is not null)
            foreach (var e in List(rightDir))
                entries[e.Name] = (entries.TryGetValue(e.Name, out var l) ? l.left : null, e);

        // Directories first, then files, each in natural order.
        var ordered = entries
            .OrderByDescending(kv => (kv.Value.left ?? kv.Value.right)!.IsDirectory)
            .ThenBy(kv => kv.Key, NaturalStringComparer.Instance);

        string prefix = dir.RelativePath.Length == 0 ? "" : dir.RelativePath + "/";
        foreach (var (name, (left, right)) in ordered)
        {
            string rel = prefix + name;
            if (GlobMatcher.IsExcluded(_options.EffectiveExcludes, name, rel)) continue;

            var any = (left ?? right)!;
            if (left is not null && right is not null && left.IsDirectory != right.IsDirectory)
            {
                dir.AddChild(new CompareRow(dir, rel, name, FileKind.Directory, left.Side, right.Side, RowStatus.Error,
                    left.IsDirectory ? "directory on the left, file on the right" : "file on the left, directory on the right"));
                continue;
            }

            if (any.IsDirectory)
            {
                var row = new CompareRow(dir, rel, name, FileKind.Directory, left?.Side, right?.Side, RowStatus.Pending);
                dir.AddChild(row);
                Populate(row, left?.Side.FullPath, right?.Side.FullPath, pending);
            }
            else
            {
                var status = left is null ? RowStatus.RightOnly : right is null ? RowStatus.LeftOnly : RowStatus.Pending;
                var row = new CompareRow(dir, rel, name, FileClassifier.Classify(name), left?.Side, right?.Side, status);
                dir.AddChild(row);
                if (status == RowStatus.Pending) pending.Add(row);
            }
        }
        dir.SealDirectory();
    }

    private sealed record Entry(string Name, bool IsDirectory, FileSide Side);

    private static IEnumerable<Entry> List(string directory)
    {
        var info = new DirectoryInfo(directory);
        foreach (var fs in info.EnumerateFileSystemInfos())
        {
            if (fs is DirectoryInfo d)
                yield return new Entry(d.Name, true, new FileSide(d.FullName, 0, d.LastWriteTimeUtc));
            else if (fs is FileInfo f)
                yield return new Entry(f.Name, false, new FileSide(f.FullName, f.Length, f.LastWriteTimeUtc));
        }
    }

    private static FileSide DirSide(string path) => new(path, 0, Directory.GetLastWriteTimeUtc(path));
}

/// <summary>A live comparison: the row tree plus the scan driving it.</summary>
public sealed class CompareRoot
{
    private readonly IReadOnlyList<CompareRow> _pending;
    private readonly IFingerprinter _fingerprinter;
    private readonly CompareOptions _options;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    private int _tier1Done, _tier2Queued, _tier2Done;
    private volatile bool _cancelled, _completed;

    public CompareRow Root { get; }
    public string LeftRoot { get; }
    public string RightRoot { get; }
    public CompareOptions Options => _options;

    /// <summary>Raised on worker threads whenever a row's status or counts change. Directory rows fire after their files.</summary>
    public event Action<CompareRow>? RowChanged;
    /// <summary>Raised on worker threads after each unit of work.</summary>
    public event Action<ScanProgress>? ProgressChanged;

    public ScanProgress Progress => new(_pending.Count, Volatile.Read(ref _tier1Done), Volatile.Read(ref _tier2Queued), Volatile.Read(ref _tier2Done), _completed, _cancelled);
    /// <summary>Completes (never faults) when the scan finishes or is cancelled.</summary>
    public Task Completion => _completion.Task;
    public bool IsCancelled => _cancelled;

    internal CompareRoot(CompareRow root, string leftRoot, string rightRoot, IReadOnlyList<CompareRow> pending, IFingerprinter fingerprinter, CompareOptions options)
    {
        Root = root;
        LeftRoot = leftRoot;
        RightRoot = rightRoot;
        _pending = pending;
        _fingerprinter = fingerprinter;
        _options = options;
    }

    /// <summary>Starts the scan on the thread pool. Idempotent.</summary>
    public void Run(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(() => RunAsync(ct), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var tier1 = Channel.CreateUnbounded<CompareRow>(new UnboundedChannelOptions { SingleWriter = true });
            var tier2 = Channel.CreateUnbounded<CompareRow>();
            foreach (var row in _pending) tier1.Writer.TryWrite(row);
            tier1.Writer.Complete();

            int workers = Math.Max(1, Math.Min(_options.EffectiveParallelism, Math.Max(1, _pending.Count)));
            int tier1Workers = workers;
            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var row in tier1.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                            await Tier1Async(row, tier2.Writer, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref tier1Workers) == 0) tier2.Writer.Complete();
                    }
                    await foreach (var row in tier2.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                        await Tier2Async(row, ct).ConfigureAwait(false);
                }, CancellationToken.None);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
        }
        catch (Exception e)
        {
            // Should not happen: per-row failures become Error rows. Surface it rather than hang.
            _completion.TrySetException(e);
            return;
        }
        finally
        {
            _completed = true;
        }
        ProgressChanged?.Invoke(Progress);
        _completion.TrySetResult();
    }

    private async ValueTask Tier1Async(CompareRow row, ChannelWriter<CompareRow> tier2, CancellationToken ct)
    {
        var (status, error) = await FingerprintAsync(row, deep: false, ct).ConfigureAwait(false);
        if (status == RowStatus.ProbablyDifferent)
        {
            if (row.Kind == FileKind.Binary)
            {
                Publish(row, RowStatus.Different, error);   // bytes are the content: final
            }
            else if (_options.DeepVerify)
            {
                Interlocked.Increment(ref _tier2Queued);
                Publish(row, status, error);
                tier2.TryWrite(row);
            }
            else
            {
                // Without a content pass the scan cannot tell recompression from a real change, so it
                // must not claim Different; the row stays "bytes differ" and the file/region views verify.
                Publish(row, RowStatus.ProbablyDifferent, error);
            }
        }
        else
        {
            Publish(row, status, error);
        }
        Interlocked.Increment(ref _tier1Done);
        ProgressChanged?.Invoke(Progress);
    }

    private async ValueTask Tier2Async(CompareRow row, CancellationToken ct)
    {
        var (status, error) = await FingerprintAsync(row, deep: true, ct).ConfigureAwait(false);
        Publish(row, status == RowStatus.ProbablyDifferent ? RowStatus.Different : status, error);
        Interlocked.Increment(ref _tier2Done);
        ProgressChanged?.Invoke(Progress);
    }

    /// <summary>Fingerprints both sides at one tier and decides the row's status. Never throws except for cancellation.</summary>
    private async ValueTask<(RowStatus, string?)> FingerprintAsync(CompareRow row, bool deep, CancellationToken ct)
    {
        try
        {
            var left = deep
                ? await _fingerprinter.DeepAsync(row.Left!.FullPath, row.Kind, ct).ConfigureAwait(false)
                : await _fingerprinter.QuickAsync(row.Left!.FullPath, row.Kind, ct).ConfigureAwait(false);
            var right = deep
                ? await _fingerprinter.DeepAsync(row.Right!.FullPath, row.Kind, ct).ConfigureAwait(false)
                : await _fingerprinter.QuickAsync(row.Right!.FullPath, row.Kind, ct).ConfigureAwait(false);

            if (!left.Ok || !right.Ok)
            {
                var parts = new List<string>(2);
                if (!left.Ok) parts.Add("left: " + left.Failure!.ToShortString());
                if (!right.Ok) parts.Add("right: " + right.Failure!.ToShortString());
                return (RowStatus.Error, string.Join("; ", parts));
            }

            row.Left.Fingerprint = left.Value;
            row.Right.Fingerprint = right.Value;
            var a = left.Value!;
            var b = right.Value!;

            if (a.HasErrors || b.HasErrors)
            {
                var parts = new List<string>(2);
                if (a.HasErrors) parts.Add($"left: {a.Region!.ChunkErrors.Count} unreadable chunk(s), e.g. {a.Region.ChunkErrors.Values.First()}");
                if (b.HasErrors) parts.Add($"right: {b.Region!.ChunkErrors.Count} unreadable chunk(s), e.g. {b.Region.ChunkErrors.Values.First()}");
                return (RowStatus.Error, string.Join("; ", parts));
            }

            return a.ContentEquals(b) ? (RowStatus.Same, null) : (RowStatus.ProbablyDifferent, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return (RowStatus.Error, e.Message);
        }
    }

    private void Publish(CompareRow row, RowStatus status, string? error)
    {
        var changed = row.SetStatus(status, error);
        var handler = RowChanged;
        if (handler is null) return;
        foreach (var r in changed) handler(r);
    }
}
