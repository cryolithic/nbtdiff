using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

/// <summary>Wraps a fingerprinter so a test can hold Tier 1 and/or Tier 2 at a gate.</summary>
internal sealed class GatedFingerprinter(IFingerprinter inner) : IFingerprinter
{
    public TaskCompletionSource QuickGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DeepGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _quickCalls, _deepCalls;
    public int QuickCalls => _quickCalls;
    public int DeepCalls => _deepCalls;

    public async ValueTask<LoadResult<FileFingerprint>> QuickAsync(string path, FileKind kind, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _quickCalls);
        await QuickGate.Task.WaitAsync(ct);
        return await inner.QuickAsync(path, kind, ct);
    }

    public async ValueTask<LoadResult<FileFingerprint>> DeepAsync(string path, FileKind kind, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _deepCalls);
        await DeepGate.Task.WaitAsync(ct);
        return await inner.DeepAsync(path, kind, ct);
    }
}

internal static class ScanSupport
{
    public static WorldBuilder BaseWorld() =>
        new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithRegion(-1, 0, chunks: 3).WithLevelDat()
            .WithPlayer(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    public static (TempDir left, TempDir right) Pair(WorldBuilder l, WorldBuilder r)
    {
        var a = new TempDir(); var b = new TempDir();
        l.Write(a.Path); r.Write(b.Path);
        return (a, b);
    }

    public static async Task<CompareRoot> Scan(string left, string right, CompareOptions? options = null, IFingerprinter? fp = null)
    {
        var comparer = fp is null ? new DirectoryComparer(options) : new DirectoryComparer(fp, options);
        var root = comparer.Start(left, right);
        await root.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        return root;
    }

    public static CompareRow Row(this CompareRoot root, string relativePath) =>
        root.Root.Descendants().Single(r => r.RelativePath == relativePath);

    public static IEnumerable<CompareRow> Files(this CompareRoot root) => root.Root.Descendants().Where(r => !r.IsDirectory);

    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}
