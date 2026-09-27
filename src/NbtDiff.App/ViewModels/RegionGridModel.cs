using CommunityToolkit.Mvvm.ComponentModel;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>One slot of the 32×32 grid. <see cref="Status"/> is null when neither side has a chunk there.</summary>
public sealed partial class ChunkCellItem(int x, int z) : ObservableObject
{
    public int X => x;
    public int Z => z;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPresent), nameof(ToolTipText), nameof(State))] private ChunkDiffStatus? _status;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToolTipText))] private string? _error;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Its state is toggled off in the legend (#11): drawn faint, skipped by keyboard navigation.</summary>
    [ObservableProperty] private bool _isDimmed;

    public bool IsPresent => Status is not null;
    public StateKind State => Status is { } s ? StateKinds.Of(s) : StateKind.Empty;
    /// <summary>Present and not the same on both sides.</summary>
    public bool IsChanged => Status is not null and not ChunkDiffStatus.Same;

    public string ToolTipText => Status switch
    {
        null => $"({X}, {Z}): no chunk on either side",
        ChunkDiffStatus.Error => Error ?? $"({X}, {Z}): error",
        var s => $"({X}, {Z}): {Describe(s.Value)}",
    };

    public static string Describe(ChunkDiffStatus status) => status switch
    {
        ChunkDiffStatus.Same => "same",
        ChunkDiffStatus.Different => "different",
        ChunkDiffStatus.LeftOnly => "only on the left",
        ChunkDiffStatus.RightOnly => "only on the right",
        ChunkDiffStatus.Error => "error",
        _ => status.ToString(),
    };

    public override string ToString() => ToolTipText;
}

/// <summary>Grid state for the region view (DESIGN §5.2): 1024 cells in (z, x) row-major order, counts, and a clamped selection.</summary>
public sealed partial class RegionGridModel : ObservableObject
{
    public const int Size = 32;

    private readonly ChunkCellItem[] _cells = new ChunkCellItem[Size * Size];

