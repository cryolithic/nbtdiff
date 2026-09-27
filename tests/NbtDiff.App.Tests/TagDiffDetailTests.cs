using fNbt;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Core.Diff;

namespace NbtDiff.App.Tests;

/// <summary>#12: value deltas, count deltas, inline change text, the detail pane and the status bar options.</summary>
public class TagDiffDetailTests
{
    private static Dictionary<string, DiffNodeItem> Items(NbtCompound left, NbtCompound right) =>
        DiffNodeItem.Build(NbtDiffer.Diff(left, right)).Descendants().ToDictionary(i => i.Path);

    [Fact]
    public void NumericDeltas_ForEveryNumberType_AndNoneForOtherChanges()
    {
        var left = new NbtCompound("")
        {
            new NbtByte("b", unchecked((byte)(sbyte)-2)), new NbtShort("s", 10), new NbtInt("i", 7160), new NbtLong("l", 5),
            new NbtFloat("f", 1.5f), new NbtDouble("d", 2.25), new NbtString("str", "a"), new NbtInt("t", 1),
        };
        var right = new NbtCompound("")
        {
            new NbtByte("b", 3), new NbtShort("s", 4), new NbtInt("i", 7560), new NbtLong("l", 5_000_000_000),
            new NbtFloat("f", 1.0f), new NbtDouble("d", 2.75), new NbtString("str", "b"), new NbtLong("t", 1),
        };
        var items = Items(left, right);
        Assert.Equal("+5", items["b"].DeltaText);
        Assert.Equal("−6", items["s"].DeltaText);
        Assert.Equal("+400", items["i"].DeltaText);
        Assert.Equal("+5.6%", items["i"].PercentText);
        Assert.Equal("+4999999995", items["l"].DeltaText);
        Assert.Equal("−0.5", items["f"].DeltaText);
        Assert.Equal("+0.5", items["d"].DeltaText);
        Assert.Null(items["str"].DeltaText);
        Assert.Null(items["t"].DeltaText);                 // type change: no delta
        Assert.Equal("int · changed", items["i"].KindText);
    }

    [Fact]
    public void Containers_CountDelta_AndInlineChangeText()
    {
        var left = new NbtCompound("")
        {
            new NbtList("block_entities", NbtTagType.Compound) { new NbtCompound { new NbtString("id", "a") } },
            new NbtCompound("mixed") { new NbtInt("x", 1), new NbtInt("gone", 1) },
        };
        var right = new NbtCompound("")
        {
            new NbtList("block_entities", NbtTagType.Compound)
            {
                new NbtCompound { new NbtString("id", "a") }, new NbtCompound { new NbtString("id", "b") }, new NbtCompound { new NbtString("id", "c") },
            },
            new NbtCompound("mixed") { new NbtInt("x", 2) },
        };
        var items = Items(left, right);
        Assert.Equal("+2", items["block_entities"].CountDeltaText);
        Assert.Equal("2 added", items["block_entities"].InlineChangesText);
        Assert.Equal("−1", items["mixed"].CountDeltaText);
        Assert.Equal("2 changes", items["mixed"].InlineChangesText);
        Assert.Null(items["mixed/x"].InlineChangesText);    // leaves never carry inline text
        Assert.Equal("compound · changes below", items["mixed"].KindText);
    }

    [Theory]
    [InlineData("Entities/[0]/NeoForgeData/naturesaura:time_alive", "Entities[0].NeoForgeData.\"naturesaura:time_alive\"")]
    [InlineData("a/b/c", "a.b.c")]
    [InlineData("list/[3]/[1]", "list[3][1]")]
    [InlineData("", "")]
    public void NbtPath_QuotesKeysThatNeedIt(string path, string expected) =>
        Assert.Equal(expected, DiffNodeItem.ToNbtPath(path));

    [Fact]
    public async Task DetailPane_AndStatusOptions()
    {
        var left = new NbtCompound("") { new NbtInt("k", 1), new NbtIntArray("arr", [1, 2, 3]) };
        var right = new NbtCompound("") { new NbtInt("k", 2), new NbtIntArray("arr", [1, 9, 3]) };
        var vm = new FileCompareViewModel(new TagPairSource("pair", left, right), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(vm.HasValueDetail);
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "k");
        Assert.True(vm.HasValueDetail);
        Assert.Equal("1", vm.SelectedItem.LeftFullText);
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "arr");
        Assert.False(vm.HasValueDetail);                    // arrays show their element table instead
        Assert.True(vm.HasArrayDetail);

        Assert.Equal("Key order ignored · lists matched by UUID / id", vm.OptionsText);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.CompoundOrderMatters = true;
        vm.UseKeyedAligner = false;
        Assert.Equal("Key order matters · lists matched by position", vm.OptionsText);
        Assert.Equal(2, changed.Count(n => n == nameof(FileCompareViewModel.OptionsText)));
    }
}
