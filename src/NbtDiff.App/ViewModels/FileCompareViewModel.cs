using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using fNbt;
using NbtDiff.App.Services;
using NbtDiff.App.Tree;
using NbtDiff.Core;
using NbtDiff.Nbt;

namespace NbtDiff.App.ViewModels;

/// <summary>
/// Aligned tag-tree diff of one file pair or chunk pair (DESIGN §5.3). Loading and diffing happen
/// off the UI thread; option toggles re-diff the cached tags. Changed nodes are navigated in
/// pre-order with wrap-around: Next from the last change goes to the first, Previous from the first
/// goes to the last.
/// </summary>
/// <summary>
/// Lets a chunk view step to the other changed chunks of its region without returning to the grid.
/// <paramref name="Chunks"/> is in (z, x) order; <paramref name="Moved"/> lets the grid follow the selection.
/// </summary>
public sealed record ChunkNavigation(IReadOnlyList<(int X, int Z)> Chunks, Func<int, int, IDiffSource> SourceFor, Action<int, int>? Moved = null);

public sealed partial class FileCompareViewModel : ViewModelBase
{
    private IDiffSource _source;
    private readonly ChunkNavigation? _navigation;
    private int _chunkIndex = -1;
    private readonly IUiDispatcher _ui;
    private TagPair? _tags;
    private DiffNodeItem? _root;
    private List<DiffNodeItem> _changed = [];
    private int _generation;

    public override string Title => _source.Title;
    public FlatTreeSource<DiffNodeItem> Tree { get; } = new();
    public DiffNodeItem? Root => _root;
    /// <summary>Changed nodes in navigation (pre-order) order.</summary>
    public IReadOnlyList<DiffNodeItem> ChangedNodes => _changed;
    /// <summary>Completes after the most recent load or re-diff has been applied. For tests.</summary>
    public Task? LoadCompletion { get; private set; }

