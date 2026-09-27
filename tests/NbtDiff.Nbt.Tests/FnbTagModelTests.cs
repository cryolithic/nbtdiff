using fNbt;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;

namespace NbtDiff.Nbt.Tests;

/// <summary>
/// Pins the behavior of the vendored fNbt tag model: NbtCompound, NbtList, NbtTag and its
/// scalar/array subclasses, NbtContainerTag, and the supporting collection types
/// (OrderedDictionary, KeyedCollection2, Comparer2, DictionaryEnumerator, ByteCountingStream).
/// </summary>
// NbtTag.DefaultIndentString and NbtFile.DefaultBufferSize are process-wide statics that tests here
// change; one collection keeps these classes from running in parallel with each other.
[Collection("fNbt statics")]
public class FnbTagModelTests
{
    private static NbtTag RoundTrip(NbtTag tag)
    {
        var root = new NbtCompound("root", new NbtTag[] { tag });
        var bytes = new NbtFile(root).SaveToBuffer(NbtCompression.None);
        var file = new NbtFile();
        file.LoadFromBuffer(bytes, 0, bytes.Length, NbtCompression.None);
        return ((NbtCompound)file.RootTag).Get(tag.Name!)!;
    }

    // ---------- NbtCompound ----------

    [Fact]
    public void Compound_Indexers_StringAndInt()
    {
        var c = new NbtCompound("c", new NbtTag[] { new NbtInt("a", 1), new NbtShort("b", 2), new NbtString("s", "x") });

        // string indexer get
        Assert.Same(c.Get("a"), c["a"]);
        Assert.Null(c["missing"]);

        // int indexer get
        Assert.Same(c.Get("b"), c[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => c[3]);

        // string indexer set: replacing an existing key keeps its position and detaches the old tag
        var oldA = c.Get("a")!;
        c["a"] = new NbtInt("a", 99);
        Assert.Equal(99, c.Get<NbtInt>("a")!.Value);
        Assert.Equal(new[] { "a", "b", "s" }, c.Names);
        Assert.Null(oldA.Parent);

        // string indexer set errors
        Assert.Throws<ArgumentNullException>(() => c[null!] = new NbtInt("x", 1));
        Assert.Throws<ArgumentNullException>(() => c["a"] = null!);
        Assert.Equal("Given tag name must match tag's actual name.",
                    Assert.Throws<ArgumentException>(() => c["a"] = new NbtInt("other", 1)).Message);
        var otherParent = new NbtCompound("p");
        otherParent.Add(new NbtInt("a", 1));
        Assert.Equal("A tag may only be added to one compound/list at a time.",
                    Assert.Throws<ArgumentException>(() => c["a"] = otherParent.Get("a")!).Message);

        // int indexer set: replaces the tag in place and detaches the old one
        var b = c.Get("b")!;
        c[1] = new NbtLong("z", 7);
        Assert.Equal(new[] { "a", "z", "s" }, c.Names);
        Assert.Null(b.Parent);
        Assert.Same(c, c.Get("z")!.Parent);
        Assert.Equal(7L, c.Get<NbtLong>("z")!.Value);
    }

    [Fact]
    public void Compound_Get_TryGet_Contains_IndexOf_CopyTo()
    {
        var a = new NbtInt("a", 1);
        var b = new NbtString("b", "x");
        var c = new NbtCompound("c", new NbtTag[] { a, b });

        Assert.Same(a, c.Get("a"));
        Assert.Same(a, c.Get<NbtInt>("a"));
        Assert.Null(c.Get("missing"));
        Assert.Throws<ArgumentNullException>(() => c.Get((string)null!));
        Assert.Throws<InvalidCastException>(() => c.Get<NbtInt>("b"));

        Assert.True(c.TryGet("a", out NbtTag? t) && ReferenceEquals(a, t));
        Assert.False(c.TryGet("missing", out t));
        Assert.Null(t);
        Assert.True(c.TryGet<NbtInt>("a", out NbtInt? ti) && ReferenceEquals(a, ti));
        Assert.Throws<InvalidCastException>(() => c.TryGet<NbtInt>("b", out _));
        Assert.Throws<ArgumentNullException>(() => c.TryGet((string)null!, out _));

        Assert.True(c.Contains("a"));
        Assert.False(c.Contains("missing"));
        Assert.Throws<ArgumentNullException>(() => c.Contains((string)null!));

        // Contains(NbtTag) looks for exact object matches, not name matches
        Assert.True(c.Contains(a));
        Assert.False(c.Contains(new NbtInt("a", 1)));
        Assert.False(c.Contains(new NbtInt()));
        Assert.Throws<ArgumentNullException>(() => c.Contains((NbtTag)null!));

        Assert.Equal(0, c.IndexOf(a));
        Assert.Equal(1, c.IndexOf(b));
        Assert.Equal(-1, c.IndexOf(new NbtInt("a", 1)));
        Assert.Equal(0, c.IndexOf("a"));
        Assert.Equal(-1, c.IndexOf("missing"));

        Assert.Equal(new[] { "a", "b" }, c.Names);
        Assert.Equal(2, c.Count);

        var arr = new NbtTag[2];
        c.CopyTo(arr, 0);
        Assert.Same(a, arr[0]);
        Assert.Same(b, arr[1]);
    }

    [Fact]
    public void Compound_Add_Insert_Remove_RemoveAt_Clear_AddRange()
    {
        var c = new NbtCompound("c");
        c.Insert(0, new NbtInt("a", 1));
        c.Insert(0, new NbtInt("z", 9));
        Assert.Equal(new[] { "z", "a" }, c.Names);
        Assert.Same(c, c.Get("a")!.Parent);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.Insert(5, new NbtInt("x", 1)));

        c.Add(new NbtString("s", "v"));
        Assert.Equal(new[] { "z", "a", "s" }, c.Names);

        // Add errors
        Assert.Throws<ArgumentNullException>(() => c.Add((NbtTag)null!));
        Assert.Equal("Only named tags are allowed in compound tags.",
                    Assert.Throws<ArgumentException>(() => c.Add(new NbtInt())).Message);
        Assert.Equal("A tag may only be added to one compound/list at a time.",
                    Assert.Throws<ArgumentException>(() => c.Add(c.Get("a")!)).Message);
        Assert.Equal("Cannot add tag to self", Assert.Throws<ArgumentException>(() => c.Add(c)).Message);

        // RemoveAt
        c.RemoveAt(0);
        Assert.Equal(new[] { "a", "s" }, c.Names);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.RemoveAt(5));

        // Remove(string)
        var a = c.Get("a")!;
        Assert.True(c.Remove("a"));
        Assert.Null(a.Parent);
        Assert.False(c.Remove("a"));

        // duplicate name rejected
        c.Add(a);
        Assert.Throws<ArgumentException>(() => c.Add(new NbtInt("a", 5)));

        // Remove(NbtTag) is identity-based
        Assert.False(c.Remove(new NbtInt("a", 1)));
        Assert.True(c.Remove(a));
        Assert.Null(a.Parent);

