using fNbt;

namespace NbtDiff.Core.Tests;

public class KeyedAlignerTests
{
    private static readonly DiffOptions Keyed = new(ListAligner: KeyedAligner.Default);
    private static readonly DiffOptions Indexed = DiffOptions.Default;

    private static NbtCompound Entity(int uuidSeed, string id = "minecraft:zombie", float health = 20f) => new()
    {
        new NbtIntArray("UUID", [uuidSeed, uuidSeed * 31, uuidSeed * 7, -uuidSeed]),
        new NbtString("id", id),
        new NbtFloat("Health", health),
        new NbtList("Pos", NbtTagType.Double) { new NbtDouble(uuidSeed), new NbtDouble(64), new NbtDouble(-uuidSeed) },
    };

    private static NbtCompound Root(params NbtCompound[] entities) => new("")
    {
        new NbtList("Entities", entities, NbtTagType.Compound),
    };

    private static NbtList List(params NbtCompound[] items) => new("l", items, NbtTagType.Compound);

    private static IEnumerable<string> ChangedPaths(DiffNode d) =>
        d.Descendants().Where(n => n.Kind != DiffKind.Unchanged).Select(n => $"{n.Kind}:{n.Path}");

    [Fact]
    public void Reordered_Entities_NoChangesKeyed_ChangesIndexed()
    {
        var a = Root(Entity(1), Entity(2), Entity(3));
        var b = Root(Entity(3), Entity(1), Entity(2));

        Assert.Equal(0, NbtDiffer.Diff(a, b, Keyed).ChangedDescendants);
        Assert.NotEqual(0, NbtDiffer.Diff(a, b, Indexed).ChangedDescendants);
    }

    [Fact]
    public void Reorder_IsTheDocumentedHashDiffDisagreement()
    {
        // The hasher sees list order as content; the keyed aligner deliberately does not.
        var a = Root(Entity(1), Entity(2));
        var b = Root(Entity(2), Entity(1));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
        Assert.False(NbtDiffer.Diff(a, b, Keyed).HasChanges);
        Assert.True(NbtDiffer.Diff(a, b, Indexed).HasChanges);
    }

    [Fact]
    public void RemovedFromMiddle_OneRemovedSubtree_RestUnchanged()
    {
        var a = Root(Entity(1), Entity(2), Entity(3), Entity(4));
        var b = Root(Entity(1), Entity(3), Entity(4));

        var keyed = NbtDiffer.Diff(a, b, Keyed);
        var list = keyed.Children.Single();
        var changedTop = list.Children.Where(c => c.HasChanges).ToList();
        var removed = Assert.Single(changedTop);
        Assert.Equal(DiffKind.Removed, removed.Kind);
        Assert.Equal("Entities/[1]", removed.Path);
        Assert.All(removed.Descendants().Skip(1), n => Assert.Equal(DiffKind.Removed, n.Kind));
        Assert.Equal(removed.ChangedDescendants, keyed.ChangedDescendants);

        // Index alignment cascades: entities 2/3/4 vs 3/4 all differ.
        var indexed = NbtDiffer.Diff(a, b, Indexed);
        Assert.True(indexed.ChangedDescendants > keyed.ChangedDescendants);
        Assert.True(indexed.Children.Single().Children.Count(c => c.HasChanges) >= 3);
    }

    [Fact]
    public void ChangedField_InsideOneEntity()
    {
        var a = Root(Entity(1), Entity(2), Entity(3));
        var b = Root(Entity(3), Entity(1), Entity(2, health: 5f));   // reordered and one health changed

        var d = NbtDiffer.Diff(a, b, Keyed);
        Assert.Equal(["ValueChanged:Entities/[1]/Health"], ChangedPaths(d));
        Assert.Equal(1, d.ChangedDescendants);
    }

