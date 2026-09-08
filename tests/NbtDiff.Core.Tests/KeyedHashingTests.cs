using fNbt;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;
using static NbtDiff.Core.Tests.ScanSupport;

namespace NbtDiff.Core.Tests;

/// <summary>Keyed list hashing: the scan and region grid must agree with the keyed differ that a reordered list is not a change.</summary>
public class KeyedHashingTests
{
    private static readonly KeyedAligner K = KeyedAligner.Default;

    private static NbtCompound Entity(int n, float health = 20f) => new()
    {
        new NbtIntArray("UUID", [n, n, n, n]),
        new NbtString("id", "minecraft:zombie"),
        new NbtFloat("Health", health),
    };

    private static NbtCompound BlockEntity(int x, int y, int z, string id = "minecraft:chest", int items = 0) => new()
    {
        new NbtString("id", id),
        new NbtInt("x", x), new NbtInt("y", y), new NbtInt("z", z),
        new NbtList("Items", Enumerable.Range(0, items).Select(i => (NbtTag)new NbtCompound { new NbtByte("Slot", (byte)i), new NbtString("id", "minecraft:stone") }), NbtTagType.Compound),
    };

    private static NbtCompound Root(string listName, params NbtCompound[] items) =>
        new("") { new NbtList(listName, items.Cast<NbtTag>(), NbtTagType.Compound) };

    [Fact]
    public void ReorderedEntities_HashEqual_OnlyInKeyedMode()
    {
        var a = Root("Entities", Entity(1), Entity(2), Entity(3));
        var b = Root("Entities", Entity(3), Entity(1), Entity(2));
        Assert.Equal(NbtCanonicalHasher.Hash(a, keyedLists: K), NbtCanonicalHasher.Hash(b, keyedLists: K));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
    }