    public IReadOnlyList<ChunkCellItem> Cells => _cells;
    public ChunkCellItem this[int x, int z] => _cells[Index(x, z)];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectionText))] private ChunkCellItem? _selected;

    public int Present { get; private set; }
    public int Same { get; private set; }
    public int Different { get; private set; }
    public int LeftOnly { get; private set; }
    public int RightOnly { get; private set; }
    public int Errors { get; private set; }

    // Legend filters (#11): a state toggled off dims its cells without moving anything.
    [ObservableProperty] private bool _showSame = true;
    [ObservableProperty] private bool _showDifferent = true;
    [ObservableProperty] private bool _showLeftOnly = true;
    [ObservableProperty] private bool _showRightOnly = true;
    [ObservableProperty] private bool _showErrors = true;

    partial void OnShowSameChanged(bool value) => ApplyFilter();
    partial void OnShowDifferentChanged(bool value) => ApplyFilter();
    partial void OnShowLeftOnlyChanged(bool value) => ApplyFilter();
    partial void OnShowRightOnlyChanged(bool value) => ApplyFilter();
    partial void OnShowErrorsChanged(bool value) => ApplyFilter();

    private bool IsShown(ChunkDiffStatus status) => status switch
    {
        ChunkDiffStatus.Same => ShowSame,
        ChunkDiffStatus.Different => ShowDifferent,
        ChunkDiffStatus.LeftOnly => ShowLeftOnly,
        ChunkDiffStatus.RightOnly => ShowRightOnly,
        ChunkDiffStatus.Error => ShowErrors,
        _ => true,
    };

    private void ApplyFilter()
    {
        foreach (var c in _cells) c.IsDimmed = c.Status is { } s && !IsShown(s);
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(Present));
        OnPropertyChanged(nameof(Same));
        OnPropertyChanged(nameof(Different));
        OnPropertyChanged(nameof(LeftOnly));
        OnPropertyChanged(nameof(RightOnly));
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(CountsText));
        ApplyFilter();
    }

    /// <summary>Selects the next (or previous) changed chunk in (z, x) order after the selection, skipping dimmed cells. False at the end.</summary>
    public bool StepChanged(int direction)
    {
        int from = Selected is null ? (direction > 0 ? -1 : _cells.Length) : Index(Selected.X, Selected.Z);
        for (int i = from + direction; i >= 0 && i < _cells.Length; i += direction)
        {
            if (_cells[i].IsChanged && !_cells[i].IsDimmed)
            {
                Selected = _cells[i];
                return true;
            }
        }
        return false;
    }

    public RegionGridModel()
    {
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                _cells[Index(x, z)] = new ChunkCellItem(x, z);
    }

    private static int Index(int x, int z)
    {
        if ((uint)x >= Size || (uint)z >= Size) throw new ArgumentOutOfRangeException(x is < 0 or >= Size ? nameof(x) : nameof(z));
        return z * Size + x;
    }

    /// <summary>Replaces every cell's status from a differ result and selects the first occupied cell.</summary>
    public void Apply(IReadOnlyList<ChunkDiffCell> cells)
    {
        foreach (var c in _cells) { c.Status = null; c.Error = null; }
        Present = Same = Different = LeftOnly = RightOnly = Errors = 0;
        foreach (var cell in cells)
        {
            var item = this[cell.X, cell.Z];
            item.Status = cell.Status;
            item.Error = cell.Error;
            Present++;
            switch (cell.Status)
            {
                case ChunkDiffStatus.Same: Same++; break;
                case ChunkDiffStatus.Different: Different++; break;
                case ChunkDiffStatus.LeftOnly: LeftOnly++; break;
                case ChunkDiffStatus.RightOnly: RightOnly++; break;
                case ChunkDiffStatus.Error: Errors++; break;
            }
        }
        RaiseCounts();
        var first = _cells.FirstOrDefault(c => c.Status is not null and not ChunkDiffStatus.Same) ?? _cells.FirstOrDefault(c => c.IsPresent);
        Selected = first;
    }

    public void Clear() => Apply([]);

    /// <summary>Replaces one cell's status (null: no chunk on either side any more) and recounts; the selection stays.</summary>
    public void Update(int x, int z, ChunkDiffCell? cell)
    {
        var item = this[x, z];
        item.Status = cell?.Status;
        item.Error = cell?.Error;
        Present = _cells.Count(c => c.Status is not null);
        Same = _cells.Count(c => c.Status == ChunkDiffStatus.Same);
        Different = _cells.Count(c => c.Status == ChunkDiffStatus.Different);
        LeftOnly = _cells.Count(c => c.Status == ChunkDiffStatus.LeftOnly);
        RightOnly = _cells.Count(c => c.Status == ChunkDiffStatus.RightOnly);
        Errors = _cells.Count(c => c.Status == ChunkDiffStatus.Error);
        RaiseCounts();
    }

    public void Select(int x, int z) => Selected = this[x, z];

    /// <summary>Moves the selection, clamped to the grid; from no selection, lands on (0, 0). Returns true if the selection changed.</summary>
    public bool Move(int dx, int dz)
    {
        int x = Selected?.X ?? 0, z = Selected?.Z ?? 0;
        if (Selected is not null)
        {
            // Step over cells whose state is toggled off; stay put if only dimmed cells lie ahead.
            int nx = x, nz = z;
            do
            {
                nx += dx;
                nz += dz;
                if (nx is < 0 or >= Size || nz is < 0 or >= Size) { nx = x; nz = z; break; }
            } while (this[nx, nz].IsDimmed);
            x = nx;
            z = nz;
        }
        var target = this[x, z];
        if (ReferenceEquals(target, Selected)) return false;
        Selected = target;
        return true;
    }

    partial void OnSelectedChanged(ChunkCellItem? oldValue, ChunkCellItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    public string CountsText
    {
        get
        {
            if (Present == 0) return "no chunks";
            var parts = new List<string>(5);
            if (Different > 0) parts.Add($"{Different} different");
            if (Same > 0) parts.Add($"{Same} same");
            if (LeftOnly > 0) parts.Add($"{LeftOnly} left-only");
            if (RightOnly > 0) parts.Add($"{RightOnly} right-only");
            if (Errors > 0) parts.Add($"{Errors} error");
            return $"{Present} chunk{(Present == 1 ? "" : "s")} · {string.Join(" · ", parts)}";
        }
    }

    public string SelectionText => Selected is null ? "" : Selected.ToolTipText;
}
