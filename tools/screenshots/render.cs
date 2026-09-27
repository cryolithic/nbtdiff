#:project ../../src/NbtDiff.App/NbtDiff.App.csproj
#:package Avalonia.Headless@12.1.2
#:package Avalonia.Skia@12.1.2
// Renders the app's views headless with real fixture data (tests/data/real) to PNGs, for checking UI
// work against docs/design/mockups and for the README screenshots. From the repo root:
//
//   dotnet run tools/screenshots/render.cs -- <out-dir> [dark|light]
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using NbtDiff.App;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;
using NbtDiff.Core;

string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
var variant = args.Length > 1 && args[1] == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
string fixtures = Path.GetFullPath(Path.Combine("tests", "data", "real", "neoforge"));
Directory.CreateDirectory(outDir);

// Work on copies with neutral names, addressed by relative paths, so no machine path shows in a shot.
string work = Path.Combine(outDir, "worlds");
if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
CopyTree(Path.Combine(fixtures, "initial"), Path.Combine(work, "Modpack World"));
CopyTree(Path.Combine(fixtures, "expected"), Path.Combine(work, "Modpack World (later)"));
Directory.SetCurrentDirectory(work);
string left = "Modpack World", right = "Modpack World (later)";

static void CopyTree(string from, string to)
{
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();
Application.Current!.RequestedThemeVariant = variant;

var ui = new InlineDispatcher();
var shell = new MainWindowViewModel(new NoDialogs(), ui);
var window = new MainWindow { DataContext = shell, Width = 1600, Height = 900 };
window.Show();

// Everything runs on this (the UI) thread: pump the dispatcher until background work completes.
void Settle(Task? work = null)
{
    var until = DateTime.UtcNow.AddSeconds(60);
    while (work is { IsCompleted: false } && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
    if (work is { IsCompleted: false }) throw new TimeoutException("view did not finish loading");
    for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
}

void Snap(string name)
{
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame()!;
#pragma warning disable CS0618 // PNG is the default encoding; the options overload adds nothing here
    using (var file = File.Create(Path.Combine(outDir, name + ".png"))) frame.Save(file);
#pragma warning restore CS0618
    Console.WriteLine($"{name}.png");
}

// Folder compare of the NeoForge fixture pair.
shell.Start([left, right]);
var folder = (FolderCompareViewModel)shell.Current!;
Settle(folder.ScanCompletion);
Snap("folder");

// Region grid (opened from the folder view, so the breadcrumb is real), then the first changed chunk.
folder.SelectedRow = folder.Tree.Rows.Single(i => i.RelativePath == "entities/r.0.0.mca");
folder.OpenSelectedCommand.Execute(null);
var region = (RegionCompareViewModel)shell.Current!;
Settle(region.LoadCompletion);
var cell = region.Grid.Cells.First(c => c.Status == ChunkDiffStatus.Different);
region.SelectCommand.Execute(cell);
Settle();
Settle();
Snap("region");

region.OpenSelectedCommand.Execute(null);
var chunk = (FileCompareViewModel)shell.Current!;
Settle(chunk.LoadCompletion);
chunk.SelectedItem = chunk.Tree.Rows.FirstOrDefault(i => i.DeltaText is not null) ?? chunk.SelectedItem;
Settle();
Snap("tag");

// FTB quest progress (SNBT) tag diff, opened from the folder view.
Settle(shell.NavigateToCommand.ExecuteAsync(0));
folder.SelectedRow = folder.Tree.Rows.Single(i => i.RelativePath.StartsWith("ftbquests/", StringComparison.Ordinal) && !i.IsDirectory);
folder.OpenSelectedCommand.Execute(null);
var snbt = (FileCompareViewModel)shell.Current!;
Settle(snbt.LoadCompletion);
Snap("snbt");
return 0;

sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public IDisposable StartTimer(TimeSpan interval, Action tick) => DispatcherTimer.Run(() => { tick(); return true; }, interval);
}

sealed class NoDialogs : IDialogService
{
    public Task<string?> PickFolderAsync(string title, string? startPath = null) => Task.FromResult<string?>(null);
    public Task<string?> PickOpenFileAsync(string title, string? startPath = null) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeLabel) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Discard") => Task.FromResult(false);
}
