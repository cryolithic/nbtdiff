using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using fNbt;
using NbtDiff.App.Converters;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;
using NbtDiff.Core.Diff;

namespace NbtDiff.App.Tests;

public class StatePaletteTests
{
    // ── #7: containers roll up the changes below them ───────────────────────────────────────

    private static DiffNodeItem Tree()
    {
        var left = new NbtCompound("") { new NbtCompound("a") { new NbtInt("x", 1) }, new NbtCompound("same") { new NbtInt("y", 1) } };
        var right = (NbtCompound)left.Clone();
        ((NbtInt)((NbtCompound)right["a"])["x"]).Value = 2;
        return DiffNodeItem.Build(NbtDiffer.Diff(left, right));
    }

    [Fact]
    public void ContainerWithAChangeBelow_IsARollup_TintedOnlyWhileCollapsed()
    {
        var a = Tree().Descendants().Single(i => i.Path == "a");
        Assert.True(a.IsRollup);
        Assert.Equal(StateKind.Different, a.State);
        Assert.Equal("●", a.StatusGlyph);
        Assert.Equal("a: 1 change below", a.ToolTipText);

        var changed = new List<string>();
        a.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        a.IsExpanded = false;
        Assert.Equal(new RowTint(StateKind.Different, Rollup: true), a.Tint);
        a.IsExpanded = true;
        Assert.Equal(RowTint.None, a.Tint);
        Assert.Contains(nameof(DiffNodeItem.Tint), changed);
    }

    [Fact]
    public void ChangedLeaf_IsTintedAlways_UnchangedSubtree_IsPlainSame()
    {
        var items = Tree().Descendants().ToDictionary(i => i.Path);
        Assert.Equal(new RowTint(StateKind.Different), items["a/x"].Tint);
        Assert.Equal("≠", items["a/x"].StatusGlyph);
        Assert.False(items["same"].IsRollup);
        Assert.Equal("=", items["same"].StatusGlyph);
        Assert.Equal(RowTint.None, items["same"].Tint);
        Assert.Equal(StateKind.Same, items["same"].State);
    }

    // ── #9: every state resolves to a real brush in both theme variants ─────────────────────

    [Fact]
    public void EveryState_ResolvesInDarkAndLight()
    {
        HeadlessUi.Run(() =>
        {
            var app = Application.Current!;
            var original = app.RequestedThemeVariant;
            try
            {
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = variant;
                    foreach (var kind in Enum.GetValues<StateKind>().Where(k => k != StateKind.None))
                    {
                        var fill = StateBrushConverter.Instance.Convert(kind, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
                        Assert.True(fill is ISolidColorBrush { Color.A: > 0 }, $"{variant} State.{kind} missing");
                    }
                    foreach (var kind in new[] { StateKind.Different, StateKind.ProbablyDifferent, StateKind.LeftOnly, StateKind.RightOnly, StateKind.Moved, StateKind.Error })
                        foreach (bool rollup in new[] { false, true })
                        {
                            var tint = StateBrushConverter.Instance.Convert(new RowTint(kind, rollup), typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture);
                            Assert.True(tint is ISolidColorBrush { Color.A: > 0 and < 0x40 }, $"{variant} {kind} tint (rollup {rollup})");
                        }
                    Assert.Same(Brushes.Transparent, StateBrushConverter.Instance.Convert(RowTint.None, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture));
                }
                // Dark and light really differ.
                app.RequestedThemeVariant = ThemeVariant.Dark;
                var dark = ((ISolidColorBrush)StateBrushConverter.Instance.Convert(StateKind.Different, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!).Color;
                app.RequestedThemeVariant = ThemeVariant.Light;
                var light = ((ISolidColorBrush)StateBrushConverter.Instance.Convert(StateKind.Different, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!).Color;
                Assert.NotEqual(dark, light);
                return 0;
            }
            finally
            {
                app.RequestedThemeVariant = original;
            }
        });
    }
}
