using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NbtDiff.App.Services;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>Window shell: a navigation stack of views (DESIGN §5) and start-up argument handling (§5.4).</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IFingerprinter? _fingerprinter;
    private readonly List<ViewModelBase> _stack = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(WindowTitle))] [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private ViewModelBase? _current;

    public bool CanGoBack => _stack.Count > 1;
    public string WindowTitle => Current is null ? "nbt-diff" : $"nbt-diff — {Current.Title}";
    public IReadOnlyList<ViewModelBase> Stack => _stack;

    public MainWindowViewModel(IDialogService dialogs, IUiDispatcher ui, IFingerprinter? fingerprinter = null)
    {
        _dialogs = dialogs;
        _ui = ui;
        _fingerprinter = fingerprinter;
    }

    public void Push(ViewModelBase vm)
    {
        _stack.Add(vm);
        Current = vm;
    }

    /// <summary>Pops the current view. The view below keeps its state (a folder scan is not restarted); the popped view is disposed if it owns resources.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void Back()
    {
        if (_stack.Count <= 1) return;
        var popped = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        Current = _stack[^1];
        (popped as IDisposable)?.Dispose();
    }

    public FolderCompareViewModel NewFolderCompare()
    {
        var vm = new FolderCompareViewModel(_dialogs, _ui, _fingerprinter);
        vm.NavigationRequested += row => Push(CreateCompareView(row.Kind, row.Left?.FullPath, row.Right?.FullPath, row.Left?.Fingerprint, row.Right?.Fingerprint));
        return vm;
    }

    /// <summary>
    /// The view for one file pair by kind: regions get the chunk grid, NBT/SNBT the tag tree, everything
    /// else a placeholder until S7 adds text compare. A null path is an absent side. Loading starts immediately.
    /// </summary>
    public ViewModelBase CreateCompareView(FileKind kind, string? leftPath, string? rightPath, FileFingerprint? leftFp = null, FileFingerprint? rightFp = null)
    {
        switch (kind)
        {
            case FileKind.Region:
            {
                var vm = new RegionCompareViewModel(leftPath, rightPath, leftFp, rightFp, _ui);
                vm.NavigationRequested += Push;
                _ = vm.Load();
                return vm;
            }
            case FileKind.Nbt:
            case FileKind.Snbt:
            {
                var vm = new FileCompareViewModel(new FileDiffSource(leftPath, rightPath), _ui);
                _ = vm.Load();
                return vm;
            }
            default:
                return new PlaceholderViewModel(FileDiffSource.PairTitle(leftPath, rightPath),
                    $"{kind} files are compared byte for byte in the folder view; a text compare view is not implemented yet (S7).\n\n{leftPath ?? "(missing)"}\n{rightPath ?? "(missing)"}");
        }
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

        string left = args[0], right = args[1];
        bool leftDir = Directory.Exists(left), rightDir = Directory.Exists(right);
        bool leftFile = File.Exists(left), rightFile = File.Exists(right);

        if (leftDir && rightDir)
        {
            var vm = NewFolderCompare();
            vm.LeftPath = left;
            vm.RightPath = right;
            Push(vm);
            vm.CompareCommand.Execute(null);
        }
        else if ((leftFile || rightFile) && !leftDir && !rightDir)
        {
            var leftKind = leftFile ? FileClassifier.Classify(left) : (FileKind?)null;
            var rightKind = rightFile ? FileClassifier.Classify(right) : (FileKind?)null;
            if (leftKind is not null && rightKind is not null && leftKind != rightKind)
            {
                Push(new PlaceholderViewModel("Cannot compare",
                    $"The files are of different kinds.\n\n{left} — {leftKind}\n{right} — {rightKind}", isError: true));
                return;
            }
            Push(CreateCompareView(leftKind ?? rightKind!.Value, leftFile ? left : null, rightFile ? right : null));
        }
        else
        {
            string Describe(string p) => Directory.Exists(p) ? "directory" : File.Exists(p) ? "file" : "missing";
            Push(new PlaceholderViewModel("Cannot compare",
                $"Both arguments must be directories or both files.\n\n{left} — {Describe(left)}\n{right} — {Describe(right)}", isError: true));
        }
    }
}
