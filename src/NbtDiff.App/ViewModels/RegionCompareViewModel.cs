using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NbtDiff.App.Services;
using NbtDiff.Core;
using NbtDiff.Nbt;

namespace NbtDiff.App.ViewModels;

/// <summary>
/// Chunk grid of one region file pair (DESIGN §5.2). Region files are opened and diffed off the UI
/// thread and stay open (chunk views read from them) until the view model is disposed, which the
/// shell does when it is popped.
/// </summary>
public sealed partial class RegionCompareViewModel : ViewModelBase, IDisposable
{
    private readonly string? _leftPath;
    private readonly string? _rightPath;
    private readonly FileFingerprint? _leftFp;
    private readonly FileFingerprint? _rightFp;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsService? _settings;
    private readonly SaveGate? _saveGate;
    private RegionFile? _left;
    private RegionFile? _right;
    private int _generation;

    public override string Title => FileDiffSource.PairTitle(_leftPath, _rightPath);

    /// <summary>The world chunk range this region covers, from its file name (<c>r.-1.1.mca</c> → x −32…−1, z 32…63).</summary>
    public override string? CrumbNote =>
        RegionCoords.FromFileName(_leftPath ?? _rightPath ?? "") is { } r
            ? $"chunks x {Signed(r.X * 32)}…{Signed(r.X * 32 + 31)}, z {Signed(r.Z * 32)}…{Signed(r.Z * 32 + 31)}"
            : null;

    internal static string Signed(int v) => v < 0 ? "−" + (-v).ToString(System.Globalization.CultureInfo.InvariantCulture) : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public RegionGridModel Grid { get; } = new();
    public string LeftPathText => _leftPath ?? "(missing)";
    public string RightPathText => _rightPath ?? "(missing)";
    public bool IsDisposed { get; private set; }
    /// <summary>Completes after the diff has been applied to <see cref="Grid"/>. For tests.</summary>
    public Task? LoadCompletion { get; private set; }

    /// <summary>Raised with the chunk view to show when the user opens a cell. The shell pushes it.</summary>
    public event Action<ViewModelBase>? NavigationRequested;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;
    [ObservableProperty] private string _headerText = "";

    public bool HasError => ErrorMessage is not null;

    public RegionCompareViewModel(string? leftPath, string? rightPath, FileFingerprint? leftFp, FileFingerprint? rightFp, IUiDispatcher ui, ISettingsService? settings = null, SaveGate? saveGate = null)
    {
        _leftPath = leftPath is not null && File.Exists(leftPath) ? leftPath : null;
        _rightPath = rightPath is not null && File.Exists(rightPath) ? rightPath : null;
        _leftFp = leftFp;
        _rightFp = rightFp;
        _ui = ui;
        _settings = settings;
        _saveGate = saveGate;
    }

    public Task Load()
    {
        IsBusy = true;
        ErrorMessage = null;
        int generation = ++_generation;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadCompletion = done.Task;
        string? leftPath = _leftPath, rightPath = _rightPath;
        Task.Run(() =>
        {
            RegionFile? left = null, right = null;
            IReadOnlyList<ChunkDiffCell>? cells = null;
            string? error = null;
            try
            {
                if (leftPath is not null)
                {
                    var r = RegionFile.Open(leftPath);
                    if (!r.Ok) error = $"Left: {r.Failure!.ToDetailedString()}";
                    else left = r.Value;
                }
                if (error is null && rightPath is not null)
                {
                    var r = RegionFile.Open(rightPath);
                    if (!r.Ok) error = $"Right: {r.Failure!.ToDetailedString()}";
                    else right = r.Value;
                }
                if (error is null && left is null && right is null)
                    error = "Neither region file exists";
                if (error is null)
                    cells = RegionDiffer.Diff(left, right, (_settings?.Current ?? new AppSettings()).ToDiffOptions(), _leftFp, _rightFp);
            }
            catch (Exception e)
            {
                error = $"Compare failed: {e.Message}";
            }

            _ui.Post(() =>
            {
                try
                {
                    if (generation != _generation || IsDisposed)
                    {
                        left?.Dispose();
                        right?.Dispose();
                        return;
                    }
                    _left?.Dispose();
                    _right?.Dispose();
                    _left = left;
                    _right = right;
                    IsBusy = false;
                    if (error is not null)
                    {
                        ErrorMessage = error;
                        Grid.Clear();
                        HeaderText = "";
                        return;
                    }
                    Grid.Apply(cells!);
                    HeaderText = Grid.CountsText + Explain(cells!);
                }
                finally
                {
                    done.TrySetResult();
                }
            });
        });
        return done.Task;
    }

