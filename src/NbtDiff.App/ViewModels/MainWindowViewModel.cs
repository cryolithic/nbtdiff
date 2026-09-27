using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NbtDiff.App.Services;
using NbtDiff.Core;
using NbtDiff.Nbt;

namespace NbtDiff.App.ViewModels;

/// <summary>Window shell: a navigation stack of views (DESIGN §5) and start-up argument handling (§5.4).</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IFingerprinter? _fingerprinter;
    private readonly List<ViewModelBase> _stack = [];

    public ISettingsService Settings { get; }

    /// <summary>One per app session, so the backup prompt is shown once, before the first save.</summary>
    public SaveGate SaveGate { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(WindowTitle), nameof(Crumbs))] [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private ViewModelBase? _current;

    public bool CanGoBack => _stack.Count > 1;

    /// <summary>The breadcrumb over the navigation stack (#13): each view contributes its labels; earlier ones pop back to that level.</summary>
    public IReadOnlyList<Crumb> Crumbs
    {
        get
        {
            var crumbs = new List<Crumb>();
            for (int level = 0; level < _stack.Count; level++)
            {
                var labels = _stack[level].CrumbLabels;
                for (int i = 0; i < labels.Count; i++)
                    crumbs.Add(new Crumb(labels[i], level, level == _stack.Count - 1 && i == labels.Count - 1, crumbs.Count == 0));
            }
            return crumbs;
        }
    }
    public string WindowTitle => Current is null ? "nbt-diff" : $"nbt-diff — {Current.Title}";
    public IReadOnlyList<ViewModelBase> Stack => _stack;

    /// <summary>The current view has copies that have not been saved back; ask before Back or window close discards them.</summary>
    public bool HasDiscardableEdits => Current is FileCompareViewModel { HasUnsavedEdits: true };

    /// <summary>True when there is nothing to lose, or the user chose to discard it.</summary>
    public async Task<bool> ConfirmDiscardEditsAsync() =>
        !HasDiscardableEdits || await _dialogs.ConfirmAsync("Unsaved copies",
            "This view has copies that have not been saved back yet.\n\nDiscard them?");

    /// <param name="settings">Null keeps settings in memory only.</param>
    public MainWindowViewModel(IDialogService dialogs, IUiDispatcher ui, IFingerprinter? fingerprinter = null, ISettingsService? settings = null)
    {
        _dialogs = dialogs;
        _ui = ui;
        _fingerprinter = fingerprinter;
        Settings = settings ?? new SettingsService(null);
        SaveGate = new SaveGate(dialogs);
    }

    public void Push(ViewModelBase vm)
    {
        _stack.Add(vm);
        vm.PropertyChanged += OnCurrentTitleChanged;
        Current = vm;
    }

    // A chunk view changes its Title when stepping between chunks; keep the window title in step.
    private void OnCurrentTitleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModelBase.Title) && ReferenceEquals(sender, Current))
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(Crumbs));
        }
    }

    /// <summary>Pops the current view. The view below keeps its state (a folder scan is not restarted); the popped view is disposed if it owns resources. Unsaved copies are confirmed first.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync() => PopToAsync(_stack.Count - 2);

    /// <summary>Pops back to a breadcrumb's level (#13), with the same unsaved-copies confirmation and disposal as Back.</summary>
    [RelayCommand]
    private Task NavigateToAsync(int level) => PopToAsync(level);

    private async Task PopToAsync(int level)
    {
        if (level < 0 || level >= _stack.Count - 1) return;
        if (!await ConfirmDiscardEditsAsync()) return;
        while (_stack.Count - 1 > level)
        {
            var popped = _stack[^1];
            popped.PropertyChanged -= OnCurrentTitleChanged;
            _stack.RemoveAt(_stack.Count - 1);
            (popped as IDisposable)?.Dispose();
        }
        Current = _stack[^1];
    }

    public FolderCompareViewModel NewFolderCompare()
    {
        var vm = new FolderCompareViewModel(_dialogs, _ui, _fingerprinter, Settings);
        vm.NavigationRequested += row =>
        {
            var view = CreateCompareView(row.Kind, row.Left?.FullPath, row.Right?.FullPath, row.Left?.Fingerprint, row.Right?.Fingerprint);
            view.CrumbPath = row.RelativePath;
            Push(view);
        };
        // Two file paths typed into the folder view open the pair's compare view directly.
        vm.FilePairRequested += (left, right) =>
        {
            if (OpenPair(left, right) is { } error) vm.ErrorMessage = error;
        };
        return vm;
    }

    /// <summary>
    /// The view for one file pair by kind: regions get the chunk grid, NBT/SNBT the tag tree, JSON and
    /// text a line diff, binary files a summary of sizes and hashes. A null path is an absent side.
    /// Loading starts immediately.
    /// </summary>
    private static bool ParsesAsSnbt(string? path) => path is null || !File.Exists(path) || NbtDocument.Load(path).Ok;

    public ViewModelBase CreateCompareView(FileKind kind, string? leftPath, string? rightPath, FileFingerprint? leftFp = null, FileFingerprint? rightFp = null)
    {
        switch (kind)
        {
            case FileKind.Region:
            {
                var vm = new RegionCompareViewModel(leftPath, rightPath, leftFp, rightFp, _ui, Settings, SaveGate);
                vm.NavigationRequested += Push;
                _ = vm.Load();
                return vm;
            }
            case FileKind.Snbt when !ParsesAsSnbt(leftPath) || !ParsesAsSnbt(rightPath):
                // Comment-only or otherwise not SNBT (FTB leaves such files behind): a line diff.
                goto case FileKind.Text;
            case FileKind.Nbt:
            case FileKind.Snbt:
            {
                var vm = new FileCompareViewModel(new FileDiffSource(leftPath, rightPath), _ui, Settings, saveGate: SaveGate);
                _ = vm.Load();
                return vm;
            }
            case FileKind.Json:
            case FileKind.Text:
            {
                var vm = new TextCompareViewModel(leftPath, rightPath, _ui);
                _ = vm.Load();
                return vm;
            }
            default:
                return new PlaceholderViewModel(FileDiffSource.PairTitle(leftPath, rightPath),
                    $"Binary files are compared byte for byte.\n\n{DescribeBinary(leftPath, leftFp)}\n{DescribeBinary(rightPath, rightFp)}");
        }
    }

    private static string DescribeBinary(string? path, FileFingerprint? fp)
    {
        if (path is null || !File.Exists(path)) return "(missing)";
        long size = fp?.Size ?? new FileInfo(path).Length;
        string hash = fp is null ? "hash not computed" : $"xxh64 {fp.Hash:x16}";
        return $"{path} — {size:N0} bytes · {hash}";
    }

    /// <summary>
    /// <c>nbtdiff &lt;left&gt; &lt;right&gt;</c>: two directories start a folder compare; two files (or one file
    /// and a missing path) open the compare view for their kind; anything else is an error view.
    /// No arguments: empty folder view.
    /// </summary>
    public void Start(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Push(NewFolderCompare());
            return;
        }
        if (args.Count != 2)
        {
            Push(new PlaceholderViewModel("Usage", "Usage: nbtdiff <left> <right>\n\nBoth arguments must be directories (world folders) or both files.", isError: true));
            return;
        }
        if (OpenPair(args[0], args[1]) is { } error)
            Push(new PlaceholderViewModel("Cannot compare", error, isError: true));
    }

    /// <summary>
    /// Routes any two typed paths: two directories start a folder compare; two files (or a file and a
    /// missing path) open the pair's compare view for its kind. Pushes the view and returns null, or
    /// returns why the pair cannot be compared without pushing anything. Used by <see cref="Start"/>
    /// and the folder view's Compare button.
    /// </summary>
    public string? OpenPair(string left, string right)
    {
        bool leftDir = Directory.Exists(left), rightDir = Directory.Exists(right);
        bool leftFile = File.Exists(left), rightFile = File.Exists(right);

        if (leftDir && rightDir)
        {
            var vm = NewFolderCompare();
            vm.LeftPath = left;
            vm.RightPath = right;
            Push(vm);
            vm.CompareCommand.Execute(null);
            return null;
        }
        if ((leftFile || rightFile) && !leftDir && !rightDir)
        {
            var leftKind = leftFile ? FileClassifier.Classify(left) : (FileKind?)null;
            var rightKind = rightFile ? FileClassifier.Classify(right) : (FileKind?)null;
            if (leftKind is not null && rightKind is not null && leftKind != rightKind)
                return $"The files are of different kinds.\n\n{left} — {leftKind}\n{right} — {rightKind}";
            Push(CreateCompareView(leftKind ?? rightKind!.Value, leftFile ? left : null, rightFile ? right : null));
            return null;
        }
        string Describe(string p) => Directory.Exists(p) ? "directory" : File.Exists(p) ? "file" : "missing";
        return $"Both paths must be folders, or both files.\n\n{left} — {Describe(left)}\n{right} — {Describe(right)}";
    }
}
