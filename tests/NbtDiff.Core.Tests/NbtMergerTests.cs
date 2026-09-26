using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

/// <summary>
/// Each test diffs two trees, copies one node across with <see cref="NbtMerger"/>, and asserts the
/// sides became equal by re-diffing (the same loop the file compare view runs).
/// </summary>
public class NbtMergerTests
{
    private static DiffNode Node(DiffNode root, string path) => Chain(root, path)[^1];

    /// <summary>Root → node, by walking the diff tree along the path's segments.</summary>
    private static List<DiffNode> Chain(DiffNode root, string path)
    {
        var chain = new List<DiffNode> { root };
        string acc = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? segment : acc + "/" + segment;
            chain.Add(root.Descendants().Single(n => n.Path == acc));
        }
        return chain;
    }

    private static void AssertEqual(NbtTag? left, NbtTag? right)
    {
        var diff = NbtDiffer.Diff(left, right);
        Assert.True(diff.ChangedDescendants == 0, $"expected equal trees, still differ:\n{Describe(diff)}");
        Assert.True(NbtEquality.AreEqual(left, right, out var difference), difference);
    }

    private static string Describe(DiffNode node) => string.Join("\n", node.Descendants().Where(n => n.Kind != DiffKind.Unchanged).Select(n => $"{n.Path}: {n.Kind}"));

    private static NbtCompound Mob(string id, int health) => new() { new NbtString("id", id), new NbtInt("Health", health) };

    [Fact]
    public void Scalar_CopiedToLeft_MakesSidesEqual()
    {
        var left = new NbtCompound("") { new NbtInt("Health", 10) };
        var right = new NbtCompound("") { new NbtInt("Health", 20) };
        var diff = NbtDiffer.Diff(left, right);
        Assert.Equal(DiffKind.ValueChanged, Node(diff, "Health").Kind);

        NbtMerger.Copy(Chain(diff, "Health"), toRight: false);

        Assert.Equal(20, left.Get<NbtInt>("Health")!.Value);
        AssertEqual(left, right);
    }

    [Fact]
    public void Subtree_CopiedToRight_MakesSidesEqual()
    {
        var left = new NbtCompound("") { new NbtCompound("a") { new NbtInt("x", 1), new NbtInt("y", 2) } };
        var right = new NbtCompound("") { new NbtCompound("a") { new NbtInt("x", 1), new NbtInt("y", 3), new NbtInt("z", 4) } };
        var diff = NbtDiffer.Diff(left, right);

        NbtMerger.Copy(Chain(diff, "a"), toRight: true);

        // The whole subtree, including the right-only key, was replaced by the left one.
        Assert.False(right.Get<NbtCompound>("a")!.Contains("z"));
        AssertEqual(left, right);
    }

    [Fact]
    public void RightOnlyKey_CopiedToLeft_InsertsIt()
    {
        var left = new NbtCompound("");
        var right = new NbtCompound("") { new NbtString("New", "tag") };
        var diff = NbtDiffer.Diff(left, right);
        Assert.Equal(DiffKind.Added, Node(diff, "New").Kind);

        NbtMerger.Copy(Chain(diff, "New"), toRight: false);

        Assert.Equal("tag", left.Get<NbtString>("New")!.Value);
        AssertEqual(left, right);
    }

    [Fact]
    public void RightOnlyKey_CopiedToRight_RemovesIt()
    {
        var left = new NbtCompound("");
        var right = new NbtCompound("") { new NbtString("New", "tag") };
        var diff = NbtDiffer.Diff(left, right);

        NbtMerger.Copy(Chain(diff, "New"), toRight: true);

        Assert.False(right.Contains("New"));
        AssertEqual(left, right);
    }

    [Fact]
    public void Root_CopiedToRight_ReplacesTheWholeTree()
    {
        var left = new NbtCompound("") { new NbtInt("a", 1) };
        var right = new NbtCompound("") { new NbtInt("b", 2) };
        var diff = NbtDiffer.Diff(left, right);

        var newRoot = NbtMerger.Copy([diff], toRight: true);

        Assert.NotSame(right, newRoot);
        AssertEqual(left, newRoot);
    }

    [Fact]
    public void RootDeletedOnSource_ReturnsNullTarget()
    {
        var right = new NbtCompound("") { new NbtInt("a", 1) };
        var diff = NbtDiffer.Diff(null, right);

        var newRoot = NbtMerger.Copy([diff], toRight: true);

        Assert.Null(newRoot);
    }

    [Fact]
    public void ListItem_CopiedToLeft_ReplacesByIdentity()
    {
        var left = new NbtCompound("") { new NbtList("mobs", NbtTagType.Compound) { Mob("zombie", 20), Mob("creeper", 20) } };
        var right = (NbtCompound)left.Clone();
        right.Get<NbtList>("mobs")![1] = Mob("creeper", 5);
        var diff = NbtDiffer.Diff(left, right);

        NbtMerger.Copy(Chain(diff, "mobs/[1]/Health"), toRight: false);

        Assert.Equal(5, left.Get<NbtList>("mobs")!.Get<NbtCompound>(1)!.Get<NbtInt>("Health")!.Value);
        AssertEqual(left, right);
    }

    [Fact]
    public void RightOnlyListItem_CopiedToLeft_InsertsAtSourcePosition()
    {
        var left = new NbtCompound("") { new NbtList("mobs", NbtTagType.Compound) { Mob("zombie", 20) } };
        var right = new NbtCompound("") { new NbtList("mobs", NbtTagType.Compound) { Mob("zombie", 20), Mob("creeper", 5) } };
        var diff = NbtDiffer.Diff(left, right);
        Assert.Equal(DiffKind.Added, Node(diff, "mobs/[1]").Kind);

        NbtMerger.Copy(Chain(diff, "mobs/[1]"), toRight: false);

        var mobs = left.Get<NbtList>("mobs")!;
        Assert.Equal(2, mobs.Count);
        Assert.Equal("creeper", mobs.Get<NbtCompound>(1)!.Get<NbtString>("id")!.Value);
        AssertEqual(left, right);
    }

    [Fact]
    public void RemovingTheLastListItem_NormalizesTheListTypeForSaving()
    {
        var left = new NbtCompound("") { new NbtList("items", NbtTagType.Int) { new NbtInt(7) } };
        var right = new NbtCompound("") { new NbtList("items", NbtTagType.Int) };
        var diff = NbtDiffer.Diff(left, right);
        Assert.Equal(DiffKind.Removed, Node(diff, "items/[0]").Kind);

        NbtMerger.Copy(Chain(diff, "items/[0]"), toRight: false);

        var items = left.Get<NbtList>("items")!;
        Assert.Equal(0, items.Count);
        Assert.Equal(NbtTagType.End, items.ListType); // an Unknown-typed empty list cannot be serialized
        AssertEqual(left, right);
    }

    [Fact]
    public void DeepNode_IntoWhollyMissingSide_SynthesizesContainers()
    {
        var left = new NbtCompound("") { new NbtList("Entities", NbtTagType.Compound) { Mob("zombie", 20) } };
        var diff = NbtDiffer.Diff(left, null);
        Assert.Equal(DiffKind.Removed, Node(diff, "Entities/[0]/Health").Kind);

        var newRoot = NbtMerger.Copy(Chain(diff, "Entities/[0]/Health"), toRight: true);

        // The whole container chain was built on the empty side; only the copied leaf is present.
        var entities = ((NbtCompound)newRoot!).Get<NbtList>("Entities")!;
        Assert.Equal(1, entities.Count);
        var mob = entities.Get<NbtCompound>(0)!;
        Assert.Equal(20, mob.Get<NbtInt>("Health")!.Value);
        Assert.False(mob.Contains("id")); // the sibling key was not dragged along
    }

    [Fact]
    public void DeepNode_IntoMissingSubtree_SynthesizesOnlyWhatIsMissing()
    {
        var left = new NbtCompound("") { new NbtList("Entities", NbtTagType.Compound) { Mob("zombie", 20) } };
        var right = new NbtCompound("") { new NbtInt("DataVersion", 3000) };   // no Entities key
        var diff = NbtDiffer.Diff(left, right);

        var root = NbtMerger.Copy(Chain(diff, "Entities/[0]/Health"), toRight: true);

        Assert.Same(right, root);   // the existing root was kept, not replaced
        var mob = right.Get<NbtList>("Entities")!.Get<NbtCompound>(0)!;
        Assert.Equal(20, mob.Get<NbtInt>("Health")!.Value);
        Assert.Equal(3000, right.Get<NbtInt>("DataVersion")!.Value);
    }
}