    [Fact]
    public void Added_OnRight_NamedByRightIndex_AppendedAfterLeft()
    {
        var a = Root(Entity(1), Entity(2));
        var b = Root(Entity(9), Entity(1), Entity(2));

        var d = NbtDiffer.Diff(a, b, Keyed);
        var list = d.Children.Single();
        Assert.Equal(["[0]", "[1]", "[0]"], list.Children.Select(c => c.Name));   // right-only item last, named by its right index
        var added = list.Children[2];
        Assert.Equal(DiffKind.Added, added.Kind);
        Assert.Equal("Entities/[0]", added.Path);
        Assert.Null(added.Left);
        Assert.Equal([DiffKind.Unchanged, DiffKind.Unchanged], list.Children.Take(2).Select(c => c.Kind));
    }

    [Fact]
    public void DuplicateKeys_PairInOrder()
    {
        var a = List(Entity(1, health: 1f), Entity(1, health: 2f), Entity(1, health: 3f));
        var b = List(Entity(1, health: 1f), Entity(1, health: 30f));

        var pairs = KeyedAligner.Default.Align(a, b);
        Assert.Equal([(0, 0), (1, 1), (2, null)], pairs.Select(p => (p.Left, p.Right)));

        var d = NbtDiffer.Diff(a, b, Keyed);
        Assert.Equal(["ValueChanged:[1]/Health", "Removed:[2]", "Removed:[2]/UUID", "Removed:[2]/id", "Removed:[2]/Health", "Removed:[2]/Pos", "Removed:[2]/Pos/[0]", "Removed:[2]/Pos/[1]", "Removed:[2]/Pos/[2]"],
            ChangedPaths(d));
    }

    [Fact]
    public void MixedKeyedAndUnkeyed_UnkeyedPairByPositionAmongThemselves()
    {
        NbtCompound Unkeyed(int v) => new() { new NbtInt("value", v) };
        var a = List(Unkeyed(1), Entity(1), Unkeyed(2), Entity(2));
        var b = List(Entity(2), Unkeyed(1), Entity(1), Unkeyed(2), Unkeyed(3));

        var pairs = KeyedAligner.Default.Align(a, b);
        Assert.Equal([(0, 1), (1, 2), (2, 3), (3, 0), (null, 4)], pairs.Select(p => (p.Left, p.Right)));

        var d = NbtDiffer.Diff(a, b, Keyed);
        Assert.Equal(["Added:[4]", "Added:[4]/value"], ChangedPaths(d));
    }

    [Fact]
    public void KeyType_Matters()
    {
        var a = List(new NbtCompound { new NbtInt("id", 5), new NbtInt("x", 1) });
        var b = List(new NbtCompound { new NbtString("id", "5"), new NbtInt("x", 1) });
        Assert.Equal([(0, null), (null, 0)], KeyedAligner.Default.Align(a, b).Select(p => (p.Left, p.Right)));
    }

    [Fact]
    public void UuidArrays_CompareByContent()
    {
        var a = List(Entity(7));
        var b = List(Entity(7, health: 3f));
        Assert.Equal([(0, 0)], KeyedAligner.Default.Align(a, b).Select(p => (p.Left, p.Right)));
        Assert.NotNull(KeyedAligner.Default.KeyOf(Entity(7)));
        Assert.Equal(KeyedAligner.Default.KeyOf(Entity(7)), KeyedAligner.Default.KeyOf(Entity(7, health: 3f)));
        Assert.NotEqual(KeyedAligner.Default.KeyOf(Entity(7)), KeyedAligner.Default.KeyOf(Entity(8)));
    }

    [Fact]
    public void FirstPresentKeyNameWins()
    {
        // No UUID → falls through to id.
        var block = new NbtCompound { new NbtString("id", "minecraft:chest"), new NbtInt("x", 1) };
        Assert.StartsWith("String:", KeyedAligner.Default.KeyOf(block));
        Assert.StartsWith("IntArray:", KeyedAligner.Default.KeyOf(Entity(1)));
        Assert.Null(KeyedAligner.Default.KeyOf(new NbtCompound { new NbtInt("x", 1) }));
    }

