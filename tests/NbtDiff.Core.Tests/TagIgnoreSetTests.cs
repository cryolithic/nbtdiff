using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

public class TagIgnoreSetTests
{
    [Fact]
    public void Parse_TrimsDedupesAndDropsBlanks()
    {
        var set = TagIgnoreSet.Parse([" LastUpdate ", "", "Level / LastUpdate", "LastUpdate", "  "]);
        Assert.Equal(["LastUpdate", "Level/LastUpdate"], set.Paths);
        Assert.True(TagIgnoreSet.Parse([]).IsEmpty);
        Assert.True(TagIgnoreSet.Parse(null).IsEmpty);
        Assert.True(TagIgnoreSet.ParseList(" , ;\n").IsEmpty);
        Assert.Equal(["a/b", "c"], TagIgnoreSet.ParseList("a/b, c;").Paths);
    }

    [Fact]
    public void Trie_MatchesExactPathsOnly()
    {
        var root = TagIgnoreSet.Parse(["Level/LastUpdate", "x/*/y"]).Root!;
        Assert.False(root.Ignores("LastUpdate"));
        Assert.False(root.Ignores("Level"));
        Assert.True(root.Child("Level")!.Ignores("LastUpdate"));
        Assert.Null(root.Child("Other"));
        // wildcard
        Assert.True(root.Child("x")!.Child("anything")!.Ignores("y"));
        Assert.False(root.Child("x")!.Child("anything")!.Ignores("z"));
    }

    [Fact]
    public void Default_IgnoresLastUpdate_BothLayouts()
    {
        Assert.True(TagIgnoreSet.Default.Root!.Ignores("LastUpdate"));
        Assert.True(TagIgnoreSet.Default.Root!.Child("Level")!.Ignores("LastUpdate"));
        Assert.False(TagIgnoreSet.Default.Root!.Ignores("InhabitedTime"));
    }

    // ---- hasher ----

    [Fact]
    public void Hash_IgnoresLastUpdate_ByDefault()
    {
        var a = WorldBuilder.MakeChunk(1, 0, 0);
        var b = (NbtCompound)a.Clone();
        b.SetPath("LastUpdate", 123456789L);

        Assert.Equal(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, ignoredTags: TagIgnoreSet.Empty), NbtCanonicalHasher.Hash(b, ignoredTags: TagIgnoreSet.Empty));
    }

    [Fact]
    public void Hash_IgnoredKey_PresentEqualsAbsent()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtLong("LastUpdate", 5) };
        var b = new NbtCompound("") { new NbtInt("x", 1) };
        Assert.Equal(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
    }

    [Fact]
    public void Hash_PathIsExact_NotByName()
    {
        // "LastUpdate" nested somewhere else is still content.
        var a = new NbtCompound("") { new NbtCompound("Other") { new NbtLong("LastUpdate", 1) } };
        var b = new NbtCompound("") { new NbtCompound("Other") { new NbtLong("LastUpdate", 2) } };
        Assert.NotEqual(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));

        // Pre-1.18 layout is covered by the default.
        var c = new NbtCompound("") { new NbtCompound("Level") { new NbtLong("LastUpdate", 1) } };
        var d = new NbtCompound("") { new NbtCompound("Level") { new NbtLong("LastUpdate", 2) } };
        Assert.Equal(NbtCanonicalHasher.Hash(c), NbtCanonicalHasher.Hash(d));
    }

    [Fact]
    public void Hash_ListsAreTransparent()
    {
        var set = TagIgnoreSet.Parse(["block_entities/keepPacked"]);
        var a = new NbtCompound("") { new NbtList("block_entities", NbtTagType.Compound) { new NbtCompound { new NbtString("id", "chest"), new NbtByte("keepPacked", 0) } } };
        var b = new NbtCompound("") { new NbtList("block_entities", NbtTagType.Compound) { new NbtCompound { new NbtString("id", "chest"), new NbtByte("keepPacked", 1) } } };
        Assert.Equal(NbtCanonicalHasher.Hash(a, ignoredTags: set), NbtCanonicalHasher.Hash(b, ignoredTags: set));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, ignoredTags: TagIgnoreSet.Empty), NbtCanonicalHasher.Hash(b, ignoredTags: TagIgnoreSet.Empty));
    }

    // ---- differ ----

    [Fact]
    public void Diff_IgnoredKey_NotInTree()
    {
        var a = WorldBuilder.MakeChunk(1, 0, 0);
        var b = (NbtCompound)a.Clone();
        b.SetPath("LastUpdate", 123456789L);

        var diff = NbtDiffer.Diff(a, b);
        Assert.False(diff.HasChanges);
        Assert.DoesNotContain(diff.Descendants(), n => n.Name == "LastUpdate");

        var strict = NbtDiffer.Diff(a, b, new DiffOptions(IgnoredTags: TagIgnoreSet.Empty));
        Assert.True(strict.HasChanges);
        Assert.Equal("LastUpdate", Assert.Single(strict.Descendants(), n => n.Kind == DiffKind.ValueChanged).Name);
    }

    [Fact]
    public void Diff_IgnoredKey_SkippedInOrderedModeAndOneSidedSubtrees()
    {
        var a = new NbtCompound("") { new NbtLong("LastUpdate", 1), new NbtInt("x", 1), new NbtInt("y", 2) };
        var b = new NbtCompound("") { new NbtInt("x", 1), new NbtLong("LastUpdate", 2), new NbtInt("y", 2) };
        var ordered = NbtDiffer.Diff(a, b, new DiffOptions(CompoundOrderMatters: true));
        Assert.False(ordered.HasChanges);   // the only reordering involves the ignored key

        var added = NbtDiffer.Diff(null, new NbtCompound("") { new NbtLong("LastUpdate", 1), new NbtInt("x", 1) });
        Assert.Equal(["x"], added.Children.Select(c => c.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HashDiffAgreement_HoldsWithIgnoreSet(bool ordered)
    {
        var set = TagIgnoreSet.Parse(["LastUpdate", "Heightmaps/MOTION_BLOCKING", "sections/block_states/palette"]);
        var options = new DiffOptions(CompoundOrderMatters: ordered, IgnoredTags: set);
        foreach (var (l, r) in new[]
        {
            (WorldBuilder.MakeChunk(1, 0, 0), WorldBuilder.MakeChunk(1, 0, 0)),
            (WorldBuilder.MakeChunk(1, 0, 0), WorldBuilder.MakeChunk(1, 1, 0)),
            (WorldBuilder.MakeChunk(1, 0, 0), ((NbtCompound)WorldBuilder.MakeChunk(1, 0, 0).Clone()).SetPath("LastUpdate", 9L)),
            (WorldBuilder.MakeChunk(1, 0, 0), ((NbtCompound)WorldBuilder.MakeChunk(1, 0, 0).Clone()).SetPath("InhabitedTime", 9L)),
        })
        {
            bool hashEqual = NbtCanonicalHasher.Hash(l, ordered, set) == NbtCanonicalHasher.Hash(r, ordered, set);
            bool diffEmpty = !NbtDiffer.Diff(l, r, options).HasChanges;
            Assert.Equal(hashEqual, diffEmpty);
        }
    }
}