    [ObservableProperty] private bool _showUnchanged;
    [ObservableProperty] private bool _compoundOrderMatters;
    [ObservableProperty] private bool _useKeyedAligner;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasArrayDetail))] private IReadOnlyList<ArrayDetailRow>? _arrayDetail;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyToLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyToRightCommand))]
    private DiffNodeItem? _selectedItem;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedEdits))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _leftModified;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedEdits))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _rightModified;
    [ObservableProperty] private string _saveStatusText = "";

    public bool HasError => ErrorMessage is not null;
    public bool HasArrayDetail => ArrayDetail is not null;
    public bool HasResult => _root is not null;
    /// <summary>Either side has WinMerge-style copies that have not been written back yet.</summary>
    public bool HasUnsavedEdits => LeftModified || RightModified;
    /// <summary>False for in-memory pairs (tests) — there is nowhere to save those to.</summary>
    public bool IsSaveable => _source is ISaveableDiffSource;

    private readonly ISettingsService _settings;

    /// <param name="settings">Seeds and persists the compare toggles; null keeps them in memory.</param>
    /// <param name="navigation">Sibling chunks reachable with Next/Previous chunk; null for standalone files.</param>
    public FileCompareViewModel(IDiffSource source, IUiDispatcher ui, ISettingsService? settings = null, ChunkNavigation? navigation = null)
    {
        _source = source;
        _ui = ui;
        _settings = settings ?? new SettingsService(null);
        _navigation = navigation;
        _compoundOrderMatters = _settings.Current.CompoundOrderMatters;
        _useKeyedAligner = _settings.Current.UseKeyedAligner;
        if (navigation is not null && source is ChunkDiffSource c)
            _chunkIndex = IndexOf(navigation.Chunks, c.X, c.Z);
    }

    /// <summary>Tag paths excluded from the diff come from settings (the folder view edits them).</summary>
    public DiffOptions Options => new(CompoundOrderMatters, UseKeyedAligner ? KeyedAligner.Default : null, _settings.Current.IgnoreSet);
    // Note: the scan and region grid hash with the same aligner (settings.UseKeyedAligner), so a chunk
    // that shows as Different there also shows changes here, and vice versa.

    public bool HasChunkNavigation => _navigation is { Chunks.Count: > 0 };
    public ChunkNavigation? Navigation => _navigation;
    /// <summary>Position among the region's changed chunks, e.g. "changed chunk 2 of 7"; empty when the current chunk is not one of them.</summary>
    public string ChunkPositionText => _navigation is null || _chunkIndex < 0 ? "" : $"changed chunk {_chunkIndex + 1} of {_navigation.Chunks.Count}";

    [RelayCommand(CanExecute = nameof(HasChunkNavigation))]
    private void NextChunk() => StepChunk(+1);

    [RelayCommand(CanExecute = nameof(HasChunkNavigation))]
    private void PreviousChunk() => StepChunk(-1);

    // Wraps at both ends. From a chunk that is not in the list (the user opened an unchanged one),
    // Next goes to the first changed chunk after it in (z, x) order and Previous to the last before it.
    private void StepChunk(int direction)
    {
        if (HasUnsavedEdits)
        {
            ErrorMessage = "Save your copies first (Ctrl+S) — stepping to another chunk would discard them.";
            return;
        }
        if (_navigation is null || _navigation.Chunks.Count == 0) return;
        var chunks = _navigation.Chunks;
        int n = chunks.Count;
        int target;
        if (_chunkIndex >= 0)
            target = (_chunkIndex + direction + n) % n;
        else if (_source is ChunkDiffSource c)
        {
            long here = Order(c.X, c.Z);
            target = direction > 0
                ? Enumerable.Range(0, n).FirstOrDefault(i => Order(chunks[i].X, chunks[i].Z) > here, 0)
                : Enumerable.Range(0, n).LastOrDefault(i => Order(chunks[i].X, chunks[i].Z) < here, n - 1);
        }
        else
            target = direction > 0 ? 0 : n - 1;

        _chunkIndex = target;
        var (x, z) = chunks[target];
        _source = _navigation.SourceFor(x, z);
        _tags = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ChunkPositionText));
        _navigation.Moved?.Invoke(x, z);
        _ = Load();
    }

    private static long Order(int x, int z) => (long)z * RegionCoords.ChunksPerAxis + x;

    private static int IndexOf(IReadOnlyList<(int X, int Z)> chunks, int x, int z)
    {
        for (int i = 0; i < chunks.Count; i++)
            if (chunks[i].X == x && chunks[i].Z == z) return i;
        return -1;
    }

    /// <summary>Loads both sides and diffs them. Safe to call once; option changes re-diff automatically.</summary>
    public Task Load() => Run(() =>
    {
        var tags = _source.Load();
        if (!tags.Ok) return LoadResult<Loaded>.Fail(tags.Failure!);
        var options = Options;
        return LoadResult<Loaded>.Success(new Loaded(tags.Value!, NbtDiffer.Diff(tags.Value!.Left, tags.Value.Right, options)));
    });

    private Task Rediff()
    {
        var tags = _tags;
        if (tags is null) return Task.CompletedTask;
        var options = Options;
        return Run(() => LoadResult<Loaded>.Success(new Loaded(tags, NbtDiffer.Diff(tags.Left, tags.Right, options))));
    }

    private sealed record Loaded(TagPair Tags, DiffNode Diff);

    private Task Run(Func<LoadResult<Loaded>> work)
    {
        IsBusy = true;
        ErrorMessage = null;
        int generation = ++_generation;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadCompletion = done.Task;
        Task.Run(() =>
        {
            LoadResult<Loaded> result;
            try { result = work(); }
            catch (Exception e) { result = LoadResult<Loaded>.Fail("Compare", e); }
            _ui.Post(() =>
            {
                try
                {
                    if (generation != _generation) return; // superseded by a later request
                    IsBusy = false;
                    if (!result.Ok)
                    {
                        ErrorMessage = result.Failure!.ToDetailedString();
                        return;
                    }
                    _tags = result.Value!.Tags;
                    Present(result.Value.Diff);
                }
                finally
                {
                    done.TrySetResult();
                }
            });
        });
        return done.Task;
    }

    private void Present(DiffNode diff)
    {
        _root = DiffNodeItem.Build(diff);
        _changed = _root.Descendants().Where(i => i.IsChanged).ToList();
        SelectedItem = null;
        // Identical trees would otherwise render as an empty grid; show the whole tree instead.
        if (_changed.Count == 0) ShowUnchanged = true;
        Tree.SetFilter(ShowUnchanged ? null : static i => i.IsChanged);
        Tree.SetRoots([_root]);
        ExpandChangedPaths();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(ChangedNodes));
        SummaryText = Summarize(_changed);
    }

    /// <summary>Expands every two-sided node that has changes below it, so each change is on screen; one-sided subtrees stay collapsed.</summary>
    private void ExpandChangedPaths()
    {
        if (_root is null) return;
        foreach (var item in _root.Descendants())
        {
            if (item.HasChanges && item.Kind is DiffKind.Unchanged or DiffKind.Moved or DiffKind.Renamed)
                Tree.Expand(item);
        }
        if (!_root.IsExpanded && _root.HasVisibleChildren) Tree.Expand(_root);
    }

    private static string Summarize(List<DiffNodeItem> changed)
    {
        if (changed.Count == 0) return "No differences";
        var counts = changed.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        void Add(DiffKind kind, string label) { if (counts.TryGetValue(kind, out var n)) parts.Add($"{n} {label}"); }
        Add(DiffKind.ValueChanged, "changed");
        Add(DiffKind.TypeChanged, "type changed");
        Add(DiffKind.Added, "right-only");
        Add(DiffKind.Removed, "left-only");
        Add(DiffKind.Moved, "moved");
        Add(DiffKind.Renamed, "renamed");
        return $"{changed.Count} difference{(changed.Count == 1 ? "" : "s")} · {string.Join(" · ", parts)}";
    }

    partial void OnShowUnchangedChanged(bool value)
    {
        if (_root is null) return;
        Tree.SetFilter(value ? null : static i => i.IsChanged);
    }

    partial void OnCompoundOrderMattersChanged(bool value)
    {
        _settings.Current.CompoundOrderMatters = value;
        _settings.Save();
        _ = Rediff();
    }

    partial void OnUseKeyedAlignerChanged(bool value)
    {
        _settings.Current.UseKeyedAligner = value;
        _settings.Save();
        _ = Rediff();
    }

    partial void OnSelectedItemChanged(DiffNodeItem? value) =>
        ArrayDetail = value is { IsArray: true } ? ArrayDetailWindow.Compute(value.Node) : null;

    [RelayCommand]
    private void NextChange()
    {
        if (_changed.Count == 0) return;
        DiffNodeItem target;
        if (SelectedItem is null) target = _changed[0];
        else
        {
            int order = SelectedItem.DfsIndex;
            target = _changed.FirstOrDefault(c => c.DfsIndex > order) ?? _changed[0];
        }
        Go(target);
    }

    [RelayCommand]
    private void PreviousChange()
    {
        if (_changed.Count == 0) return;
        DiffNodeItem target;
        if (SelectedItem is null) target = _changed[^1];
        else
        {
            int order = SelectedItem.DfsIndex;
            target = _changed.LastOrDefault(c => c.DfsIndex < order) ?? _changed[^1];
        }
        Go(target);
    }

    private void Go(DiffNodeItem target)
    {
        Tree.Reveal(target);
        SelectedItem = target;
    }

    private bool CanCopySelection => SelectedItem is not null && _tags is not null;

    /// <summary>WinMerge-style copy of the selected row's value or subtree from right to left.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task CopyToLeft() => Copy(SelectedItem, toRight: false);

    /// <summary>WinMerge-style copy of the selected row's value or subtree from left to right.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySelection))]
    private Task CopyToRight() => Copy(SelectedItem, toRight: true);

    private async Task Copy(DiffNodeItem? item, bool toRight)
    {
        if (IsBusy || _tags is null || item is null) return;
        var chain = new List<DiffNode>();
        for (DiffNodeItem? i = item; i is not null; i = i.Parent) chain.Add(i.Node);
        chain.Reverse();

        NbtTag? newRoot;
        try
        {
            newRoot = NbtMerger.Copy(chain, toRight);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Copy failed at {(item.Path.Length == 0 ? "(root)" : item.Path)}: {e.Message}";
            return;
        }
        // Adopt a replaced root: a root-level copy, or containers synthesized for a missing side.
        if (!ReferenceEquals(toRight ? _tags.Right : _tags.Left, newRoot))
            _tags = new TagPair(toRight ? _tags.Left : newRoot, toRight ? newRoot : _tags.Right);
        if (toRight) RightModified = true; else LeftModified = true;
        SaveStatusText = "";

        var source = toRight ? item.Node.Left : item.Node.Right;
        await Rediff();
        // Keep the user's place by the *source tag's identity* — paths repeat under KeyedAligner, so
        // matching on Path could jump to a different same-path row. A deletion copy has no source;
        // its row is gone from both sides, so the selection simply stays cleared.
        if (source is not null)
        {
            var again = _root?.Descendants().FirstOrDefault(i => (toRight ? i.Node.Left : i.Node.Right) == source);
            if (again is not null) Go(again);
        }
    }

    private bool CanSave => HasUnsavedEdits && IsSaveable;

    /// <summary>Writes every edited side back to its file or region (a one-time <c>.bak</c> backs the file up first).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save()
    {
        var tags = _tags;
        if (IsBusy || tags is null || _source is not ISaveableDiffSource saver) return Task.CompletedTask;
        bool saveLeft = LeftModified, saveRight = RightModified;

        IsBusy = true;
        ErrorMessage = null;
        int generation = ++_generation;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadCompletion = done.Task;
        Task.Run(() =>
        {
            var errors = new List<string>();
            void SaveSide(bool right, NbtTag? root, string side)
            {
                if (root is null) { errors.Add($"The {side} side is empty; there is nothing to save"); return; }
                var result = saver.Save(right, root);
                if (!result.Ok) errors.Add($"{side}: {result.Failure!.ToDetailedString()}");
            }
            if (saveLeft) SaveSide(right: false, tags.Left, "left");
            if (saveRight) SaveSide(right: true, tags.Right, "right");
            return errors;
        }).ContinueWith(t => _ui.Post(() =>
        {
            try
            {
                if (generation != _generation) return;
                IsBusy = false;
                List<string> errors;
                if (t.Status == TaskStatus.RanToCompletion) errors = t.Result;
                else errors = [$"Save failed: {t.Exception!.GetBaseException().Message}"];
                if (errors.Count > 0)
                {
                    ErrorMessage = string.Join("\n", errors); // flags stay set, so Save can be retried
                    return;
                }
                if (saveLeft) LeftModified = false;
                if (saveRight) RightModified = false;
                SaveStatusText = $"Saved {DateTime.Now:HH:mm:ss}";
            }
            finally
            {
                done.TrySetResult();
            }
        }), TaskScheduler.Default);
        return done.Task;
    }

    [RelayCommand]
    private void Toggle(DiffNodeItem? item)
    {
        if (item is not null) Tree.Toggle(item);
    }

    [RelayCommand]
    private void ExpandAll() => Tree.ExpandAll();

    [RelayCommand]
    private void CollapseAll()
    {
        Tree.CollapseAll();
        if (_root is not null) Tree.Expand(_root);
    }

    [RelayCommand]
    private void ToggleSelected()
    {
        if (SelectedItem is not null) Tree.Toggle(SelectedItem);
    }

    [RelayCommand]
    private void ExpandSelected()
    {
        if (SelectedItem is not null) Tree.Expand(SelectedItem);
    }

    [RelayCommand]
    private void CollapseSelected()
    {
        if (SelectedItem is null) return;
        if (SelectedItem.IsExpanded) Tree.Collapse(SelectedItem);
        else if (SelectedItem.Parent is { } parent && Tree.Rows.Contains(parent)) SelectedItem = parent;
    }
}