    /// <summary>
    /// When the folder scan flagged this file but no chunk differs, say why, so a "different" row
    /// that opens to an all-green grid is not a mystery: unverified byte differences (Deep verify off
    /// or scan still in pass 2) are the expected case; a verified mismatch with no differing chunk is not.
    /// </summary>
    private string Explain(IReadOnlyList<ChunkDiffCell> cells)
    {
        if (_leftFp is null || _rightFp is null || _leftFp.ContentEquals(_rightFp)) return "";
        if (cells.Any(c => c.Status != ChunkDiffStatus.Same)) return "";
        bool verified = _leftFp.Tier == FingerprintTier.Deep && _rightFp.Tier == FingerprintTier.Deep;
        return verified
            ? " · the scan marked this file different but no chunk differs — please report this"
            : " · bytes differ (recompression, sector layout or timestamps) but every chunk's content matches";
    }

    private bool CanOpenSelected => Grid.Selected is { IsPresent: true };

    /// <summary>Opens the tag diff of the selected chunk pair; no-op on an empty slot.</summary>
    [RelayCommand]
    private void OpenSelected()
    {
        var cell = Grid.Selected;
        if (cell is not { IsPresent: true } || IsDisposed) return;
        var vm = new FileCompareViewModel(new ChunkDiffSource(_left, _right, cell.X, cell.Z, Title), _ui, _settings, ChangedChunkNavigation(), _saveGate);
        vm.Saved += OnChunkSaved;
        _ = vm.Load();
        NavigationRequested?.Invoke(vm);
    }

    /// <summary>Every present, non-Same cell in (z, x) order — what Next/Previous chunk step through; the grid selection follows.</summary>
    private ChunkNavigation ChangedChunkNavigation()
    {
        var changed = Grid.Cells.Where(c => c.IsPresent && c.Status != ChunkDiffStatus.Same).Select(c => (c.X, c.Z)).ToList();
        return new ChunkNavigation(changed, (x, z) => new ChunkDiffSource(_left, _right, x, z, Title), (x, z) => Grid.Select(x, z));
    }

    /// <summary>A chunk view saved into these regions: re-check that slot so the grid is not stale when the user comes Back.</summary>
    private void OnChunkSaved(FileCompareViewModel chunk)
    {
        if (chunk.Source is not ChunkDiffSource source || IsDisposed) return;
        var options = (_settings?.Current ?? new AppSettings()).ToDiffOptions();
        Grid.Update(source.X, source.Z, RegionDiffer.DiffCell(_left, _right, source.X, source.Z, options));
        HeaderText = Grid.CountsText;
    }

    [RelayCommand]
    private void Select(ChunkCellItem? cell)
    {
        if (cell is not null) Grid.Selected = cell;
    }

    [RelayCommand]
    private void Move(string direction)
    {
        switch (direction)
        {
            case "left": Grid.Move(-1, 0); break;
            case "right": Grid.Move(1, 0); break;
            case "up": Grid.Move(0, -1); break;
            case "down": Grid.Move(0, 1); break;
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _generation++;
        _left?.Dispose();
        _right?.Dispose();
        _left = _right = null;
    }
}