        // AddRange appends in order
        c.AddRange(new NbtTag[] { new NbtInt("p", 1), new NbtInt("q", 2) });
        Assert.Equal(new[] { "s", "p", "q" }, c.Names);
        Assert.Throws<ArgumentNullException>(() => c.AddRange((IEnumerable<NbtTag>)null!));

        // Clear detaches all children
        c.Clear();
        Assert.Equal(0, c.Count);
        Assert.Null(a.Parent);
    }

    [Fact]
    public void Compound_Sort_ByName_Recursive()
    {
        var byName = Comparer<NbtTag>.Create((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));

        var c = new NbtCompound("c", new NbtTag[]
        {
            new NbtCompound("z", new NbtTag[] { new NbtInt("b", 1), new NbtInt("a", 2) }),
            new NbtList("m", new NbtTag[] { new NbtCompound(new NbtTag[] { new NbtInt("y", 1), new NbtInt("x", 2) }) }),
            new NbtInt("c", 3),
        });
        c.Sort(byName, recursive: true);

        Assert.Equal(new[] { "c", "m", "z" }, c.Names);
        // values stay with their keys
        Assert.Equal(3, c.Get<NbtInt>("c")!.Value);
        // nested compound sorted recursively
        var z = (NbtCompound)c.Get("z")!;
        Assert.Equal(new[] { "a", "b" }, z.Names);
        Assert.Equal(2, z.Get<NbtInt>("a")!.Value);
        // list of compounds: the compound children are sorted
        var m = (NbtList)c.Get("m")!;
        var inner = (NbtCompound)m[0];
        Assert.Equal(new[] { "x", "y" }, inner.Names);

        // non-recursive sort leaves children alone
        var c2 = new NbtCompound("c", new NbtTag[]
        {
            new NbtCompound("z", new NbtTag[] { new NbtInt("b", 1), new NbtInt("a", 2) }),
            new NbtInt("c", 3),
        });
        c2.Sort(byName, recursive: false);
        Assert.Equal(new[] { "c", "z" }, c2.Names);
        Assert.Equal(new[] { "b", "a" }, ((NbtCompound)c2.Get("z")!).Names);
    }

    [Fact]
    public void Compound_Constructors()
    {
        var empty = new NbtCompound();
        Assert.Equal(0, empty.Count);
        Assert.Null(empty.Name);

        var named = new NbtCompound("n");
        Assert.Equal("n", named.Name);
        Assert.Equal(0, named.Count);

        var fromTags = new NbtCompound(new NbtTag[] { new NbtInt("a", 1), new NbtInt("b", 2) });
        Assert.Null(fromTags.Name);
        Assert.Equal(new[] { "a", "b" }, fromTags.Names);
        Assert.Same(fromTags, fromTags.Get("a")!.Parent);

        var namedFromTags = new NbtCompound("n", new NbtTag[] { new NbtInt("a", 1) });
        Assert.Equal("n", namedFromTags.Name);
        Assert.Equal(new[] { "a" }, namedFromTags.Names);

        Assert.Throws<ArgumentNullException>(() => new NbtCompound((IEnumerable<NbtTag>)null!));
        Assert.Throws<ArgumentNullException>(() => new NbtCompound("n", (IEnumerable<NbtTag>)null!));
        Assert.Equal("Only named tags are allowed in compound tags.",
                    Assert.Throws<ArgumentException>(() => new NbtCompound(new NbtTag[] { new NbtInt() })).Message);
        var taken = new NbtCompound("p");
        taken.Add(new NbtInt("a", 1));
        Assert.Equal("A tag may only be added to one compound/list at a time.",
                    Assert.Throws<ArgumentException>(() => new NbtCompound(new NbtTag[] { taken.Get("a")! })).Message);
        Assert.Throws<ArgumentNullException>(() => new NbtCompound(new NbtTag[] { null! }));

        // copy constructor is a deep copy
        var src = new NbtCompound("c", new NbtTag[] { new NbtInt("a", 1) });
        var copy = new NbtCompound(src);
        Assert.Equal("c", copy.Name);
        Assert.Equal(1, copy.Get<NbtInt>("a")!.Value);
        Assert.NotSame(src.Get("a"), copy.Get("a"));
        src.Get<NbtInt>("a")!.Value = 42;
        Assert.Equal(1, copy.Get<NbtInt>("a")!.Value);
        Assert.Throws<ArgumentNullException>(() => new NbtCompound((NbtCompound)null!));
    }

    [Fact]
    public void Compound_CanAdd_AlwaysTrue()
    {
        var c = new NbtCompound("c");
        foreach (var t in new[] { NbtTagType.Byte, NbtTagType.Int, NbtTagType.List, NbtTagType.Compound, NbtTagType.LongArray })
            Assert.True(c.CanAdd(t));
    }

    [Fact]
    public void Compound_PrettyPrint_Exact()
    {
        var c = new NbtCompound("root", new NbtTag[] { new NbtInt("a", 1), new NbtString("b", "hi") });
        Assert.Equal("TAG_Compound(\"root\"): 2 entries {\n  TAG_Int(\"a\"): 1\n  TAG_String(\"b\"): \"hi\"\n}", c.ToString());
        Assert.Equal("TAG_Compound(\"e\"): 0 entries {}", new NbtCompound("e").ToString());
        Assert.Equal("TAG_Compound(\"root\"): 2 entries {\n>TAG_Int(\"a\"): 1\n>TAG_String(\"b\"): \"hi\"\n}", c.ToString(">"));
    }

    [Fact]
    public void Compound_Rename_KeepsIndex()
    {
        var c = new NbtCompound("c", new NbtTag[] { new NbtInt("a", 1), new NbtInt("b", 2) });
        var a = (NbtInt)c.Get("a")!;

        a.Name = "z";
        Assert.Equal(new[] { "z", "b" }, c.Names);
        Assert.False(c.Contains("a"));
        Assert.Equal("z", a.Name);
        Assert.Equal("c.z", a.Path);

        // renaming onto an existing name is rejected
        Assert.Equal("Cannot rename: a tag with the name already exists in this compound.",
                    Assert.Throws<ArgumentException>(() => a.Name = "b").Message);
    }

    [Fact]
    public void Compound_ReadTag_WriteTag()
    {
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        w.Write(NbtTagType.Compound);
        w.Write("root");
        w.Write(NbtTagType.Int);
        w.Write("a");
        w.Write(1);
        w.Write(NbtTagType.Compound);
        w.Write("sub");
        w.Write(NbtTagType.String);
        w.Write("s");
        w.Write("hi");
        w.Write(NbtTagType.End);
        w.Write(NbtTagType.List);
        w.Write("lst");
        w.Write(NbtTagType.Int);
        w.Write(2);
        w.Write(5);
        w.Write(6);
        w.Write(NbtTagType.End);

        // ReadTag
        ms.Position = 0;
        var r = new NbtBinaryReader(ms, true);
        r.ReadTagType();
        r.ReadString();
        var c = new NbtCompound("root");
        Assert.True(c.ReadTag(r));
        Assert.Equal(3, c.Count);
        Assert.Equal(new[] { "a", "sub", "lst" }, c.Names);
        Assert.Equal(1, c.Get<NbtInt>("a")!.Value);
        var sub = (NbtCompound)c.Get("sub")!;
        Assert.Equal("hi", sub.Get<NbtString>("s")!.Value);
        var lst = (NbtList)c.Get("lst")!;
        Assert.Equal(new[] { 5, 6 }, lst.ToArray<NbtInt>().Select(x => x.Value));
        Assert.Equal(ms.Length, r.BaseStream.Position);

        // WriteTag round-trips
        using var ms2 = new MemoryStream();
        var w2 = new NbtBinaryWriter(ms2, true);
        c.WriteTag(w2);
        Assert.Equal(ms.ToArray(), ms2.ToArray());

        // a null name cannot be written
        Assert.Equal("Name is null",
                    Assert.Throws<NbtFormatException>(() => new NbtCompound().WriteTag(new NbtBinaryWriter(new MemoryStream(), true))).Message);
    }

    // ---------- NbtList ----------

    [Fact]
    public void List_ListType_Setter_Semantics()
    {
        var l = new NbtList("l");
        Assert.Equal(NbtTagType.Unknown, l.ListType);

        // empty list: End is allowed
        l.ListType = NbtTagType.End;
        Assert.Equal(NbtTagType.End, l.ListType);

        l.ListType = NbtTagType.Int;
        l.Add(new NbtInt(1));
        Assert.Equal(NbtTagType.Int, l.ListType);

        // non-empty list: End is rejected
        Assert.Equal("Only empty list tags may have TagType of End.",
                    Assert.Throws<ArgumentException>(() => l.ListType = NbtTagType.End).Message);
        // non-empty list: mismatched type is rejected
        Assert.Equal("Given NbtTagType (String) does not match actual element type (Int)",
                    Assert.Throws<ArgumentException>(() => l.ListType = NbtTagType.String).Message);
        // matching type is a no-op
        l.ListType = NbtTagType.Int;
        Assert.Equal(NbtTagType.Int, l.ListType);
        // unrecognized type is rejected
        Assert.Throws<ArgumentOutOfRangeException>(() => l.ListType = (NbtTagType)0x0d);
    }

    [Fact]
    public void List_Indexers_Get_ToArray()
    {
        var l = new NbtList("l", new NbtTag[] { new NbtInt(1), new NbtInt(2) });
        Assert.Equal(2, l.Count);
        Assert.Equal(1, l[0].IntValue);
        Assert.Equal(2, l[1].IntValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => l[2]);

        // set errors
        Assert.Equal("Items must be of type Int",
                    Assert.Throws<ArgumentException>(() => l[0] = new NbtString("x")).Message);
        Assert.Throws<ArgumentNullException>(() => l[0] = null!);
        Assert.Equal("Named tag given. A list may only contain unnamed tags.",
                    Assert.Throws<ArgumentException>(() => l[0] = new NbtInt("named", 9)).Message);
        var otherParent = new NbtList("p");
        otherParent.Add(new NbtInt(9));
        Assert.Equal("A tag may only be added to one compound/list at a time.",
                    Assert.Throws<ArgumentException>(() => l[0] = otherParent[0]).Message);

        // valid replace detaches the old tag
        var old = l[0];
        l[0] = new NbtInt(7);
        Assert.Equal(7, l[0].IntValue);
        Assert.Null(old.Parent);
        Assert.Same(l, l[0].Parent);

        // Get<T>
        Assert.Equal(2, l.Get<NbtInt>(1).Value);
        Assert.Throws<InvalidCastException>(() => l.Get<NbtString>(0));

        // ToArray
        var arr = l.ToArray();
        Assert.Equal(2, arr.Length);
        Assert.Same(l[0], arr[0]);
        Assert.Equal(new[] { 7, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.Throws<InvalidCastException>(() => l.ToArray<NbtString>());
    }

    [Fact]
    public void List_Insert_Remove_RemoveAt_Clear_Contains_IndexOf_CanAdd()
    {
        var l = new NbtList("l");
        // empty list accepts any type
        Assert.True(l.CanAdd(NbtTagType.String));
        Assert.True(l.CanAdd(NbtTagType.Int));

        l.Insert(0, new NbtInt(1));
        Assert.Equal(NbtTagType.Int, l.ListType); // inferred on first insert
        l.Insert(0, new NbtInt(9));
        Assert.Equal(new[] { 9, 1 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.Same(l, l[0].Parent);
        Assert.Throws<ArgumentOutOfRangeException>(() => l.Insert(5, new NbtInt(3)));

        // mismatched insert into a non-empty list
        Assert.Equal("Items must be of type Int",
                    Assert.Throws<ArgumentException>(() => l.Insert(1, new NbtString("x"))).Message);

        l.Add(new NbtInt(2));
        Assert.Equal(new[] { 9, 1, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));

        // Add errors
        Assert.Throws<ArgumentNullException>(() => l.Add((NbtTag)null!));
        Assert.Equal("Named tag given. A list may only contain unnamed tags.",
                    Assert.Throws<ArgumentException>(() => l.Add(new NbtInt("n", 1))).Message);
        Assert.Equal("Items in this list must be of type Int. Given type: String",
                    Assert.Throws<ArgumentException>(() => l.Add(new NbtString("x"))).Message);
        var otherParent = new NbtList("p");
        otherParent.Add(new NbtInt(1));
        Assert.Equal("A tag may only be added to one compound/list at a time.",
                    Assert.Throws<ArgumentException>(() => l.Add(otherParent[0])).Message);
        Assert.Equal("A list tag may not be added to itself or to its child tag.",
                    Assert.Throws<ArgumentException>(() => l.Add(l)).Message);

        // non-empty list only accepts its own type
        Assert.True(l.CanAdd(NbtTagType.Int));
        Assert.False(l.CanAdd(NbtTagType.String));

        // Contains / IndexOf
        Assert.True(l.Contains(l[0]));
        Assert.False(l.Contains(new NbtInt(1)));
        Assert.Equal(0, l.IndexOf(l[0]));
        Assert.Equal(-1, l.IndexOf(new NbtInt(9)));
        Assert.Equal(-1, l.IndexOf(null!));

        // RemoveAt
        l.RemoveAt(0);
        Assert.Equal(new[] { 1, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.Throws<ArgumentOutOfRangeException>(() => l.RemoveAt(5));

        // Remove(NbtTag)
        Assert.True(l.Remove(l[0]));
        Assert.Equal(new[] { 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.False(l.Remove(new NbtInt(1)));

        // removing the last element resets ListType to Unknown
        Assert.True(l.Remove(l[0]));
        Assert.Equal(0, l.Count);
        Assert.Equal(NbtTagType.Unknown, l.ListType);

        // Clear detaches children and resets ListType
        l.Add(new NbtInt(5));
        var first = l[0];
        l.Clear();
        Assert.Equal(0, l.Count);
        Assert.Null(first.Parent);
        Assert.Equal(NbtTagType.Unknown, l.ListType);

        // AddRange
        l.AddRange(new NbtTag[] { new NbtInt(1), new NbtInt(2) });
        Assert.Equal(new[] { 1, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.Throws<ArgumentNullException>(() => l.AddRange((IEnumerable<NbtTag>)null!));

        // CopyTo
        var arr = new NbtTag[2];
        l.CopyTo(arr, 0);
        Assert.Same(l[0], arr[0]);
        Assert.Same(l[1], arr[1]);
    }

    [Fact]
    public void List_Constructors()
    {
        var empty = new NbtList();
        Assert.Equal(NbtTagType.Unknown, empty.ListType);
        Assert.Equal(0, empty.Count);

        var named = new NbtList("l");
        Assert.Equal("l", named.Name);
        Assert.Equal(NbtTagType.Unknown, named.ListType);

        var typed = new NbtList(NbtTagType.Int);
        Assert.Equal(NbtTagType.Int, typed.ListType);
        Assert.Equal(0, typed.Count);

        var inferred = new NbtList(new NbtTag[] { new NbtInt(1) });
        Assert.Null(inferred.Name);
        Assert.Equal(NbtTagType.Int, inferred.ListType);

        var namedInferred = new NbtList("l", new NbtTag[] { new NbtInt(1) });
        Assert.Equal("l", namedInferred.Name);
        Assert.Equal(NbtTagType.Int, namedInferred.ListType);
        Assert.Same(namedInferred, namedInferred[0].Parent);

        var explicitType = new NbtList(new NbtTag[] { new NbtInt(1) }, NbtTagType.Int);
        Assert.Equal(NbtTagType.Int, explicitType.ListType);


        // null collections
        Assert.Throws<ArgumentNullException>(() => new NbtList((IEnumerable<NbtTag>)null!));
        Assert.Throws<ArgumentNullException>(() => new NbtList("l", (IEnumerable<NbtTag>)null!));
        Assert.Throws<ArgumentNullException>(() => new NbtList((IEnumerable<NbtTag>)null!, NbtTagType.Int));

        // unrecognized type
        Assert.Throws<ArgumentOutOfRangeException>(() => new NbtList("l", (NbtTagType)0x0d));

        // copy constructor is a deep copy
        var src = new NbtList("l", new NbtTag[] { new NbtInt(1) });
        var copy = new NbtList(src);
        Assert.Equal("l", copy.Name);
        Assert.Equal(1, copy[0].IntValue);
        Assert.NotSame(src[0], copy[0]);
        src.Get<NbtInt>(0).Value = 42;
        Assert.Equal(1, copy[0].IntValue);
        Assert.Throws<ArgumentNullException>(() => new NbtList((NbtList)null!));
    }

    [Fact]
    public void List_ReadTag()
    {
        // Int list
        using (var ms = new MemoryStream())
        {
            var w = new NbtBinaryWriter(ms, true);
            w.Write(NbtTagType.List);
            w.Write("l");
            w.Write(NbtTagType.Int);
            w.Write(2);
            w.Write(-5);
            w.Write(7);
            ms.Position = 0;
            var r = new NbtBinaryReader(ms, true);
            r.ReadTagType();
            r.ReadString();
            var l = new NbtList("l");
            Assert.True(l.ReadTag(r));
            Assert.Equal(NbtTagType.Int, l.ListType);
            Assert.Equal(2, l.Count);
            Assert.Equal(-5, l[0].IntValue);
            Assert.Equal(7, l[1].IntValue);
            Assert.Same(l, l[0].Parent);
            Assert.Equal(ms.Length, r.BaseStream.Position);
        }

        // empty list: the element type byte End (0) is read back as ListType End
        using (var ms = new MemoryStream())
        {
            var w = new NbtBinaryWriter(ms, true);
            w.Write(NbtTagType.List);
            w.Write("l");
            w.Write(NbtTagType.End);
            w.Write(0);
            ms.Position = 0;
            var r = new NbtBinaryReader(ms, true);
            r.ReadTagType();
            r.ReadString();
            var l = new NbtList("l");
            Assert.True(l.ReadTag(r));
            Assert.Equal(NbtTagType.End, l.ListType);
            Assert.Equal(0, l.Count);
        }

        // negative length
        using (var ms = new MemoryStream())
        {
            var w = new NbtBinaryWriter(ms, true);
            w.Write(NbtTagType.List);
            w.Write("l");
            w.Write(NbtTagType.Int);
            w.Write(-1);
            ms.Position = 0;
            var r = new NbtBinaryReader(ms, true);
            r.ReadTagType();
            r.ReadString();
            var ex = Assert.Throws<NbtFormatException>(() => new NbtList("l").ReadTag(r));
            Assert.Equal("Negative list size given.", ex.Message);
        }
    }

    [Fact]
    public void List_WriteData_UnknownType_BecomesEnd()
    {
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        var l = new NbtList("l"); // ListType is Unknown
        w.Write(NbtTagType.List);
        w.Write("l");
        l.WriteData(w);
        var bytes = ms.ToArray();
        Assert.Equal(0x09, bytes[0]); // TAG_List
        Assert.Equal(0x00, bytes[1]); // name length high byte
        Assert.Equal(0x01, bytes[2]); // name length low byte
        Assert.Equal((byte)'l', bytes[3]);
        Assert.Equal(0x00, bytes[4]); // Unknown is normalized to End
        Assert.Equal(0, BitConverter.ToInt32(bytes, 5)); // element count
    }

    [Fact]
    public void List_PrettyPrint_Exact()
    {
        var l = new NbtList("l", new NbtTag[] { new NbtInt(1), new NbtInt(2) });
        Assert.Equal("TAG_List(\"l\"): 2 entries {\n  TAG_Int: 1\n  TAG_Int: 2\n}", l.ToString());
        Assert.Equal("TAG_List(\"e\"): 0 entries {}", new NbtList("e").ToString());
        Assert.Equal("TAG_List: 1 entries {\n  TAG_Int: 7\n}", new NbtList(new NbtTag[] { new NbtInt(7) }).ToString());
    }

    [Fact]
    public void List_RoundTripsThroughBinary()
    {
        var l = new NbtList("l", new NbtTag[] { new NbtInt(-5), new NbtInt(7) });
        var root = new NbtCompound("root", new NbtTag[] { l });
        var bytes = new NbtFile(root).SaveToBuffer(NbtCompression.None);
        var file = new NbtFile();
        file.LoadFromBuffer(bytes, 0, bytes.Length, NbtCompression.None);
        var l2 = (NbtList)((NbtCompound)file.RootTag).Get("l")!;
        Assert.Equal(NbtTagType.Int, l2.ListType);
        Assert.Equal(new[] { -5, 7 }, l2.ToArray<NbtInt>().Select(x => x.Value));

        // an empty list is written with element type End and reads back as End
        var root2 = new NbtCompound("root", new NbtTag[] { new NbtList("l") });
        var bytes2 = new NbtFile(root2).SaveToBuffer(NbtCompression.None);
        var file2 = new NbtFile();
        file2.LoadFromBuffer(bytes2, 0, bytes2.Length, NbtCompression.None);
        var l3 = (NbtList)((NbtCompound)file2.RootTag).Get("l")!;
        Assert.Equal(NbtTagType.End, l3.ListType);
        Assert.Equal(0, l3.Count);
    }

    // ---------- NbtTag ----------

    [Fact]
    public void Tag_HasValue()
    {
        Assert.False(new NbtCompound("c").HasValue);
        Assert.False(new NbtList("l").HasValue);
        Assert.True(new NbtByte("b").HasValue);
        Assert.True(new NbtShort("s").HasValue);
        Assert.True(new NbtInt("i").HasValue);
        Assert.True(new NbtLong("l").HasValue);
        Assert.True(new NbtFloat("f").HasValue);
        Assert.True(new NbtDouble("d").HasValue);
        Assert.True(new NbtByteArray("b").HasValue);
        Assert.True(new NbtString("s").HasValue);
        Assert.True(new NbtIntArray("i").HasValue);
        Assert.True(new NbtLongArray("l").HasValue);
    }

    [Fact]
    public void Tag_ValueAccessors_Coercion()
    {
        // NbtByte.Value is an unsigned byte: 255 widens to 255, not -1
        var b = new NbtByte("b", 255);
        Assert.Equal((byte)255, b.ByteValue);
        Assert.Equal((short)255, b.ShortValue);
        Assert.Equal(255, b.IntValue);
        Assert.Equal(255L, b.LongValue);
        Assert.Equal(255.0f, b.FloatValue);
        Assert.Equal(255.0, b.DoubleValue);
        Assert.Equal("255", b.StringValue);

        var s = new NbtShort("s", -300);
        Assert.Equal((short)-300, s.ShortValue);
        Assert.Equal(-300, s.IntValue);
        Assert.Equal(-300L, s.LongValue);
        Assert.Equal(-300.0f, s.FloatValue);
        Assert.Equal(-300.0, s.DoubleValue);
        Assert.Equal("-300", s.StringValue);

        var i = new NbtInt("i", -42);
        Assert.Equal(-42, i.IntValue);
        Assert.Equal(-42L, i.LongValue);
        Assert.Equal(-42.0f, i.FloatValue);
        Assert.Equal(-42.0, i.DoubleValue);
        Assert.Equal("-42", i.StringValue);

        var l = new NbtLong("l", 7L);
        Assert.Equal(7L, l.LongValue);
        Assert.Equal(7.0f, l.FloatValue);
        Assert.Equal(7.0, l.DoubleValue);
        Assert.Equal("1234567890123", new NbtLong("l", 1234567890123L).StringValue);

        var f = new NbtFloat("f", 2.5f);
        Assert.Equal(2.5f, f.FloatValue);
        Assert.Equal(2.5, f.DoubleValue);
        Assert.Equal("2.5", f.StringValue);

        var d = new NbtDouble("d", -3.5);
        Assert.Equal(-3.5, d.DoubleValue);
        Assert.Equal(-3.5f, d.FloatValue);
        Assert.Equal("-3.5", d.StringValue);

        var str = new NbtString("s", "héllo");
        Assert.Equal("héllo", str.StringValue);

        // array accessors return the stored arrays
        Assert.Equal(new byte[] { 1, 2 }, new NbtByteArray("b", new byte[] { 1, 2 }).ByteArrayValue);
        Assert.Equal(new[] { -1 }, new NbtIntArray("i", new[] { -1 }).IntArrayValue);
        Assert.Equal(new[] { 7L }, new NbtLongArray("l", new[] { 7L }).LongArrayValue);
    }

    [Fact]
    public void Tag_ValueAccessors_WrongType_Throw()
    {
        var i = new NbtInt("i", 1);
        Assert.Equal("Cannot get ByteValue from TAG_Int",
                    Assert.Throws<InvalidCastException>(() => i.ByteValue).Message);
        Assert.Equal("Cannot get ShortValue from TAG_Int",
                    Assert.Throws<InvalidCastException>(() => i.ShortValue).Message);
        Assert.Equal("Cannot get IntValue from TAG_Long",
                    Assert.Throws<InvalidCastException>(() => new NbtLong("l", 1).IntValue).Message);
        Assert.Equal("Cannot get LongValue from TAG_Float",
                    Assert.Throws<InvalidCastException>(() => new NbtFloat("f", 1).LongValue).Message);
        Assert.Equal("Cannot get FloatValue from TAG_String",
                    Assert.Throws<InvalidCastException>(() => new NbtString("s", "x").FloatValue).Message);
        Assert.Equal("Cannot get DoubleValue from TAG_String",
                    Assert.Throws<InvalidCastException>(() => new NbtString("s", "x").DoubleValue).Message);
        Assert.Equal("Cannot get ByteArrayValue from TAG_Int",
                    Assert.Throws<InvalidCastException>(() => i.ByteArrayValue).Message);
        Assert.Equal("Cannot get IntArrayValue from TAG_String",
                    Assert.Throws<InvalidCastException>(() => new NbtString("s", "x").IntArrayValue).Message);
        Assert.Equal("Cannot get LongArrayValue from TAG_String",
                    Assert.Throws<InvalidCastException>(() => new NbtString("s", "x").LongArrayValue).Message);
        Assert.Equal("Cannot get StringValue from TAG_Compound",
                    Assert.Throws<InvalidCastException>(() => new NbtCompound("c").StringValue).Message);
        Assert.Equal("Cannot get StringValue from TAG_Byte_Array",
                    Assert.Throws<InvalidCastException>(() => new NbtByteArray("b").StringValue).Message);
        Assert.Equal("Cannot get StringValue from TAG_Int_Array",
                    Assert.Throws<InvalidCastException>(() => new NbtIntArray("i").StringValue).Message);
        Assert.Equal("Cannot get StringValue from TAG_List",
                    Assert.Throws<InvalidCastException>(() => new NbtList("l").StringValue).Message);
    }

    [Fact]
    public void Tag_SetName_Rules()
    {
        // a detached tag may be renamed to null
        var t = new NbtInt("a");
        t.Name = null;
        Assert.Null(t.Name);

        var c = new NbtCompound("c", new NbtTag[] { new NbtInt("a"), new NbtInt("b") });
        var a = (NbtInt)c.Get("a")!;
        Assert.Equal("Name of tags inside an NbtCompound may not be null. (Parameter 'value')",
                    Assert.Throws<ArgumentNullException>(() => a.Name = null).Message);

        a.Name = "z";
        Assert.Equal(new[] { "z", "b" }, c.Names);
        Assert.False(c.Contains("a"));
        Assert.Equal("c.z", a.Path);

        // renaming onto an existing name is rejected
        Assert.Equal("Cannot rename: a tag with the name already exists in this compound.",
                    Assert.Throws<ArgumentException>(() => a.Name = "b").Message);
    }

    [Fact]
    public void Tag_GetCanonicalTagName_AllTypes()
    {
        Assert.Equal("TAG_Byte", NbtTag.GetCanonicalTagName(NbtTagType.Byte));
        Assert.Equal("TAG_Byte_Array", NbtTag.GetCanonicalTagName(NbtTagType.ByteArray));
        Assert.Equal("TAG_Compound", NbtTag.GetCanonicalTagName(NbtTagType.Compound));
        Assert.Equal("TAG_Double", NbtTag.GetCanonicalTagName(NbtTagType.Double));
        Assert.Equal("TAG_End", NbtTag.GetCanonicalTagName(NbtTagType.End));
        Assert.Equal("TAG_Float", NbtTag.GetCanonicalTagName(NbtTagType.Float));
        Assert.Equal("TAG_Int", NbtTag.GetCanonicalTagName(NbtTagType.Int));
        Assert.Equal("TAG_Int_Array", NbtTag.GetCanonicalTagName(NbtTagType.IntArray));
        Assert.Equal("TAG_List", NbtTag.GetCanonicalTagName(NbtTagType.List));
        Assert.Equal("TAG_Long", NbtTag.GetCanonicalTagName(NbtTagType.Long));
        Assert.Equal("TAG_Long_Array", NbtTag.GetCanonicalTagName(NbtTagType.LongArray));
        Assert.Equal("TAG_Short", NbtTag.GetCanonicalTagName(NbtTagType.Short));
        Assert.Equal("TAG_String", NbtTag.GetCanonicalTagName(NbtTagType.String));
        Assert.Null(NbtTag.GetCanonicalTagName(NbtTagType.Unknown));
    }

    [Fact]
    public void Tag_Path()
    {
        Assert.Equal("x", new NbtInt("x").Path);
        Assert.Equal("", new NbtInt().Path);

        var c = new NbtCompound("root", new NbtTag[]
        {
            new NbtInt("child", 1),
            new NbtCompound("mid", new NbtTag[] { new NbtString("leaf", "v") }),
            new NbtList("lst", new NbtTag[] { new NbtInt(1), new NbtInt(2) }),
        });
        Assert.Equal("root.child", c.Get("child")!.Path);
        Assert.Equal("root.mid", c.Get("mid")!.Path);
        Assert.Equal("root.mid.leaf", ((NbtCompound)c.Get("mid")!).Get("leaf")!.Path);
        var lst = (NbtList)c.Get("lst")!;
        Assert.Equal("root.lst[0]", lst[0].Path);
        Assert.Equal("root.lst[1]", lst[1].Path);
    }

    [Fact]
    public void Tag_Indexers_ThrowOnNonContainers()
    {
        var t = new NbtInt("i", 1);
        Assert.Equal("String indexers only work on NbtCompound tags.",
                    Assert.Throws<InvalidOperationException>(() => t["x"]).Message);
        Assert.Equal("String indexers only work on NbtCompound tags.",
                    Assert.Throws<InvalidOperationException>(() => t["x"] = new NbtInt()).Message);
        Assert.Equal("Integer indexers only work on container tags.",
                    Assert.Throws<InvalidOperationException>(() => t[0]).Message);
        Assert.Equal("Integer indexers only work on container tags.",
                    Assert.Throws<InvalidOperationException>(() => t[0] = new NbtInt()).Message);
    }

    [Fact]
    public void Tag_DefaultIndentString()
    {
        var original = NbtTag.DefaultIndentString;
        Assert.Equal("  ", original);
        try
        {
            NbtTag.DefaultIndentString = ">";
            Assert.Equal(">", NbtTag.DefaultIndentString);
            Assert.Equal("TAG_Int(\"i\"): 1", new NbtInt("i", 1).ToString());
            Assert.Equal("TAG_Compound(\"c\"): 1 entries {\n>TAG_Int(\"i\"): 1\n}",
                        new NbtCompound("c", new NbtTag[] { new NbtInt("i", 1) }).ToString());
        }
        finally
        {
            NbtTag.DefaultIndentString = original;
        }
        Assert.Throws<ArgumentNullException>(() => NbtTag.DefaultIndentString = null!);
    }

    // ---------- Scalar tags ----------

    [Fact]
    public void Scalars_Value_Setter_RoundTrips()
    {
        var b = new NbtByte("b", 1);
        b.Value = 255;
        Assert.Equal((byte)255, b.Value);
        Assert.Equal((byte)255, ((NbtByte)RoundTrip(b)).Value);

        var s = new NbtShort("s", 1);
        s.Value = -300;
        Assert.Equal((short)-300, s.Value);
        Assert.Equal((short)-300, ((NbtShort)RoundTrip(s)).Value);

        var i = new NbtInt("i", 1);
        i.Value = -42;
        Assert.Equal(-42, i.Value);
        Assert.Equal(-42, ((NbtInt)RoundTrip(i)).Value);

        var l = new NbtLong("l", 1);
        l.Value = long.MinValue;
        Assert.Equal(long.MinValue, l.Value);
        Assert.Equal(long.MinValue, ((NbtLong)RoundTrip(l)).Value);

        var f = new NbtFloat("f", 1f);
        f.Value = 2.5f;
        Assert.Equal(2.5f, f.Value);
        Assert.Equal(2.5f, ((NbtFloat)RoundTrip(f)).Value);

        var d = new NbtDouble("d", 1.0);
        d.Value = -3.5;
        Assert.Equal(-3.5, d.Value);
        Assert.Equal(-3.5, ((NbtDouble)RoundTrip(d)).Value);

        var str = new NbtString("s", "x");
        str.Value = "héllo";
        Assert.Equal("héllo", str.Value);
        Assert.Equal("héllo", ((NbtString)RoundTrip(str)).Value);
    }

    [Fact]
    public void Scalars_Constructors()
    {
        // name-only constructors default the value to 0
        Assert.Equal(0, new NbtByte("b").Value);
        Assert.Equal(0, new NbtShort("s").Value);
        Assert.Equal(0, new NbtInt("i").Value);
        Assert.Equal(0L, new NbtLong("l").Value);
        Assert.Equal(0f, new NbtFloat("f").Value);
        Assert.Equal(0.0, new NbtDouble("d").Value);

        // value-only constructors leave the name null
        Assert.Null(new NbtByte((byte)7).Name);
        Assert.Equal((byte)7, new NbtByte((byte)7).Value);
        Assert.Equal("v", new NbtString("v").Value);
        Assert.Null(new NbtString("v").Name);

        // copy constructor is a deep copy
        var src = new NbtString("s", "x");
        var copy = new NbtString(src);
        Assert.Equal("s", copy.Name);
        Assert.Equal("x", copy.Value);
        src.Value = "y";
        Assert.Equal("x", copy.Value);
        Assert.Throws<ArgumentNullException>(() => new NbtString((NbtString)null!));

        // null values are rejected
        Assert.Throws<ArgumentNullException>(() => new NbtString((string)null!));
        Assert.Throws<ArgumentNullException>(() => new NbtString("s", null!));
        var t = new NbtString("s");
        Assert.Throws<ArgumentNullException>(() => t.Value = null!);
    }

    [Fact]
    public void Scalars_PrettyPrint_Exact()
    {
        Assert.Equal("TAG_Byte(\"b\"): 255", new NbtByte("b", 255).ToString());
        Assert.Equal("TAG_Byte: 0", new NbtByte((byte)0).ToString());
        Assert.Equal("TAG_Short(\"s\"): -300", new NbtShort("s", -300).ToString());
        Assert.Equal("TAG_Int(\"i\"): -42", new NbtInt("i", -42).ToString());
        Assert.Equal("TAG_Long(\"l\"): -9223372036854775808", new NbtLong("l", long.MinValue).ToString());
        Assert.Equal("TAG_Float(\"f\"): 2.5", new NbtFloat("f", 2.5f).ToString());
        Assert.Equal("TAG_Double(\"d\"): -3.5", new NbtDouble("d", -3.5).ToString());
        Assert.Equal("TAG_String(\"s\"): \"hi\"", new NbtString("s", "hi").ToString());
        Assert.Equal("TAG_String: \"hi\"", new NbtString("hi").ToString());
    }

    [Fact]
    public void Scalars_ReadTag()
    {
        // NbtInt reads its value
        using (var ms = new MemoryStream())
        {
            var w = new NbtBinaryWriter(ms, true);
            w.Write(NbtTagType.Int);
            w.Write("i");
            w.Write(-42);
            ms.Position = 0;
            var r = new NbtBinaryReader(ms, true);
            r.ReadTagType();
            r.ReadString();
            var t = new NbtInt("i");
            Assert.True(t.ReadTag(r));
            Assert.Equal(-42, t.Value);
        }
    }

    // ---------- Array tags ----------

    [Fact]
    public void ByteArray_Value_Indexer_Constructors()
    {
        var t = new NbtByteArray("b", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, t.Value);

        // the array constructor clones
        var src = new byte[] { 9, 9 };
        var fromSrc = new NbtByteArray("b", src);
        src[0] = 42;
        Assert.Equal(9, fromSrc.Value[0]);

        // the Value setter stores the array as-is (no clone)
        var arr = new byte[] { 5, 6 };
        t.Value = arr;
        Assert.Same(arr, t.Value);
        Assert.Throws<ArgumentNullException>(() => t.Value = null!);

        // element indexer
        t[0] = 42;
        Assert.Equal(42, arr[0]);
        Assert.Equal(6, t[1]);
        Assert.Throws<IndexOutOfRangeException>(() => t[2]);

        // name-only constructor: empty array
        var empty = new NbtByteArray("b");
        Assert.Empty(empty.Value);

        // copy constructor clones
        var copy = new NbtByteArray(t);
        Assert.Equal("b", copy.Name);
        Assert.Equal(arr, copy.Value);
        Assert.NotSame(arr, copy.Value);
        Assert.Throws<ArgumentNullException>(() => new NbtByteArray((NbtByteArray)null!));
    }

    [Fact]
    public void IntArray_Value_Indexer_Constructors()
    {
        var t = new NbtIntArray("i", new[] { -1, 5 });
        Assert.Equal(new[] { -1, 5 }, t.Value);

        var src = new[] { 9, 9 };
        var fromSrc = new NbtIntArray("i", src);
        src[0] = 42;
        Assert.Equal(9, fromSrc.Value[0]);

        var arr = new[] { 7 };
        t.Value = arr;
        Assert.Same(arr, t.Value);
        Assert.Throws<ArgumentNullException>(() => t.Value = null!);

        t[0] = -42;
        Assert.Equal(-42, t.Value[0]);
        Assert.Throws<IndexOutOfRangeException>(() => t[1]);

        var empty = new NbtIntArray("i");
        Assert.Empty(empty.Value);

        var copy = new NbtIntArray(t);
        Assert.Equal("i", copy.Name);
        Assert.Equal(arr, copy.Value);
        Assert.NotSame(arr, copy.Value);
        Assert.Throws<ArgumentNullException>(() => new NbtIntArray((NbtIntArray)null!));
    }

    [Fact]
    public void LongArray_Value_Indexer_Constructors()
    {
        var t = new NbtLongArray("l", new[] { long.MinValue, 7L });
        Assert.Equal(new[] { long.MinValue, 7L }, t.Value);

        var src = new[] { 9L, 9L };
        var fromSrc = new NbtLongArray("l", src);
        src[0] = 42;
        Assert.Equal(9L, fromSrc.Value[0]);

        var arr = new[] { 7L };
        t.Value = arr;
        Assert.Same(arr, t.Value);
        Assert.Throws<ArgumentNullException>(() => t.Value = null!);

        t[0] = long.MaxValue;
        Assert.Equal(long.MaxValue, t.Value[0]);
        Assert.Throws<IndexOutOfRangeException>(() => t[1]);

        var empty = new NbtLongArray("l");
        Assert.Empty(empty.Value);

        var copy = new NbtLongArray(t);
        Assert.Equal("l", copy.Name);
        Assert.Equal(arr, copy.Value);
        Assert.NotSame(arr, copy.Value);
        Assert.Throws<ArgumentNullException>(() => new NbtLongArray((NbtLongArray)null!));
    }

    [Fact]
    public void ArrayTags_PrettyPrint_Exact()
    {
        Assert.Equal("TAG_Byte_Array(\"b\"): [3 bytes]", new NbtByteArray("b", new byte[] { 1, 2, 3 }).ToString());
        Assert.Equal("TAG_Int_Array(\"i\"): [2 ints]", new NbtIntArray("i", new[] { -1, 5 }).ToString());
        Assert.Equal("TAG_Long_Array(\"l\"): [1 longs]", new NbtLongArray("l", new[] { 7L }).ToString());
        Assert.Equal("TAG_Byte_Array: [0 bytes]", new NbtByteArray().ToString());
    }

    [Fact]
    public void ByteArray_ReadTag()
    {
        // negative length: ReadTag must reject it before touching the data
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        w.Write(NbtTagType.ByteArray);
        w.Write("b");
        w.Write(-1);
        ms.Position = 0;
        var r = new NbtBinaryReader(ms, true);
        r.ReadTagType();
        r.ReadString();
        var ex = Assert.Throws<NbtFormatException>(() => new NbtByteArray("b").ReadTag(r));
        Assert.Equal("Negative length given in TAG_Byte_Array", ex.Message);
    }

    [Fact]
    public void IntArray_ReadTag()
    {
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        w.Write(NbtTagType.IntArray);
        w.Write("i");
        w.Write(2);
        w.Write(-1);
        w.Write(5);

        ms.Position = 0;
        var r = new NbtBinaryReader(ms, true);
        r.ReadTagType();
        r.ReadString();
        var t = new NbtIntArray("i");
        Assert.True(t.ReadTag(r));
        Assert.Equal(new[] { -1, 5 }, t.Value);
        Assert.Equal(ms.Length, r.BaseStream.Position);

        using var ms2 = new MemoryStream();
        var w2 = new NbtBinaryWriter(ms2, true);
        w2.Write(NbtTagType.IntArray);
        w2.Write("i");
        w2.Write(-1);
        ms2.Position = 0;
        var r2 = new NbtBinaryReader(ms2, true);
        r2.ReadTagType();
        r2.ReadString();
        var ex = Assert.Throws<NbtFormatException>(() => new NbtIntArray("i").ReadTag(r2));
        Assert.Equal("Negative length given in TAG_Int_Array", ex.Message);
    }

    [Fact]
    public void LongArray_ReadTag()
    {
        using var ms = new MemoryStream();
        var w = new NbtBinaryWriter(ms, true);
        w.Write(NbtTagType.LongArray);
        w.Write("l");
        w.Write(2);
        w.Write(long.MinValue);
        w.Write(7L);

        ms.Position = 0;
        var r = new NbtBinaryReader(ms, true);
        r.ReadTagType();
        r.ReadString();
        var t = new NbtLongArray("l");
        Assert.True(t.ReadTag(r));
        Assert.Equal(new[] { long.MinValue, 7L }, t.Value);
        Assert.Equal(ms.Length, r.BaseStream.Position);

        using var ms2 = new MemoryStream();
        var w2 = new NbtBinaryWriter(ms2, true);
        w2.Write(NbtTagType.LongArray);
        w2.Write("l");
        w2.Write(-1);
        ms2.Position = 0;
        var r2 = new NbtBinaryReader(ms2, true);
        r2.ReadTagType();
        r2.ReadString();
        var ex = Assert.Throws<NbtFormatException>(() => new NbtLongArray("l").ReadTag(r2));
        Assert.Equal("Negative length given in TAG_Long_Array", ex.Message);
    }

    [Fact]
    public void ArrayTags_WriteTag_RoundTrips()
    {
        var cases = new (NbtTag t, NbtTag fresh)[]
        {
            (new NbtByteArray("b", new byte[] { 1, 255 }), new NbtByteArray("b")),
            (new NbtIntArray("i", new[] { -1, 5 }), new NbtIntArray("i")),
            (new NbtLongArray("l", new[] { long.MinValue, 7L }), new NbtLongArray("l")),
        };
        foreach (var (t, fresh) in cases)
        {
            using var ms = new MemoryStream();
            var w = new NbtBinaryWriter(ms, true);
            t.WriteTag(w);
            ms.Position = 0;
            var r = new NbtBinaryReader(ms, true);
            r.ReadTagType();
            r.ReadString();
            Assert.True(fresh.ReadTag(r));
            NbtAssert.Equal(t, fresh);
        }

        // a null name cannot be written
        Assert.Equal("Name is null",
                    Assert.Throws<NbtFormatException>(() => new NbtByteArray().WriteTag(new NbtBinaryWriter(new MemoryStream(), true))).Message);
        Assert.Equal("Name is null",
                    Assert.Throws<NbtFormatException>(() => new NbtIntArray().WriteTag(new NbtBinaryWriter(new MemoryStream(), true))).Message);
        Assert.Equal("Name is null",
                    Assert.Throws<NbtFormatException>(() => new NbtLongArray().WriteTag(new NbtBinaryWriter(new MemoryStream(), true))).Message);
    }

    // ---------- NbtContainerTag ----------

    [Fact]
    public void ContainerTag_IsReadOnly_GetAllTags()
    {
        Assert.False(new NbtCompound("c").IsReadOnly);
        Assert.False(new NbtList("l").IsReadOnly);

        var inner = new NbtCompound(new NbtTag[] { new NbtInt("d", 4) });
        var inner2 = new NbtCompound();
        var list = new NbtList("l", new NbtTag[] { inner, inner2 });
        var root = new NbtCompound("root", new NbtTag[] { new NbtInt("a", 1), list });

        var all = root.GetAllTags().ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(new[] { "a", "l", null, "d", null }, all.Select(x => x.Name).ToArray());
        Assert.Equal(new[] { "root.a", "root.l", "root.l[0]", "root.l[0].d", "root.l[1]" }, all.Select(x => x.Path).ToArray());
    }

    [Fact]
    public void ContainerTag_Mutations_ListAndCompound()
    {
        var l = new NbtList("l");
        l.Add(new NbtInt(1));
        l.AddRange(new NbtTag[] { new NbtInt(2) });
        l.Insert(0, new NbtInt(9));
        Assert.Equal(new[] { 9, 1, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        Assert.True(l.Remove(l[1]));
        Assert.Equal(new[] { 9, 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        l.RemoveAt(0);
        Assert.Equal(new[] { 2 }, l.ToArray<NbtInt>().Select(x => x.Value));
        l.Clear();
        Assert.Equal(0, l.Count);

        var c = new NbtCompound("c");
        c.Add(new NbtInt("a", 1));
        c.AddRange(new NbtTag[] { new NbtInt("b", 2) });
        c.Insert(0, new NbtInt("z", 9));
        Assert.Equal(new[] { "z", "a", "b" }, c.Names);
        Assert.True(c.Remove("a"));
        Assert.Equal(new[] { "z", "b" }, c.Names);
        c.RemoveAt(0);
        Assert.Equal(new[] { "b" }, c.Names);
        c.Clear();
        Assert.Equal(0, c.Count);
    }

    // ---------- OrderedDictionary ----------

    [Fact]
    public void ByteCountingStream_Counts()
    {
        using var ms = new MemoryStream();
        var s = new ByteCountingStream(ms);

        Assert.True(s.CanRead);
        Assert.True(s.CanWrite);
        Assert.True(s.CanSeek);
        Assert.Equal(0, s.BytesRead);
        Assert.Equal(0, s.BytesWritten);

        var data = new byte[] { 1, 2, 3 };
        s.Write(data, 0, 3);
        Assert.Equal(3, s.BytesWritten);
        Assert.Equal(0, s.BytesRead);
        s.WriteByte(4);
        Assert.Equal(4, s.BytesWritten);
        Assert.Equal(4, s.Length);

        s.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, s.Position);
        var buf = new byte[2];
        Assert.Equal(2, s.Read(buf, 0, 2));
        Assert.Equal(2, s.BytesRead);
        Assert.Equal(1, buf[0]);
        Assert.Equal(2, buf[1]);
        Assert.Equal(2, s.Position); // Read advanced the position

        Assert.Equal(3, s.ReadByte()); // byte 2
        Assert.Equal(4, s.ReadByte()); // byte 3
        Assert.Equal(4, s.BytesRead); // 2 (Read) + 2 (ReadByte) = 4
        Assert.Equal(4, s.Position);

        // EOF: ReadByte returns -1 and Read returns 0; neither counts
        Assert.Equal(-1, s.ReadByte());
        Assert.Equal(0, s.Read(buf, 0, 2));
        Assert.Equal(4, s.BytesRead);
        Assert.Equal(4, s.BytesWritten);

        s.Flush();
        s.Position = 0;
        Assert.Equal(0, s.Position);
        s.SetLength(10);
        Assert.Equal(10, s.Length);
        Assert.Equal(10, s.Seek(0, SeekOrigin.End));
        Assert.Equal(10, s.Position);
    }
}