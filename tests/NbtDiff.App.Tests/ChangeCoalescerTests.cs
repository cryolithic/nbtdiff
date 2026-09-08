using NbtDiff.App.Tree;

namespace NbtDiff.App.Tests;

public class ChangeCoalescerTests
{
    private sealed class Item(string name) { public string Name => name; }

    [Fact]
    public void Burst_IsDedupedPerDrain()
    {
        var c = new ChangeCoalescer<Item>();
        var a = new Item("a"); var b = new Item("b");
        for (int i = 0; i < 100; i++) c.Add(a);
        c.Add(b);
        var batch = c.Drain();
        Assert.Equal(2, batch.Count);
        Assert.Contains(a, batch);
        Assert.Contains(b, batch);
        Assert.Equal(101, c.Added);
        Assert.Empty(c.Drain());
        Assert.False(c.HasPending);
    }

    [Fact]
    public void AddAfterDrain_IsNeverLost()
    {
        var c = new ChangeCoalescer<Item>();
        var a = new Item("a");
        c.Add(a);
        Assert.Single(c.Drain());
        c.Add(a);                    // the row changed again after the flush
        Assert.True(c.HasPending);
        Assert.Single(c.Drain());
    }

    [Fact]
    public async Task ConcurrentAdds_AllSurface()
    {
        var c = new ChangeCoalescer<Item>();
        var items = Enumerable.Range(0, 500).Select(i => new Item($"i{i}")).ToArray();
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var producers = Task.Run(() => Parallel.ForEach(items, it => { for (int k = 0; k < 20; k++) c.Add(it); }));
        while (!producers.IsCompleted)
        {
            foreach (var it in c.Drain()) seen.Add(it);
            await Task.Yield();
        }
        await producers;
        foreach (var it in c.Drain()) seen.Add(it);
        Assert.Equal(items.Length, seen.Count);
        Assert.Equal(items.Length * 20, c.Added);
    }
}
