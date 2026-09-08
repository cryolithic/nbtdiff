using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NbtDiff.App.Tree;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

public enum RowFilter
{
    All,
    /// <summary>Different, probably different, or one-sided. Errors and pending rows are hidden.</summary>
    Differences,
    Same,
    /// <summary>Left-only and right-only.</summary>
    Orphans,
    /// <summary>Unreadable on either side.</summary>
    Errors,
}

/// <summary>
/// Observable wrapper over a <see cref="CompareRow"/>: the scan mutates the row on worker threads, the
/// UI calls <see cref="Refresh"/> on this item (via the coalescer) and every bound cell updates.
/// One item carries both the left and right columns, so both panes bind to the same list.
/// </summary>
public sealed partial class CompareRowItem : ObservableObject, IFlatTreeNode<CompareRowItem>
{
    private readonly List<CompareRowItem> _children = [];

    public CompareRow Row { get; }
    public CompareRowItem? Parent { get; }
    public IReadOnlyList<CompareRowItem> Children => _children;

    public string Name => Row.Name;
    public string RelativePath => Row.RelativePath;
    public FileKind Kind => Row.Kind;
    public bool IsDirectory => Row.IsDirectory;
    public bool HasLeft => Row.Left is not null;
    public bool HasRight => Row.Right is not null;
    public string? LeftSizeText => Row.Left is { } l && !IsDirectory ? FormatSize(l.Size) : null;
    public string? RightSizeText => Row.Right is { } r && !IsDirectory ? FormatSize(r.Size) : null;
    public string? LeftModifiedText => Row.Left is { } l && !IsDirectory ? FormatDate(l.Modified) : null;
    public string? RightModifiedText => Row.Right is { } r && !IsDirectory ? FormatDate(r.Modified) : null;

    // Live state, re-read from the row on Refresh().
    public RowStatus Status => Row.Status;
    public string? Error => Row.Error;
    public string StatusGlyph => Glyph(Row.Status);
    public string? CountsText => IsDirectory ? DescribeCounts(Row.Counts) : null;
    public string? ToolTipText => Row.Error ?? (IsDirectory ? CountsText : null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    private bool _hasVisibleChildren;

    public int Depth { get; set; }

    public string ExpanderGlyph => !HasVisibleChildren ? "" : IsExpanded ? "▾" : "▸";

    private CompareRowItem(CompareRow row, CompareRowItem? parent)
    {
        Row = row;
        Parent = parent;
    }

    /// <summary>Builds the item tree and an index from row to item.</summary>
    public static (CompareRowItem Root, Dictionary<CompareRow, CompareRowItem> Index) Build(CompareRow root)
    {
        var index = new Dictionary<CompareRow, CompareRowItem>(ReferenceEqualityComparer.Instance);
        var item = Build(root, null, index);
        return (item, index);
    }

    private static CompareRowItem Build(CompareRow row, CompareRowItem? parent, Dictionary<CompareRow, CompareRowItem> index)
    {
        var item = new CompareRowItem(row, parent);
        index[row] = item;
        foreach (var child in row.Children)
            item._children.Add(Build(child, item, index));
        return item;
    }

    /// <summary>Re-reads status, error and counts from the underlying row.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    public static bool Matches(RowFilter filter, RowStatus status) => filter switch
    {
        RowFilter.All => true,
        RowFilter.Differences => status is RowStatus.Different or RowStatus.ProbablyDifferent or RowStatus.LeftOnly or RowStatus.RightOnly,
        RowFilter.Same => status == RowStatus.Same,
        RowFilter.Orphans => status is RowStatus.LeftOnly or RowStatus.RightOnly,
        RowFilter.Errors => status == RowStatus.Error,
        _ => true,
    };

    public static string Glyph(RowStatus status) => status switch
    {
        RowStatus.Pending => "…",
        RowStatus.Same => "=",
        RowStatus.ProbablyDifferent => "≠?",
        RowStatus.Different => "≠",
        RowStatus.LeftOnly => "◀",
        RowStatus.RightOnly => "▶",
        RowStatus.Error => "!",
        _ => "?",
    };

    public static string DescribeCounts(RowCounts c)
    {
        var parts = new List<string>(6);
        if (c.Different > 0) parts.Add($"{c.Different} differ");
        if (c.ProbablyDifferent > 0) parts.Add($"{c.ProbablyDifferent} probably differ");
        if (c.Same > 0) parts.Add($"{c.Same} same");
        if (c.LeftOnly > 0) parts.Add($"{c.LeftOnly} left-only");
        if (c.RightOnly > 0) parts.Add($"{c.RightOnly} right-only");
        if (c.Error > 0) parts.Add($"{c.Error} error");
        if (c.Pending > 0) parts.Add($"{c.Pending} pending");
        return parts.Count == 0 ? "empty" : string.Join(" · ", parts);
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    private static string FormatDate(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public override string ToString() => Row.ToString();
}
