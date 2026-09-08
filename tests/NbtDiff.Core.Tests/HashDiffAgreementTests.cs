using fNbt;
using NbtDiff.Nbt.Snbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

/// <summary>The contract between hasher and differ: equal hash ⇔ empty diff, in both key-order modes.</summary>
public class HashDiffAgreementTests
{
    private static void AssertAgree(NbtTag a, NbtTag b)
    {
        foreach (bool ordered in new[] { false, true })
        foreach (var keyed in new KeyedAligner?[] { null, KeyedAligner.Default })
        {
            bool hashEqual = NbtCanonicalHasher.Hash(a, ordered, keyedLists: keyed) == NbtCanonicalHasher.Hash(b, ordered, keyedLists: keyed);
            var diff = NbtDiffer.Diff(a, b, new DiffOptions(CompoundOrderMatters: ordered, ListAligner: keyed));
            Assert.True(hashEqual == (diff.ChangedDescendants == 0),
                $"ordered={ordered} keyed={keyed is not null}: hashEqual={hashEqual} but ChangedDescendants={diff.ChangedDescendants}; first change: {diff.Descendants().FirstOrDefault(n => n.Kind != DiffKind.Unchanged)}");
        }
    }

    public static TheoryData<int, int> SeedPairs
    {
        get
        {
            var d = new TheoryData<int, int>();
            for (int i = 1; i <= 4; i++)
                for (int j = 1; j <= 4; j++)
                    d.Add(i, j);
            return d;
        }
    }