    [Fact]
    public void ChangedEntity_StillDiffersInKeyedMode()
    {
        var a = Root("Entities", Entity(1), Entity(2));
        var b = Root("Entities", Entity(2), Entity(1, health: 5f));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, keyedLists: K), NbtCanonicalHasher.Hash(b, keyedLists: K));
        var diff = NbtDiffer.Diff(a, b, DiffOptions.Keyed);
        Assert.Equal("Entities/[0]/Health", Assert.Single(diff.Descendants(), n => n.Kind == DiffKind.ValueChanged).Path);
    }

    [Fact]
    public void BlockEntities_KeyedByPosition_NotById()
    {
        // Two chests share an id; swapping them must pair each chest with itself (by position).
        var a = Root("block_entities", BlockEntity(1, 64, 1, items: 2), BlockEntity(2, 64, 2, items: 0));
        var b = Root("block_entities", BlockEntity(2, 64, 2, items: 0), BlockEntity(1, 64, 1, items: 2));
        Assert.Equal(NbtCanonicalHasher.Hash(a, keyedLists: K), NbtCanonicalHasher.Hash(b, keyedLists: K));
        Assert.False(NbtDiffer.Diff(a, b, DiffOptions.Keyed).HasChanges);
        Assert.Equal("xyz:1,64,1", K.KeyOf(BlockEntity(1, 64, 1)));

        // Same positions, chest contents moved: a real change on both sides.
        var c = Root("block_entities", BlockEntity(1, 64, 1, items: 0), BlockEntity(2, 64, 2, items: 2));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, keyedLists: K), NbtCanonicalHasher.Hash(c, keyedLists: K));
        Assert.True(NbtDiffer.Diff(a, c, DiffOptions.Keyed).HasChanges);
    }

    [Fact]
    public void UnkeyedItems_KeepPositionalSemantics()
    {
        var a = Root("l", new NbtCompound { new NbtInt("v", 1) }, new NbtCompound { new NbtInt("v", 2) });
        var b = Root("l", new NbtCompound { new NbtInt("v", 2) }, new NbtCompound { new NbtInt("v", 1) });
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, keyedLists: K), NbtCanonicalHasher.Hash(b, keyedLists: K));
        Assert.True(NbtDiffer.Diff(a, b, DiffOptions.Keyed).HasChanges);
    }

    [Fact]
    public void DuplicateKeys_PairByOccurrence_HashAgrees()
    {
        var a = Root("Entities", Entity(1, 20f), Entity(1, 10f));
        var b = Root("Entities", Entity(1, 10f), Entity(1, 20f));   // same multiset, different occurrence order
        bool hashEqual = NbtCanonicalHasher.Hash(a, keyedLists: K) == NbtCanonicalHasher.Hash(b, keyedLists: K);
        bool diffEmpty = !NbtDiffer.Diff(a, b, DiffOptions.Keyed).HasChanges;
        Assert.Equal(hashEqual, diffEmpty);   // both say "changed": occurrence order is kept for duplicates
        Assert.False(diffEmpty);
    }

    [Fact]
    public void Sections_KeyedByY()
    {
        var sec = (NbtList)WorldBuilder.MakeChunk(1, 0, 0)["sections"];
        Assert.StartsWith("Byte:", K.KeyOf((NbtCompound)sec[0]));
    }

    [Fact]
    public void CanonicalItems_KeyedSortedThenUnkeyedInOrder()
    {
        var list = new NbtList("l", NbtTagType.Compound)
        {
            new NbtCompound { new NbtInt("v", 1) },
            Entity(2),
            new NbtCompound { new NbtInt("v", 2) },
            Entity(1),
        };
        var order = K.CanonicalItems(list).Select(t => t is NbtCompound c && c.Contains("UUID") ? "E" + ((NbtIntArray)c["UUID"]).Value[0] : "u" + ((NbtInt)((NbtCompound)t)["v"]).Value).ToList();
        Assert.Equal(["E1", "E2", "u1", "u2"], order);
        var ints = new NbtList("i", NbtTagType.Int) { new NbtInt(1) };
        Assert.Same(ints, K.CanonicalItems(ints));   // non-compound lists pass through untouched
    }

    // ---- end to end ----

    private static WorldBuilder WithEntities(WorldBuilder w, params NbtCompound[] entities) =>
        w.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("block_entities", new NbtList("block_entities", entities.Cast<NbtTag>(), NbtTagType.Compound)));

    [Fact]
    public async Task Scan_ReorderedBlockEntities_IsSame_UnlessKeyedListsOff()
    {
        var left = WithEntities(BaseWorld(), BlockEntity(1, 64, 1, items: 2), BlockEntity(2, 64, 2));
        var right = WithEntities(BaseWorld(), BlockEntity(2, 64, 2), BlockEntity(1, 64, 1, items: 2));
        var (l, r) = Pair(left, right);
        using (l) using (r)
        {
            var root = await Scan(l.Path, r.Path);
            Assert.Equal(RowStatus.Same, root.Row("region/r.0.0.mca").Status);

            var positional = await Scan(l.Path, r.Path, new CompareOptions(KeyedLists: false));
            Assert.Equal(RowStatus.Different, positional.Row("region/r.0.0.mca").Status);
        }
    }

    [Fact]
    public void RegionDiffer_ReorderedBlockEntities_AgreesWithScan()
    {
        var left = WithEntities(BaseWorld(), BlockEntity(1, 64, 1, items: 2), BlockEntity(2, 64, 2));
        var right = WithEntities(BaseWorld(), BlockEntity(2, 64, 2), BlockEntity(1, 64, 1, items: 2));
        var (l, r) = Pair(left, right);
        using (l) using (r)
        {
            using var lf = RegionFile.Open(l.File(Path.Combine("region", "r.0.0.mca"))).ValueOrThrow();
            using var rf = RegionFile.Open(r.File(Path.Combine("region", "r.0.0.mca"))).ValueOrThrow();
            var keyed = new CompareOptions().ToDiffOptions();
            Assert.All(RegionDiffer.Diff(lf, rf, keyed), c => Assert.Equal(ChunkDiffStatus.Same, c.Status));
            Assert.False(RegionDiffer.DiffChunk(lf, rf, 3, 1, keyed).ValueOrThrow().HasChanges);

            var positional = new CompareOptions(KeyedLists: false).ToDiffOptions();
            Assert.Contains(RegionDiffer.Diff(lf, rf, positional), c => c.Status == ChunkDiffStatus.Different);
        }
    }
}
