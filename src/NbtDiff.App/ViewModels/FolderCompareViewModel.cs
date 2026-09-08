using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NbtDiff.App.Services;
using NbtDiff.App.Tree;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>Beyond-Compare-style folder view (DESIGN §5.1) over <see cref="DirectoryComparer"/>.</summary>
public sealed partial class FolderCompareViewModel : ViewModelBase
{
    /// <summary>How often worker-thread changes are flushed to the grid.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(33);

    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IFingerprinter _fingerprinter;
    private readonly ChangeCoalescer<CompareRow> _changes = new();

    private Dictionary<CompareRow, CompareRowItem> _index = new();
    private CancellationTokenSource? _cts;
    private IDisposable? _timer;
    private readonly object _progressGate = new();
    private ScanProgress _progress;
    private volatile string? _lastChangedDir;

    private ScanProgress LatestProgress
    {
        get { lock (_progressGate) return _progress; }
        set { lock (_progressGate) _progress = value; }
    }

    public override string Title => "Folder compare";
    public FlatTreeSource<CompareRowItem> Tree { get; } = new();
    public CompareRoot? Current { get; private set; }
    /// <summary>Completes after the UI has flushed the final state of every row. For tests.</summary>
    public Task? ScanCompletion { get; private set; }

    /// <summary>Raised when the user opens a file row (double-click / Enter). The shell decides what to show.</summary>
    public event Action<CompareRow>? NavigationRequested;

    [ObservableProperty] private string _leftPath = "";
    [ObservableProperty] private string _rightPath = "";
    [ObservableProperty] private bool _deepVerify = true;
    [ObservableProperty] private RowFilter _filter = RowFilter.All;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(CancelCommand), nameof(ExportCommand))] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Choose two folders and press Compare.";
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private CompareRowItem? _selectedRow;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;
    public bool HasResult => Current is not null;

    public FolderCompareViewModel(IDialogService dialogs, IUiDispatcher ui, IFingerprinter? fingerprinter = null)
    {
        _dialogs = dialogs;
        _ui = ui;
        _fingerprinter = fingerprinter ?? new Fingerprinter();
    }

    partial void OnFilterChanged(RowFilter value)
    {
        Tree.SetFilter(value == RowFilter.All ? null : item => CompareRowItem.Matches(value, item.Status));
    }

    [RelayCommand]
    private void SetFilter(RowFilter filter) => Filter = filter;

    private bool CanCompare => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare()
    {
        ErrorMessage = null;
        string left = LeftPath.Trim(), right = RightPath.Trim();
        if (!Directory.Exists(left)) { ErrorMessage = $"Left folder does not exist: {left}"; return; }
        if (!Directory.Exists(right)) { ErrorMessage = $"Right folder does not exist: {right}"; return; }

        CompareRoot root;
        try
        {
            var comparer = new DirectoryComparer(_fingerprinter, new CompareOptions(DeepVerify: DeepVerify));
            root = comparer.Prepare(left, right);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Cannot read folders: {e.Message}";
            return;
        }

        Current = root;
        OnPropertyChanged(nameof(HasResult));
        var (rootItem, index) = CompareRowItem.Build(root.Root);
        _index = index;
        Tree.SetRoots(rootItem.Children);
        Tree.ExpandToDepth(1);
        _changes.Drain();
        _lastChangedDir = null;
        LatestProgress = root.Progress;

        root.RowChanged += OnRowChanged;
        root.ProgressChanged += p => LatestProgress = p;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        ProgressFraction = 0;
        UpdateStatus();
        _timer = _ui.StartTimer(FlushInterval, Flush);
        root.Run(_cts.Token);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScanCompletion = done.Task;
        root.Completion.ContinueWith(_ => _ui.Post(() =>
        {
            try { Finish(root); }
            finally { done.TrySetResult(); }
        }), TaskScheduler.Default);
    }

    private bool CanCancel => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void OnRowChanged(CompareRow row)
    {
        _changes.Add(row);
        if (!row.IsDirectory && row.Parent is { } dir) _lastChangedDir = dir.RelativePath;
    }

    /// <summary>UI-thread flush: refresh changed items once each, refilter if anything moved in or out of view, update the status bar.</summary>
    private void Flush()
    {
        var changed = _changes.Drain();
        if (changed.Count > 0)
        {
            foreach (var row in changed)
                if (_index.TryGetValue(row, out var item)) item.Refresh();
            if (Filter != RowFilter.All) Tree.Refilter();
        }
        UpdateStatus();
    }

    private void Finish(CompareRoot root)
    {
        if (!ReferenceEquals(root, Current)) return;
        _timer?.Dispose();
        _timer = null;
        Flush();
        IsScanning = false;
        ProgressFraction = 1;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (Current is null) return;
        var counts = Current.Root.Counts;
        var p = LatestProgress;
        string summary = CompareRowItem.DescribeCounts(counts);
        if (IsScanning)
        {
            ProgressFraction = p.Fraction;
            string where = _lastChangedDir is { Length: > 0 } d ? $"scanning {d}/" : p.Tier1Complete ? "verifying" : "scanning";
            StatusText = $"{summary} · {where} ({p.Fraction:P0})";
        }
        else
        {
            StatusText = Current.IsCancelled ? $"{summary} · cancelled" : summary;
        }
    }

    [RelayCommand]
    private async Task BrowseLeft()
    {
        var path = await _dialogs.PickFolderAsync("Left folder", LeftPath);
        if (path is not null) LeftPath = path;
    }

    [RelayCommand]
    private async Task BrowseRight()
    {
        var path = await _dialogs.PickFolderAsync("Right folder", RightPath);
        if (path is not null) RightPath = path;
    }

    private bool CanExport => Current is not null && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task Export(string format)
    {
        if (Current is null) return;
        var fmt = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Json : ReportFormat.Text;
        string ext = fmt == ReportFormat.Json ? "json" : "txt";
        var path = await _dialogs.PickSaveFileAsync("Export report", $"nbtdiff-report.{ext}", ext, fmt == ReportFormat.Json ? "JSON report" : "Text report");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, DiffReport.ToString(Current, fmt));
            StatusText = $"Report written to {path}";
        }
        catch (Exception e)
        {
            ErrorMessage = $"Export failed: {e.Message}";
        }
    }

    [RelayCommand]
    private void Toggle(CompareRowItem? item)
    {
        if (item is not null) Tree.Toggle(item);
    }

    [RelayCommand]
    private void ExpandAll() => Tree.ExpandAll();

    [RelayCommand]
    private void CollapseAll() => Tree.CollapseAll();

    /// <summary>Double-click / Enter: folders toggle, files ask the shell to open a compare view.</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        var item = SelectedRow;
        if (item is null) return;
        if (item.IsDirectory) Tree.Toggle(item);
        else NavigationRequested?.Invoke(item.Row);
    }

    [RelayCommand]
    private void ExpandSelected()
    {
        if (SelectedRow is { IsDirectory: true } item) Tree.Expand(item);
    }

    [RelayCommand]
    private void CollapseSelected()
    {
        if (SelectedRow is null) return;
        if (SelectedRow.IsExpanded) Tree.Collapse(SelectedRow);
        else if (SelectedRow.Parent is { } parent && Tree.Rows.Contains(parent)) SelectedRow = parent;
    }
}