    [Fact]
    public void BlockEntities_KeyedById_ThenPositional()
    {
        // Two chests share id; they pair in order. A furnace is removed; a hopper is added.
        NbtCompound Block(string id, int x) => new() { new NbtString("id", id), new NbtInt("x", x), new NbtInt("y", 64), new NbtInt("z", 0) };
        var a = Root(Block("minecraft:chest", 1), Block("minecraft:furnace", 2), Block("minecraft:chest", 3));
        var b = Root(Block("minecraft:chest", 1), Block("minecraft:chest", 3), Block("minecraft:hopper", 4));

        var d = NbtDiffer.Diff(a, b, Keyed);
        var kinds = d.Children.Single().Children.Select(c => (c.Path, c.Kind)).ToList();
        Assert.Equal([("Entities/[0]", DiffKind.Unchanged), ("Entities/[1]", DiffKind.Removed), ("Entities/[2]", DiffKind.Unchanged), ("Entities/[2]", DiffKind.Added)], kinds);
    }

    [Fact]
    public void CustomKeyList()
    {
        var aligner = new KeyedAligner(["tag"]);
        NbtCompound Item(string tag, int n) => new() { new NbtString("tag", tag), new NbtInt("n", n), new NbtString("id", "ignored") };
        var a = List(Item("a", 1), Item("b", 2));
        var b = List(Item("b", 2), Item("a", 1));
        Assert.Equal([(0, 1), (1, 0)], aligner.Align(a, b).Select(p => (p.Left, p.Right)));
        Assert.False(NbtDiffer.Diff(a, b, new DiffOptions(ListAligner: aligner)).HasChanges);
        Assert.Throws<ArgumentException>(() => new KeyedAligner([]));
    }

    [Theory]
    [InlineData(NbtTagType.Int)]
    [InlineData(NbtTagType.String)]
    [InlineData(NbtTagType.List)]
    public void NonCompoundLists_SameAsIndexAligner(NbtTagType type)
    {
        NbtTag Item(int i) => type switch
        {
            NbtTagType.Int => new NbtInt(i),
            NbtTagType.String => new NbtString("s" + i),
            _ => new NbtList(NbtTagType.Int) { new NbtInt(i) },
        };
        var a = new NbtList("l", type) { Item(1), Item(2), Item(3) };
        var b = new NbtList("l", type) { Item(3), Item(1) };
        Assert.Equal(IndexAligner.Instance.Align(a, b), KeyedAligner.Default.Align(a, b));
    }

    [Fact]
    public void EmptyLists_SameAsIndexAligner()
    {
        var empty = new NbtList("l");
        var some = List(Entity(1), Entity(2));
        Assert.Empty(KeyedAligner.Default.Align(empty, new NbtList("l")));
        Assert.Equal([(0, null), (1, null)], KeyedAligner.Default.Align(some, empty).Select(p => (p.Left, p.Right)));
        Assert.Equal([(null, 0), (null, 1)], KeyedAligner.Default.Align(empty, some).Select(p => (p.Left, p.Right)));
        Assert.Equal(IndexAligner.Instance.Align(some, empty), KeyedAligner.Default.Align(some, empty));
    }

    [Fact]
    public void EveryIndexAppearsExactlyOnce()
    {
        var a = List(Entity(1), Entity(2), Entity(2), new NbtCompound { new NbtInt("v", 1) }, Entity(5));
        var b = List(Entity(2), new NbtCompound { new NbtInt("v", 1) }, Entity(1), Entity(9), new NbtCompound { new NbtInt("v", 2) });
        var pairs = KeyedAligner.Default.Align(a, b);
        Assert.Equal(Enumerable.Range(0, a.Count), pairs.Where(p => p.Left is not null).Select(p => p.Left!.Value).Order());
        Assert.Equal(Enumerable.Range(0, b.Count), pairs.Where(p => p.Right is not null).Select(p => p.Right!.Value).Order());
        Assert.Equal(a.Count, pairs.TakeWhile(p => p.Left is not null).Count());   // left-walk first, right-only appended
    }
}