    [Theory, MemberData(nameof(SeedPairs))]
    public void SampleCompounds(int a, int b)
    {
        AssertAgree(NbtFixtures.SampleCompound(a), NbtFixtures.SampleCompound(b));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(3, -4, 3, -4)]
    [InlineData(3, -4, -4, 3)]
    public void Chunks(int ax, int az, int bx, int bz)
    {
        AssertAgree(WorldBuilder.MakeChunk(42, ax, az), WorldBuilder.MakeChunk(42, bx, bz));
        AssertAgree(WorldBuilder.MakeChunk(42, ax, az), WorldBuilder.MakeChunk(43, bx, bz));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShuffledKeyOrder(int seed)
    {
        var a = NbtFixtures.SampleCompound(seed);
        var b = Shuffle((NbtCompound)a.Clone(), new Random(seed));
        AssertAgree(a, b);
        // And the unordered hash really is equal for the shuffle, so the theory exercised both branches.
        Assert.Equal(NbtCanonicalHasher.Hash(a), NbtCanonicalHasher.Hash(b));
        Assert.NotEqual(NbtCanonicalHasher.Hash(a, true), NbtCanonicalHasher.Hash(b, true));
    }

    private static NbtCompound Shuffle(NbtCompound c, Random rng)
    {
        var tags = c.Tags.ToList();
        c.Clear();
        // Reverse guarantees a different order for ≥2 keys; rng picks a rotation on top.
        tags.Reverse();
        int rot = tags.Count > 1 ? rng.Next(tags.Count) : 0;
        foreach (var t in tags.Skip(rot).Concat(tags.Take(rot)))
        {
            if (t is NbtCompound sub) Shuffle(sub, rng);
            if (t is NbtList list)
                foreach (var item in list.OfType<NbtCompound>()) Shuffle(item, rng);
            c.Add(t);
        }
        return c;
    }

    [Fact]
    public void EmptyLists_SnbtVersusBinary()
    {
        var original = NbtFixtures.SampleCompound(5);   // contains "emptyList"
        var viaSnbt = (NbtCompound)SnbtParser.Parse(SnbtWriter.Write(original));
        viaSnbt.Name = "";
        var file = new NbtFile((NbtCompound)original.Clone());
        var reread = new NbtFile();
        reread.LoadFromBuffer(file.SaveToBuffer(NbtCompression.None), 0, file.SaveToBuffer(NbtCompression.None).Length, NbtCompression.None);
        var viaBinary = (NbtCompound)reread.RootTag;

        Assert.NotEqual(((NbtList)viaSnbt["emptyList"]).ListType, ((NbtList)viaBinary["emptyList"]).ListType);
        AssertAgree(viaSnbt, viaBinary);
        Assert.Equal(NbtCanonicalHasher.Hash(viaSnbt), NbtCanonicalHasher.Hash(viaBinary));
        AssertAgree(original, viaSnbt);
        AssertAgree(original, viaBinary);
    }

    [Fact]
    public void Names_NullVersusEmpty()
    {
        AssertAgree(new NbtInt("", 1), new NbtInt(1));
        AssertAgree(new NbtCompound(""), new NbtCompound());
        AssertAgree(new NbtCompound("") { new NbtInt("x", 1) }, new NbtCompound() { new NbtInt("x", 1) });
        AssertAgree(new NbtList("l", NbtTagType.Int) { new NbtInt(1) }, new NbtList("l", NbtTagType.Int) { new NbtInt(1) });
    }

    [Fact]
    public void Scalars_EdgeCases()
    {
        AssertAgree(new NbtDouble("d", 0.0), new NbtDouble("d", -0.0));
        AssertAgree(new NbtFloat("f", float.NaN), new NbtFloat("f", float.NaN));
        AssertAgree(new NbtByte("b", 255), new NbtByte("b", 255));
        AssertAgree(new NbtString("s", "a"), new NbtString("s", "A"));
        AssertAgree(new NbtInt("n", 1), new NbtLong("n", 1));
        AssertAgree(new NbtByteArray("a", [1, 2]), new NbtIntArray("a", [1, 2]));
        AssertAgree(new NbtLongArray("a", [1, 2, 3]), new NbtLongArray("a", [1, 2]));
    }

    [Fact]
    public void Lists_EdgeCases()
    {
        AssertAgree(new NbtList("l", NbtTagType.Int) { new NbtInt(1) }, new NbtList("l", NbtTagType.Byte) { new NbtByte(1) });
        AssertAgree(new NbtList("l"), new NbtList("l", NbtTagType.Int) { new NbtInt(1) });
        AssertAgree(new NbtList("l", NbtTagType.String) { new NbtString("a"), new NbtString("b") }, new NbtList("l", NbtTagType.String) { new NbtString("b"), new NbtString("a") });
        AssertAgree(new NbtList("l", NbtTagType.List) { new NbtList(), new NbtList(NbtTagType.Int) }, new NbtList("l", NbtTagType.List) { new NbtList(NbtTagType.Byte), new NbtList() });
    }

    [Theory]
    [InlineData("InhabitedTime")]
    [InlineData("Status")]
    [InlineData("sections/[0]/Y")]
    [InlineData("Heightmaps/MOTION_BLOCKING")]
    public void Mutations(string path)
    {
        var a = WorldBuilder.MakeChunk(7, 1, 1);
        var b = (NbtCompound)a.Clone();
        NbtTag target = path.Split('/').Aggregate((NbtTag)b, (t, seg) => seg.StartsWith('[') ? ((NbtList)t)[int.Parse(seg[1..^1])] : ((NbtCompound)t)[seg]);
        switch (target)
        {
            case NbtLong l: l.Value++; break;
            case NbtByte by: by.Value++; break;
            case NbtString s: s.Value += "!"; break;
            case NbtLongArray la: la.Value[0]++; break;
        }
        AssertAgree(a, b);
        Assert.True(NbtDiffer.Diff(a, b).HasChanges);
    }

    [Fact]
    public void AddRemoveKey()
    {
        var a = WorldBuilder.MakeChunk(7, 1, 1);
        var removed = (NbtCompound)a.Clone();
        removed.Remove("Status");
        var added = (NbtCompound)a.Clone();
        added.Add(new NbtInt("extra", 1));
        AssertAgree(a, removed);
        AssertAgree(a, added);
        AssertAgree(removed, added);
    }
}
