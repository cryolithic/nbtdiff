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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void Back()
    {
        if (_stack.Count <= 1) return;
        _stack.RemoveAt(_stack.Count - 1);
        Current = _stack[^1];
    }

    public FolderCompareViewModel NewFolderCompare()
    {
        var vm = new FolderCompareViewModel(_dialogs, _ui, _fingerprinter);
        vm.NavigationRequested += row => Push(new PlaceholderViewModel(
            row.Name, $"File compare is not implemented yet (S6).\n\n{row.RelativePath}\nStatus: {row.Status}" + (row.Error is null ? "" : $"\n{row.Error}")));
        return vm;
    }

    /// <summary>
    /// <c>nbtdiff &lt;left&gt; &lt;right&gt;</c>: two directories start a folder compare; two files go to the
    /// (placeholder) file compare; anything else is an error view. No arguments: empty folder view.
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
        else if (leftFile && rightFile)
        {
            Push(new PlaceholderViewModel("File compare", $"File compare is not implemented yet (S6).\n\n{left}\n{right}"));
        }
        else
        {
            string Describe(string p) => Directory.Exists(p) ? "directory" : File.Exists(p) ? "file" : "missing";
            Push(new PlaceholderViewModel("Cannot compare",
                $"Both arguments must be directories or both files.\n\n{left} — {Describe(left)}\n{right} — {Describe(right)}", isError: true));
        }
    }
}
