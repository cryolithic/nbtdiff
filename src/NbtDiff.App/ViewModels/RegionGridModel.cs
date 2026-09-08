using CommunityToolkit.Mvvm.ComponentModel;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>One slot of the 32×32 grid. <see cref="Status"/> is null when neither side has a chunk there.</summary>
public sealed partial class ChunkCellItem(int x, int z) : ObservableObject
{
    public int X => x;
    public int Z => z;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPresent), nameof(ToolTipText))] private ChunkDiffStatus? _status;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ToolTipText))] private string? _error;
    [ObservableProperty] private bool _isSelected;

    public bool IsPresent => Status is not null;

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
        OnPropertyChanged(nameof(CountsText));
        var first = _cells.FirstOrDefault(c => c.Status is not null and not ChunkDiffStatus.Same) ?? _cells.FirstOrDefault(c => c.IsPresent);
        Selected = first;
    }

    public void Clear() => Apply([]);

    public void Select(int x, int z) => Selected = this[x, z];

    /// <summary>Moves the selection, clamped to the grid; from no selection, lands on (0, 0). Returns true if the selection changed.</summary>
    public bool Move(int dx, int dz)
    {
        int x = Selected?.X ?? 0, z = Selected?.Z ?? 0;
        if (Selected is not null)
        {
            x = Math.Clamp(x + dx, 0, Size - 1);
            z = Math.Clamp(z + dz, 0, Size - 1);
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
