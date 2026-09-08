using fNbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.Core.Tests;

public class NbtCanonicalHasherTests
{
    private static ulong H(NbtTag t, bool ordered = false) => NbtCanonicalHasher.Hash(t, ordered);

    [Fact]
    public void SameTree_SameHash()
    {
        var a = NbtFixtures.SampleCompound(1);
        var b = NbtFixtures.SampleCompound(1);
        Assert.Equal(H(a), H(b));
        Assert.Equal(H(a), H((NbtCompound)a.Clone()));
    }

    [Fact]
    public void DifferentSeed_DifferentHash()
    {
        Assert.NotEqual(H(NbtFixtures.SampleCompound(1)), H(NbtFixtures.SampleCompound(2)));
    }

    [Fact]
    public void CompoundKeyOrder_Ignored()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtString("y", "s"), new NbtCompound("z") { new NbtByte("p", 1), new NbtByte("q", 2) } };
        var b = new NbtCompound("") { new NbtCompound("z") { new NbtByte("q", 2), new NbtByte("p", 1) }, new NbtString("y", "s"), new NbtInt("x", 1) };
        Assert.Equal(H(a), H(b));
    }

    [Fact]
    public void CompoundKeyOrder_RespectedWhenAsked()
    {
        var a = new NbtCompound("") { new NbtInt("x", 1), new NbtInt("y", 2) };
        var b = new NbtCompound("") { new NbtInt("y", 2), new NbtInt("x", 1) };
        Assert.NotEqual(H(a, ordered: true), H(b, ordered: true));
        Assert.Equal(H(a, ordered: true), H((NbtCompound)a.Clone(), ordered: true));
    }

    [Fact]
    public void KeyOrderInsideList_Ignored_ListOrder_Not()
    {
        var a = new NbtList("l", NbtTagType.Compound) { new NbtCompound { new NbtInt("a", 1), new NbtInt("b", 2) }, new NbtCompound { new NbtInt("c", 3) } };
        var b = new NbtList("l", NbtTagType.Compound) { new NbtCompound { new NbtInt("b", 2), new NbtInt("a", 1) }, new NbtCompound { new NbtInt("c", 3) } };
        var c = new NbtList("l", NbtTagType.Compound) { new NbtCompound { new NbtInt("c", 3) }, new NbtCompound { new NbtInt("a", 1), new NbtInt("b", 2) } };
        Assert.Equal(H(a), H(b));
        Assert.NotEqual(H(a), H(c));
    }

    [Fact]
    public void SingleScalarChange_AnywhereDeep_ChangesHash()
    {
        var a = WorldBuilder.MakeChunk(1, 0, 0);
        var b = (NbtCompound)a.Clone();
        var data = (NbtLongArray)((NbtCompound)((NbtCompound)((NbtList)b["sections"])[0])["block_states"])["data"];
        data.Value[100] ^= 1;
        Assert.NotEqual(H(a), H(b));
    }

    [Theory]
    [InlineData(NbtTagType.Byte)]
    [InlineData(NbtTagType.Short)]
    [InlineData(NbtTagType.Int)]
    [InlineData(NbtTagType.Long)]
    [InlineData(NbtTagType.String)]
    public void ScalarValue_Matters(NbtTagType type)
    {
        NbtTag Make(int v) => type switch
        {
            NbtTagType.Byte => new NbtByte("t", (byte)v),
            NbtTagType.Short => new NbtShort("t", (short)v),
            NbtTagType.Int => new NbtInt("t", v),
            NbtTagType.Long => new NbtLong("t", v),
            _ => new NbtString("t", v.ToString()),
        };
        Assert.Equal(H(Make(5)), H(Make(5)));
        Assert.NotEqual(H(Make(5)), H(Make(6)));
    }

    [Fact]
    public void Floats_CompareByBits()
    {
        Assert.NotEqual(H(new NbtDouble("d", 0.0)), H(new NbtDouble("d", -0.0)));
        Assert.NotEqual(H(new NbtFloat("f", 0f)), H(new NbtFloat("f", -0f)));
        Assert.Equal(H(new NbtDouble("d", double.NaN)), H(new NbtDouble("d", double.NaN)));
        Assert.Equal(H(new NbtFloat("f", 1.5f)), H(new NbtFloat("f", 1.5f)));
        Assert.NotEqual(H(new NbtFloat("f", 1.5f)), H(new NbtFloat("f", 1.5000001f)));
    }

    [Fact]
    public void TypeMatters_EvenForSameBytes()
    {
        Assert.NotEqual(H(new NbtInt("n", 1)), H(new NbtLong("n", 1)));
        Assert.NotEqual(H(new NbtByteArray("a", [1, 2])), H(new NbtIntArray("a", [1, 2])));
        Assert.NotEqual(H(new NbtCompound("c")), H(new NbtList("c")));
        Assert.NotEqual(H(new NbtList("l", NbtTagType.Int) { new NbtInt(1) }), H(new NbtList("l", NbtTagType.Byte) { new NbtByte(1) }));
    }

    [Fact]
    public void NameMatters_AndNullDiffersFromEmpty()
    {
        Assert.NotEqual(H(new NbtInt("a", 1)), H(new NbtInt("b", 1)));
        Assert.NotEqual(H(new NbtInt("", 1)), H(new NbtInt(1)));
    }

    [Fact]
    public void Arrays_LengthAndContent()
    {
        Assert.Equal(H(new NbtLongArray("a", [1, 2, 3])), H(new NbtLongArray("a", [1, 2, 3])));
        Assert.NotEqual(H(new NbtLongArray("a", [1, 2, 3])), H(new NbtLongArray("a", [1, 2])));
        Assert.NotEqual(H(new NbtLongArray("a", [1, 2, 3])), H(new NbtLongArray("a", [1, 3, 2])));
        Assert.NotEqual(H(new NbtByteArray("a", [])), H(new NbtByteArray("a", [0])));
    }

    [Fact]
    public void Structure_NotJustLeaves()
    {
        // Same leaves, different nesting.
        var flat = new NbtCompound("") { new NbtInt("a", 1), new NbtInt("b", 2) };
        var nested = new NbtCompound("") { new NbtCompound("a") { new NbtInt("b", 2) }, new NbtInt("a2", 1) };
        Assert.NotEqual(H(flat), H(nested));
    }

    [Fact]
    public void Empty_ContainersHashConsistently()
    {
        Assert.Equal(H(new NbtCompound("")), H(new NbtCompound("")));
        Assert.Equal(H(new NbtList("")), H(new NbtList("")));
        // Empty list element type is not content (SNBT gives Unknown, binary gives End).
        Assert.Equal(H(new NbtList("", NbtTagType.End)), H(new NbtList("", NbtTagType.Int)));
        Assert.Equal(H(new NbtList("")), H(new NbtList("", NbtTagType.Compound)));
        Assert.NotEqual(H(new NbtCompound("")), H(new NbtCompound("") { new NbtCompound("x") }));
    }
}
