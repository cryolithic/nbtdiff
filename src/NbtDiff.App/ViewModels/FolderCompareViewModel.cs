using System.Collections.ObjectModel;
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
    private readonly IFingerprinter? _injectedFingerprinter;
    private readonly ISettingsService _settings;
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

    /// <summary>The breadcrumb names the world being compared (the left folder's name).</summary>
    public override IReadOnlyList<string> CrumbLabels =>
        [Path.GetFileName(LeftPath.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : Title];
    public FlatTreeSource<CompareRowItem> Tree { get; } = new();
    public CompareRoot? Current { get; private set; }
    /// <summary>Completes after the UI has flushed the final state of every row. For tests.</summary>
    public Task? ScanCompletion { get; private set; }

    /// <summary>Raised when the user opens a file row (double-click / Enter). The shell decides what to show.</summary>
    public event Action<CompareRow>? NavigationRequested;

    /// <summary>Raised when Compare is pressed with two file paths (or a file and a missing path);
    /// the shell opens the pair's compare view instead of a folder scan.</summary>
    public event Action<string, string>? FilePairRequested;

    [ObservableProperty] private string _leftPath = "";
    [ObservableProperty] private string _rightPath = "";
    [ObservableProperty] private bool _deepVerify = true;
    /// <summary>Match compound list items by identity (entity UUID, block-entity position, …) so reordered lists are not differences. Persisted; also used by the file/chunk views.</summary>
    [ObservableProperty] private bool _useKeyedAligner = true;
    /// <summary>Comma-separated tag paths that are not content (persisted). Applies to the next compare and to file/chunk views.</summary>
    [ObservableProperty] private string _ignoredTagsText = "";
    [ObservableProperty] private RowFilter _filter = RowFilter.All;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(CancelCommand), nameof(ExportCommand))] private bool _isScanning;
    [ObservableProperty] private string _statusText = "Choose two folders and press Compare.";
    /// <summary>Counts of the whole compare, for the filter badges and the status bar chips (#10).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DifferencesCount))] private RowCounts _counts;
    /// <summary>What the scan is doing, e.g. <c>Pass 1 of 2 · hashing bytes in region/</c>; empty when idle.</summary>
    [ObservableProperty] private string _passText = "";
    public int DifferencesCount => Counts.Different + Counts.ProbablyDifferent + Counts.LeftOnly + Counts.RightOnly;
    /// <summary>Summary shown on the Compare options button, so the settings stay visible: <c>Deep verify · keyed lists · 2 ignored tags</c>.</summary>
    public string OptionsSummary
    {
        get
        {
            int ignored = TagIgnoreSet.ParseList(IgnoredTagsText).Paths.Count;
            var parts = new List<string>(3) { DeepVerify ? "Deep verify" : "Quick scan" };
            if (UseKeyedAligner) parts.Add("keyed lists");
            parts.Add(ignored == 0 ? "no ignored tags" : ignored == 1 ? "1 ignored tag" : $"{ignored} ignored tags");
            return string.Join(" · ", parts);
        }
    }
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private CompareRowItem? _selectedRow;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;

    public bool HasError => ErrorMessage is not null;
    public bool HasResult => Current is not null;

    /// <summary>Most recently compared pairs, newest first (persisted).</summary>
    public ObservableCollection<RecentPair> RecentPairs { get; } = [];
    public bool HasRecent => RecentPairs.Count > 0;

    [ObservableProperty] private RecentPair? _selectedRecent;

    public FolderCompareViewModel(IDialogService dialogs, IUiDispatcher ui, IFingerprinter? fingerprinter = null, ISettingsService? settings = null)
    {
        _dialogs = dialogs;
        _ui = ui;
        _settings = settings ?? new SettingsService(null);
        _injectedFingerprinter = fingerprinter;
        _deepVerify = _settings.Current.DeepVerify;
        _useKeyedAligner = _settings.Current.UseKeyedAligner;
        _ignoredTagsText = string.Join(", ", _settings.Current.IgnoredTags);
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        RecentPairs.Clear();
        foreach (var p in _settings.Current.RecentPairs) RecentPairs.Add(p);
        OnPropertyChanged(nameof(HasRecent));
    }

    private void AddRecentPair(string left, string right)
    {
        _settings.Current.AddRecent(left, right);
        _settings.Save();
        RefreshRecent();
    }

    partial void OnDeepVerifyChanged(bool value)
    {
        _settings.Current.DeepVerify = value;
        _settings.Save();
        OnPropertyChanged(nameof(OptionsSummary));
    }

    partial void OnUseKeyedAlignerChanged(bool value)
    {
        _settings.Current.UseKeyedAligner = value;
        _settings.Save();
        OnPropertyChanged(nameof(OptionsSummary));
    }

    partial void OnIgnoredTagsTextChanged(string value)
    {
        _settings.Current.IgnoredTags = TagIgnoreSet.ParseList(value).Paths.ToList();
        _settings.Save();
        OnPropertyChanged(nameof(OptionsSummary));
    }

    /// <summary>Swaps the two paths; a finished compare is re-run so the results swap too.</summary>
    [RelayCommand]
    private void Swap()
    {
        (LeftPath, RightPath) = (RightPath, LeftPath);
        if (HasResult && !IsScanning && CompareCommand.CanExecute(null)) CompareCommand.Execute(null);
    }

    /// <summary>Picking a recent pair fills both paths and compares; the picker then clears so it can be picked again.</summary>
    partial void OnSelectedRecentChanged(RecentPair? value)
    {
        if (value is null) return;
        LeftPath = value.Left;
        RightPath = value.Right;
        SelectedRecent = null;
        if (CompareCommand.CanExecute(null)) CompareCommand.Execute(null);
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
        bool leftDir = Directory.Exists(left), rightDir = Directory.Exists(right);
        bool leftFile = File.Exists(left), rightFile = File.Exists(right);

        if (!leftDir && !rightDir && (leftFile || rightFile))
        {
            AddRecentPair(left, right);
            FilePairRequested?.Invoke(left, right);
            return;
        }
        if (leftDir && rightDir)
        {
            CompareFolders(left, right);
        }
        else if (leftFile || rightFile)
        {
            ErrorMessage = "One path is a folder and the other is a file — compare two folders, or two files.";
        }
        else if (!leftDir)
        {
            ErrorMessage = $"Left path does not exist: {left}";
        }
        else
        {
            ErrorMessage = $"Right path does not exist: {right}";
        }
    }

    private void CompareFolders(string left, string right)
    {
        CompareRoot root;
        try
        {
            var options = _settings.Current.ToCompareOptions() with { DeepVerify = DeepVerify };
            // Built per compare so edited ignore tags / key-order settings take effect without a restart.
            var comparer = new DirectoryComparer(_injectedFingerprinter ?? Fingerprinter.For(options), options);
            root = comparer.Prepare(left, right);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Cannot read folders: {e.Message}";
            return;
        }

        AddRecentPair(left, right);

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
        Counts = counts;
        var p = LatestProgress;
        string summary = CompareRowItem.DescribeCounts(counts);
        if (IsScanning)
        {
            ProgressFraction = p.Fraction;
            // Tier 2 is a small tail after thousands of Tier-1 files, so a percentage would read 100% while it runs.
            string where = p.Tier1Complete && p.Tier2Queued > 0
                ? $"pass 2 of 2 — verifying content {p.Tier2Done}/{p.Tier2Queued}"
                : $"pass 1 of 2 — hashing bytes{(_lastChangedDir is { Length: > 0 } d ? $" in {d}/" : "")} ({p.Fraction:P0})";
            StatusText = $"{summary} · {where}";
            PassText = p.Tier1Complete && p.Tier2Queued > 0
                ? $"Pass 2 of 2 · verifying content {p.Tier2Done}/{p.Tier2Queued}"
                : $"Pass 1 of 2 · hashing bytes{(_lastChangedDir is { Length: > 0 } dir ? $" in {dir}/" : "")}";
        }
        else
        {
            StatusText = Current.IsCancelled ? $"{summary} · cancelled" : summary;
            PassText = Current.IsCancelled ? "Cancelled" : "";
        }
    }

    // Four explicit pickers rather than one guessing button: an empty box must still reach the
    // file picker (two .dat files can be compared straight from this view).
    [RelayCommand]
    private async Task BrowseLeftFolder()
    {
        var path = await _dialogs.PickFolderAsync("Left folder", LeftPath);
        if (path is not null) LeftPath = path;
    }

    [RelayCommand]
    private async Task BrowseLeftFile()
    {
        var path = await _dialogs.PickOpenFileAsync("Left file", LeftPath);
        if (path is not null) LeftPath = path;
    }

    [RelayCommand]
    private async Task BrowseRightFolder()
    {
        var path = await _dialogs.PickFolderAsync("Right folder", RightPath);
        if (path is not null) RightPath = path;
    }

    [RelayCommand]
    private async Task BrowseRightFile()
    {
        var path = await _dialogs.PickOpenFileAsync("Right file", RightPath);
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
