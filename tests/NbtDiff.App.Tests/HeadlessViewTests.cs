using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using fNbt;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.App.Views;
using NbtDiff.Nbt;
using NbtDiff.TestFixtures;

namespace NbtDiff.App.Tests;

/// <summary>
/// The views' code-behind (focus on Loaded, keyboard handlers) and the main window's close/placement
/// logic, driven through a headless Avalonia window with real view models. View models are loaded on
/// the test thread before a view is attached, so no worker thread touches a live control.
/// </summary>
public class HeadlessViewTests
{
    private static Task OnUi(Action action) => HeadlessUi.Session.Dispatch(action, CancellationToken.None);

    /// <summary>Shows <paramref name="vm"/> through the app's DataTemplates, as the main window does.</summary>
    private static Window Host(object vm)
    {
        var window = new Window { Width = 1200, Height = 900, Content = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T ViewOf<T>(Window window) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single();

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    #region FolderCompareView

    private static async Task<(FolderCompareViewModel vm, TempDir left, TempDir right)> ScannedFolder()
    {
        var l = new TempDir(); var r = new TempDir();
        var world = new WorldBuilder(seed: 7).WithRegion(0, 0, chunks: 4).WithLevelDat();
        world.Write(l.Path);
        world.Mutate(m => m.Chunk(0, 0, 1, 0).SetPath("InhabitedTime", 999L)).Write(r.Path);
        var vm = new FolderCompareViewModel(new FakeDialogService(), new ImmediateUiDispatcher()) { LeftPath = l.Path, RightPath = r.Path };
        vm.CompareCommand.Execute(null);
        await vm.ScanCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        return (vm, l, r);
    }

    [Fact]
    public async Task FolderView_Loaded_RestoresSelectionAndFocusesGrid()
    {
        var (vm, l, r) = await ScannedFolder();
        using (l) using (r)
        {
            var file = vm.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca");
            vm.SelectedRow = file;
            await OnUi(() =>
            {
                var window = Host(vm);
                var grid = ViewOf<FolderCompareView>(window).FindControl<DataGrid>("Grid")!;
                Assert.Same(file, grid.SelectedItem);
                Assert.True(grid.IsKeyboardFocusWithin);
                window.Close();
            });
        }
    }

    [Fact]
    public async Task FolderView_Keys_CollapseExpandAndOpen()
    {
        var (vm, l, r) = await ScannedFolder();
        using (l) using (r)
        {
            var region = vm.Tree.Rows.Single(i => i.RelativePath == "region");
            vm.SelectedRow = region;
            CompareRowItem? opened = null;
            vm.NavigationRequested += row => opened = vm.Tree.Rows.Single(i => ReferenceEquals(i.Row, row));
            await OnUi(() =>
            {
                var window = Host(vm);

                Press(window, Key.Left);
                Assert.False(region.IsExpanded);
                Assert.DoesNotContain(vm.Tree.Rows, i => i.RelativePath == "region/r.0.0.mca");

                Press(window, Key.Right);
                Assert.True(region.IsExpanded);

                vm.SelectedRow = vm.Tree.Rows.Single(i => i.RelativePath == "region/r.0.0.mca");
                Dispatcher.UIThread.RunJobs();
                Press(window, Key.Enter);
                Assert.Same(vm.SelectedRow, opened);
                window.Close();
            });
        }
    }

    #endregion

    #region FileCompareView

    [Fact]
    public async Task FileView_Keys_CollapseExpandAndToggle()
    {
        var left = new NbtCompound("") { new NbtCompound("a") { new NbtInt("x", 1) } };
        var right = new NbtCompound("") { new NbtCompound("a") { new NbtInt("x", 2) } };
        var vm = new FileCompareViewModel(new TagPairSource("pair", left, right), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        var a = vm.Tree.Rows.Single(i => i.Path == "a");
        Assert.True(a.IsExpanded);
        vm.SelectedItem = a;

        await OnUi(() =>
        {
            var window = Host(vm);
            var grid = ViewOf<FileCompareView>(window).FindControl<DataGrid>("Grid")!;
            Assert.True(grid.IsKeyboardFocusWithin);

            Press(window, Key.Left);
            Assert.False(a.IsExpanded);
            Press(window, Key.Right);
            Assert.True(a.IsExpanded);
            Press(window, Key.Enter);
            Assert.False(a.IsExpanded);
            window.Close();
        });
    }

    [Fact]
    public async Task FileView_AltRight_ReachesCopyBinding()
    {
        // The grid's own arrow-key handler must leave modified keys to the view's KeyBindings.
        var left = new NbtCompound("") { new NbtInt("k", 1) };
        var right = new NbtCompound("") { new NbtInt("k", 2) };
        var vm = new FileCompareViewModel(new TagPairSource("pair", left, right), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        vm.SelectedItem = vm.Tree.Rows.Single(i => i.Path == "k");

        await OnUi(() =>
        {
            var window = Host(vm);
            Press(window, Key.Right, RawInputModifiers.Alt);
            window.Close();
        });
        Assert.True(vm.HasUnsavedEdits);
        Assert.Equal(1, right.Get<NbtInt>("k")!.Value);
    }

    #endregion

    #region RegionCompareView

    [Fact]
    public async Task RegionView_ArrowKeysMoveSelection_EnterOpensChunk()
    {
        using var l = new TempDir();
        using var r = new TempDir();
        var world = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40);
        world.Write(l.Path);
        world.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(r.Path);
        using var vm = new RegionCompareViewModel(Path.Combine(l.Path, "region", "r.0.0.mca"),
            Path.Combine(r.Path, "region", "r.0.0.mca"), null, null, new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Same(vm.Grid[3, 1], vm.Grid.Selected);
        ViewModelBase? opened = null;
        vm.NavigationRequested += v => opened = v;

        await OnUi(() =>
        {
            var window = Host(vm);
            Press(window, Key.Right);
            Assert.Same(vm.Grid[4, 1], vm.Grid.Selected);
            Press(window, Key.Down);
            Assert.Same(vm.Grid[4, 2], vm.Grid.Selected);
            Press(window, Key.Left);
            Press(window, Key.Up);
            Assert.Same(vm.Grid[3, 1], vm.Grid.Selected);

            Press(window, Key.Enter);
            Assert.IsType<FileCompareViewModel>(opened);
            window.Close();
        });
        (opened as IDisposable)?.Dispose();
    }

    #endregion

    #region TextCompareView

    [Fact]
    public async Task TextView_LoadsAndFocusesGrid()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.snbt"), "{a:1,b:2}\n");
        File.WriteAllText(d.File("b.snbt"), "{a:1,b:3}\n");
        var vm = new TextCompareViewModel(d.File("a.snbt"), d.File("b.snbt"), new ImmediateUiDispatcher());
        await vm.Load().WaitAsync(TimeSpan.FromSeconds(30));

        await OnUi(() =>
        {
            var window = Host(vm);
            var grid = ViewOf<TextCompareView>(window).FindControl<DataGrid>("Grid")!;
            Assert.True(grid.IsKeyboardFocusWithin);
            Assert.NotEmpty(vm.Rows);
            window.Close();
        });
    }

    #endregion

    #region MainWindow

    private static MainWindowViewModel Shell(FakeDialogService dialogs, ISettingsService? settings = null) =>
        new(dialogs, new ImmediateUiDispatcher(), settings: settings);

    [Fact]
    public async Task MainWindow_Close_SavesPlacement()
    {
        var settings = new SettingsService(null);
        var shell = Shell(new FakeDialogService(), settings);
        await OnUi(() =>
        {
            var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Assert.False(window.IsVisible);
        });
        var saved = settings.Current.Window!;
        Assert.Equal(900, saved.Width);
        Assert.Equal(600, saved.Height);
        Assert.False(saved.IsMaximized);
    }

    [Fact]
    public async Task MainWindow_ClosingMaximized_KeepsPreviousNormalSize()
    {
        var settings = new SettingsService(null);
        settings.Current.Window = new WindowPlacement(10, 20, 700, 500, true);
        var shell = Shell(new FakeDialogService(), settings);
        await OnUi(() =>
        {
            var window = new MainWindow { DataContext = shell };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            window.Close();
        });
        Assert.Equal(new WindowPlacement(10, 20, 700, 500, true), settings.Current.Window);
    }

    [Fact]
    public async Task MainWindow_CloseWithUnsavedCopies_AsksFirst()
    {
        using var d = new TempDir();
        NbtFixtures.WriteFile(d.File("a.dat"), new NbtCompound("") { new NbtInt("k", 1) }, NbtFormat.JavaNbt);
        NbtFixtures.WriteFile(d.File("b.dat"), new NbtCompound("") { new NbtInt("k", 2) }, NbtFormat.JavaNbt);
        var dialogs = new FakeDialogService();
        var shell = Shell(dialogs);
        shell.Start([]);
        shell.OpenPair(d.File("a.dat"), d.File("b.dat"));
        var file = Assert.IsType<FileCompareViewModel>(shell.Current);
        await file.LoadCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        file.SelectedItem = file.Tree.Rows.Single(i => i.Path == "k");
        await file.CopyToRightCommand.ExecuteAsync(null);
        Assert.True(shell.HasDiscardableEdits);

        await OnUi(() =>
        {
            var window = new MainWindow { DataContext = shell };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            dialogs.NextConfirm = false;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);   // discard refused: the close was cancelled

            dialogs.NextConfirm = true;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible);
        });
        Assert.Equal(2, dialogs.Requests.Count(q => q == "confirm:Unsaved copies"));
    }

    #endregion
}
