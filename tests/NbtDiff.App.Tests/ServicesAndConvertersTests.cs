using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NbtDiff.App.Converters;
using NbtDiff.App.Services;
using NbtDiff.Core;
using NbtDiff.Core.Diff;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

public class ServicesAndConvertersTests
{
    // The converters' static brushes are thread-affine AvaloniaObjects: create and read them on the
    // headless UI thread, or the view tests that later render them fail with a cross-thread error.
    private static object? C(IValueConverter c, object? value, object? parameter = null) =>
        HeadlessUi.Run(() => c.Convert(value, typeof(object), parameter, CultureInfo.InvariantCulture));

    private static void AssertColor(object? result, Color expected) =>
        Assert.Equal(expected, HeadlessUi.Run(() => ((SolidColorBrush)result!).Color));

    // ── SettingsStore ──────────────────────────────────────────────────────────

    [Fact]
    public void SettingsStore_DefaultPath_IsConfigDirNbtdiffSettings()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SettingsStore.DefaultPath);
        Assert.EndsWith(Path.Combine("nbtdiff", "settings.json"), SettingsStore.DefaultPath);
    }

    [Fact]
    public void SettingsStore_Load_MissingFile_ReturnsAllDefaults()
    {
        using var d = new TempDir();
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.True(s.DeepVerify);
        Assert.False(s.CompoundOrderMatters);
        Assert.True(s.UseKeyedAligner);
        Assert.Equal(["session.lock"], s.ExcludeGlobs);
        Assert.Equal(TagIgnoreSet.DefaultPaths, s.IgnoredTags);
        Assert.Empty(s.RecentPairs);
        Assert.Null(s.Window);
        Assert.True(s.IgnoreSet.Root!.Ignores("LastUpdate"));
        Assert.False(s.IgnoreSet.Root.Ignores("InhabitedTime"));
    }

    [Fact]
    public void SettingsStore_Load_WrongTypedJson_FallsBackToDefaults()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("settings.json"), """{ "DeepVerify": "yes", "ExcludeGlobs": 5 }""");
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.True(s.DeepVerify);
        Assert.Equal(["session.lock"], s.ExcludeGlobs);
        Assert.Empty(s.RecentPairs);
    }

    [Fact]
    public void SettingsStore_Load_ToleratesCommentsAndTrailingCommas()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("settings.json"), """
        // user-edited
        { "DeepVerify": false, "ExcludeGlobs": ["x",], "IgnoredTags": ["InhabitedTime",], }
        """);
        var s = new SettingsStore(d.File("settings.json")).Load();
        Assert.False(s.DeepVerify);
        Assert.Equal(["x"], s.ExcludeGlobs);
        Assert.Equal(["InhabitedTime"], s.IgnoredTags);
    }

    [Fact]
    public void SettingsStore_TrySave_ValidPath_CreatesDirs_RoundTrips_NoTmpLeft()
    {
        using var d = new TempDir();
        var path = d.File("deep/nested/settings.json");
        var store = new SettingsStore(path);
        var s = new AppSettings { DeepVerify = false, CompoundOrderMatters = true, UseKeyedAligner = false, ExcludeGlobs = ["a", "b"], IgnoredTags = ["InhabitedTime"], Window = new WindowPlacement(1, 2, 300, 200, true) };
        s.AddRecent("l", "r");

        Assert.True(store.TrySave(s));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var back = store.Load();
        Assert.False(back.DeepVerify);
        Assert.True(back.CompoundOrderMatters);
        Assert.False(back.UseKeyedAligner);
        Assert.Equal(["a", "b"], back.ExcludeGlobs);
        Assert.Equal(["InhabitedTime"], back.IgnoredTags);
        Assert.Equal(s.Window, back.Window);
        Assert.Equal(s.RecentPairs, back.RecentPairs);

        // A null Window is omitted from the file rather than written as null.
        s.Window = null;
        Assert.True(store.TrySave(s));
        Assert.DoesNotContain("\"Window\"", File.ReadAllText(path));
        Assert.Null(store.Load().Window);
    }

    [Fact]
    public void SettingsStore_TrySave_ParentIsAFile_ReturnsFalse_NoFileWritten()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("blocker"), "x");
        var store = new SettingsStore(d.File("blocker/settings.json"));

        Assert.False(store.TrySave(new AppSettings()));
        Assert.False(File.Exists(store.Path));
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    // ── SettingsService ────────────────────────────────────────────────────────

    [Fact]
    public void SettingsService_RealStore_LoadsFile_SavePersistsChanges()
    {
        using var d = new TempDir();
        var path = d.File("settings.json");
        var seed = new AppSettings { DeepVerify = false, ExcludeGlobs = ["a"] };
        seed.AddRecent("l", "r");
        Assert.True(new SettingsStore(path).TrySave(seed));

        var svc = new SettingsService(new SettingsStore(path));
        Assert.False(svc.Current.DeepVerify);
        Assert.Equal(["a"], svc.Current.ExcludeGlobs);
        Assert.Equal([new RecentPair("l", "r")], svc.Current.RecentPairs);

        svc.Current.DeepVerify = true;
        svc.Current.AddRecent("l2", "r2");
        svc.Save();

        var reloaded = new SettingsStore(path).Load();
        Assert.True(reloaded.DeepVerify);
        Assert.Equal([new RecentPair("l2", "r2"), new RecentPair("l", "r")], reloaded.RecentPairs);
    }

    [Fact]
    public void SettingsService_NullStore_DefaultsInMemory_SaveIsNoOp()
    {
        var svc = new SettingsService(null);
        Assert.True(svc.Current.DeepVerify);
        Assert.Empty(svc.Current.RecentPairs);
        Assert.Null(svc.Current.Window);

        svc.Current.UseKeyedAligner = false;
        svc.Current.AddRecent("a", "b");
        svc.Save(); // must not throw and must not write anywhere

        Assert.False(svc.Current.UseKeyedAligner);
        Assert.Equal([new RecentPair("a", "b")], svc.Current.RecentPairs);
        Assert.Null(svc.Current.Window);
    }

    // ── AppSettings ────────────────────────────────────────────────────────────

    [Fact]
    public void AppSettings_AddRecent_DedupesExactPair_KeepsSameLeftDifferentRight()
    {
        var s = new AppSettings();
        s.AddRecent("a", "b");
        s.AddRecent("a", "b");
        Assert.Equal([new RecentPair("a", "b")], s.RecentPairs);

        s.AddRecent("a", "c");
        Assert.Equal([new RecentPair("a", "c"), new RecentPair("a", "b")], s.RecentPairs);
    }

    [Fact]
    public void AppSettings_Sanitize_RepairsNullsDropsJunk_CapsRecents()
    {
        var s = new AppSettings
        {
            RecentPairs = [null!, new RecentPair("", "x"), new RecentPair("  ", "y"), new RecentPair("a", "b"), new RecentPair("c", "d"), new RecentPair("e", "f"), new RecentPair("g", "h"), new RecentPair("i", "j"), new RecentPair("k", "l"), new RecentPair("m", "n"), new RecentPair("o", "p"), new RecentPair("q", "r"), new RecentPair("s", "t")],
            ExcludeGlobs = ["  ", "x", null!],
            IgnoredTags = null!,
            Window = new WindowPlacement(0, 0, 100, 100, false),
        };

        s.Sanitize();
        Assert.Equal([new RecentPair("a", "b"), new RecentPair("c", "d"), new RecentPair("e", "f"), new RecentPair("g", "h"), new RecentPair("i", "j"), new RecentPair("k", "l"), new RecentPair("m", "n"), new RecentPair("o", "p"), new RecentPair("q", "r"), new RecentPair("s", "t")], s.RecentPairs);
        Assert.Equal(["x"], s.ExcludeGlobs);
        Assert.Equal(TagIgnoreSet.DefaultPaths, s.IgnoredTags);
        Assert.Null(s.Window); // 100x100 is below the 200x150 floor

        var capped = new AppSettings();
        for (int i = 0; i < 12; i++) capped.AddRecent($"l{i}", $"r{i}");
        capped.Sanitize();
        Assert.Equal(AppSettings.MaxRecent, capped.RecentPairs.Count);
        Assert.Equal("l11", capped.RecentPairs[0].Left);
        Assert.Equal("l2", capped.RecentPairs[9].Left);
    }

    [Fact]
    public void AppSettings_Sanitize_NormalizesIgnoredTags_TrimsDedupesDropsBlanks()
    {
        var s = new AppSettings { IgnoredTags = ["  LastUpdate  ", "LastUpdate", "", "Level/LastUpdate"] };
        s.Sanitize();
        Assert.Equal(["LastUpdate", "Level/LastUpdate"], s.IgnoredTags);
        Assert.True(s.IgnoreSet.Root!.Ignores("LastUpdate"));
        Assert.True(s.IgnoreSet.Root.Child("Level")!.Ignores("LastUpdate"));

        var empty = new AppSettings { IgnoredTags = ["   "] };
        empty.Sanitize();
        Assert.Empty(empty.IgnoredTags); // blank-only stays empty, does NOT revert to defaults
        Assert.True(empty.IgnoreSet.IsEmpty);
        Assert.Null(empty.IgnoreSet.Root);
    }

    [Fact]
    public void AppSettings_Sanitize_WindowBoundaries()
    {
        static WindowPlacement? Fix(int x, int y, double w, double h, bool max)
        {
            var s = new AppSettings { Window = new WindowPlacement(x, y, w, h, max) };
            s.Sanitize();
            return s.Window;
        }

        Assert.Null(Fix(0, 0, double.NaN, 500, false));
        Assert.Null(Fix(0, 0, 199.9, 500, false));
        Assert.Null(Fix(0, 0, 800, 149.9, false));
        Assert.Equal(new WindowPlacement(5, 6, 200, 150, true), Fix(5, 6, 200, 150, true));
    }

    [Fact]
    public void AppSettings_ToCompareOptions_MapsEveryField()
    {
        var s = new AppSettings { DeepVerify = false, CompoundOrderMatters = true, ExcludeGlobs = ["a", "b"], UseKeyedAligner = false, IgnoredTags = ["X"] };
        var o = s.ToCompareOptions();
        Assert.False(o.DeepVerify);
        Assert.True(o.CompoundOrderMatters);
        Assert.Equal(["a", "b"], o.ExcludeGlobs);
        Assert.Equal(0, o.MaxParallelism);
        Assert.Equal(["X"], o.IgnoredTags);
        Assert.False(o.KeyedLists);
        Assert.Null(o.KeyedListAligner);

        var keyed = new AppSettings().ToCompareOptions(); // defaults: DeepVerify on, keyed on
        Assert.True(keyed.DeepVerify);
        Assert.True(keyed.KeyedLists);
        Assert.Same(KeyedAligner.Default, keyed.KeyedListAligner);
        Assert.Equal(TagIgnoreSet.DefaultPaths, keyed.EffectiveIgnoredTags.Paths);
    }

    [Fact]
    public void AppSettings_ToDiffOptions_KeyedAndPositionalAligners()
    {
        var o = new AppSettings().ToDiffOptions();
        Assert.False(o.CompoundOrderMatters);
        Assert.Same(KeyedAligner.Default, o.ListAligner);
        Assert.Same(KeyedAligner.Default, o.Aligner);
        Assert.Same(KeyedAligner.Default, o.KeyedLists);
        Assert.Equal(TagIgnoreSet.DefaultPaths, o.Ignored.Paths);

        var p = new AppSettings { UseKeyedAligner = false, CompoundOrderMatters = true, IgnoredTags = ["InhabitedTime"] }.ToDiffOptions();
        Assert.True(p.CompoundOrderMatters);
        Assert.Null(p.ListAligner);
        Assert.Same(IndexAligner.Instance, p.Aligner);
        Assert.Null(p.KeyedLists);
        Assert.True(p.Ignored.Root!.Ignores("InhabitedTime"));
        Assert.False(p.Ignored.Root.Ignores("LastUpdate"));
    }

    [Fact]
    public void AppSettings_IgnoreSet_DefaultCustomAndEmpty()
    {
        var s = new AppSettings();
        Assert.Equal(["LastUpdate", "Level/LastUpdate"], s.IgnoreSet.Paths);
        Assert.True(s.IgnoreSet.Root!.Ignores("LastUpdate"));
        Assert.False(s.IgnoreSet.Root.Ignores("InhabitedTime"));

        var c = new AppSettings { IgnoredTags = ["InhabitedTime", "  Data  "] };
        Assert.True(c.IgnoreSet.Root!.Ignores("InhabitedTime"));
        Assert.True(c.IgnoreSet.Root.Ignores("Data")); // Parse trims segments

        var e = new AppSettings { IgnoredTags = [] };
        Assert.True(e.IgnoreSet.IsEmpty);
        Assert.Null(e.IgnoreSet.Root);
    }

    [Fact]
    public void RecentPair_Display_JoinsSidesWithArrows_KeepsEmptySides()
    {
        Assert.Equal(@"C:\a  ↔  C:\b", new RecentPair(@"C:\a", @"C:\b").Display);
        Assert.Equal("  ↔  r", new RecentPair("", "r").Display);
        Assert.Equal("l  ↔  ", new RecentPair("l", "").Display);
        Assert.Equal("  ↔  ", new RecentPair("", "").Display);
    }

    // ── Converters ─────────────────────────────────────────────────────────────

    [Fact]
    public void StatusBackgroundConverter_EveryRowStatusAndDefault()
    {
        var c = new StatusBackgroundConverter();
        AssertColor(C(c, RowStatus.Different), Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
        AssertColor(C(c, RowStatus.ProbablyDifferent), Color.FromArgb(0x22, 0xE0, 0x40, 0x40));
        AssertColor(C(c, RowStatus.LeftOnly), Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
        AssertColor(C(c, RowStatus.RightOnly), Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));
        AssertColor(C(c, RowStatus.Error), Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        Assert.Same(Brushes.Transparent, C(c, RowStatus.Same));
        Assert.Same(Brushes.Transparent, C(c, RowStatus.Pending));
        Assert.Same(Brushes.Transparent, C(c, null));
        Assert.Same(Brushes.Transparent, C(c, "not a status"));
    }

    [Fact]
    public void StatusDecorationConverter_OnlyErrorGetsStrikethrough()
    {
        var c = new StatusDecorationConverter();
        Assert.Same(TextDecorations.Strikethrough, C(c, RowStatus.Error));
        Assert.Null(C(c, RowStatus.Different));
        Assert.Null(C(c, RowStatus.ProbablyDifferent));
        Assert.Null(C(c, RowStatus.Same));
        Assert.Null(C(c, RowStatus.Pending));
        Assert.Null(C(c, RowStatus.LeftOnly));
        Assert.Null(C(c, RowStatus.RightOnly));
        Assert.Null(C(c, null));
    }

    [Fact]
    public void StatusOpacityConverter_EveryRowStatusAndDefault()
    {
        var c = new StatusOpacityConverter();
        Assert.Equal(0.55, (double)C(c, RowStatus.ProbablyDifferent)!);
        Assert.Equal(0.4, (double)C(c, RowStatus.Pending)!);
        Assert.Equal(1.0, (double)C(c, RowStatus.Same)!);
        Assert.Equal(1.0, (double)C(c, RowStatus.Different)!);
        Assert.Equal(1.0, (double)C(c, RowStatus.LeftOnly)!);
        Assert.Equal(1.0, (double)C(c, RowStatus.RightOnly)!);
        Assert.Equal(1.0, (double)C(c, RowStatus.Error)!);
        Assert.Equal(1.0, (double)C(c, null)!);
    }

    [Fact]
    public void DepthToMarginConverter_IndentsByDepthTimesStep()
    {
        var c = new DepthToMarginConverter();
        Assert.Equal(16, c.Step);
        Assert.Equal(new Thickness(0, 0, 0, 0), (Thickness)C(c, 0)!);
        Assert.Equal(new Thickness(16, 0, 0, 0), (Thickness)C(c, 1)!);
        Assert.Equal(new Thickness(48, 0, 0, 0), (Thickness)C(c, 3)!);
        Assert.Equal(new Thickness(-32, 0, 0, 0), (Thickness)C(c, -2)!);
        Assert.Equal(new Thickness(0, 0, 0, 0), (Thickness)C(c, null)!);
        Assert.Equal(new Thickness(0, 0, 0, 0), (Thickness)C(c, "x")!);
        Assert.Equal(new Thickness(0, 0, 0, 0), (Thickness)C(c, 2.5)!); // double is not int

        c.Step = 8;
        Assert.Equal(new Thickness(24, 0, 0, 0), (Thickness)C(c, 3)!);
    }

    [Fact]
    public void EnumEqualsConverter_MatchesByName_RequiresBothSides()
    {
        var c = new EnumEqualsConverter();
        Assert.True((bool)C(c, RowStatus.Same, "Same")!);
        Assert.True((bool)C(c, RowStatus.Same, RowStatus.Same)!);
        Assert.False((bool)C(c, RowStatus.Same, "Different")!);
        Assert.False((bool)C(c, RowStatus.Same, "same")!); // ordinal, not case-insensitive
        Assert.False((bool)C(c, null, "Same")!);
        Assert.False((bool)C(c, RowStatus.Same, null)!);
        Assert.False((bool)C(c, null, null)!);
    }

    [Fact]
    public void DiffKindBackgroundConverter_EveryDiffKindAndDefault()
    {
        var c = new DiffKindBackgroundConverter();
        AssertColor(C(c, DiffKind.ValueChanged), Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
        AssertColor(C(c, DiffKind.TypeChanged), Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
        AssertColor(C(c, DiffKind.Removed), Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
        AssertColor(C(c, DiffKind.Added), Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));
        AssertColor(C(c, DiffKind.Moved), Color.FromArgb(0x38, 0xE0, 0xB0, 0x30));
        AssertColor(C(c, DiffKind.Renamed), Color.FromArgb(0x38, 0xE0, 0xB0, 0x30));
        Assert.Same(Brushes.Transparent, C(c, DiffKind.Unchanged));
        Assert.Same(Brushes.Transparent, C(c, null));
        Assert.Same(Brushes.Transparent, C(c, "x"));
    }

    [Fact]
    public void ChunkStatusBrushConverter_EveryChunkDiffStatusAndDefault()
    {
        var c = new ChunkStatusBrushConverter();
        AssertColor(C(c, ChunkDiffStatus.Same), Color.FromArgb(0x60, 0x60, 0xB0, 0x60));
        AssertColor(C(c, ChunkDiffStatus.Different), Color.FromArgb(0xE0, 0xE0, 0x40, 0x40));
        AssertColor(C(c, ChunkDiffStatus.LeftOnly), Color.FromArgb(0xD0, 0x30, 0x90, 0xF0));
        AssertColor(C(c, ChunkDiffStatus.RightOnly), Color.FromArgb(0xD0, 0xA0, 0x50, 0xE0));
        AssertColor(C(c, ChunkDiffStatus.Error), Color.FromArgb(0xE0, 0xF0, 0xA0, 0x20));
        AssertColor(C(c, null), Color.FromArgb(0x10, 0x80, 0x80, 0x80));
        AssertColor(C(c, "x"), Color.FromArgb(0x10, 0x80, 0x80, 0x80));
    }

    [Fact]
    public void LineDiffKindBackgroundConverter_EveryLineDiffKindAndDefault()
    {
        var c = new LineDiffKindBackgroundConverter();
        AssertColor(C(c, LineDiffKind.Changed), Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
        AssertColor(C(c, LineDiffKind.Removed), Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
        AssertColor(C(c, LineDiffKind.Added), Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));
        Assert.Same(Brushes.Transparent, C(c, LineDiffKind.Unchanged));
        Assert.Same(Brushes.Transparent, C(c, null));
        Assert.Same(Brushes.Transparent, C(c, "x"));
    }

    [Fact]
    public void DifferentRowBackgroundConverter_TrueOnlyGetsRed()
    {
        var c = new DifferentRowBackgroundConverter();
        AssertColor(C(c, true), Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
        Assert.Same(Brushes.Transparent, C(c, false));
        Assert.Same(Brushes.Transparent, C(c, null));
        Assert.Same(Brushes.Transparent, C(c, "true"));
    }

    [Fact]
    public void ConvertBack_ThrowsForEveryConverter()
    {
        IValueConverter[] converters =
        [
            new StatusBackgroundConverter(),
            new StatusDecorationConverter(),
            new StatusOpacityConverter(),
            new DepthToMarginConverter(),
            new EnumEqualsConverter(),
            new DiffKindBackgroundConverter(),
            new ChunkStatusBrushConverter(),
            new LineDiffKindBackgroundConverter(),
            new DifferentRowBackgroundConverter(),
        ];
        foreach (var c in converters)
            Assert.Throws<NotSupportedException>(() => c.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture));
    }
}
