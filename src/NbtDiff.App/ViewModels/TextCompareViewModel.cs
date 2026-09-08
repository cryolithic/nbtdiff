using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NbtDiff.App.Services;
using NbtDiff.Core.Diff;

namespace NbtDiff.App.ViewModels;

/// <summary>One row of the side-by-side text view.</summary>
public sealed class TextDiffRowItem(LineDiffRow row, int index, bool isHunkStart)
{
    public LineDiffRow Row => row;
    public int Index => index;
    public bool IsHunkStart => isHunkStart;
    public LineDiffKind Kind => row.Kind;
    public string LeftLineText => row.LeftLine?.ToString() ?? "";
    public string RightLineText => row.RightLine?.ToString() ?? "";
    public string LeftText => row.LeftText ?? "";
    public string RightText => row.RightText ?? "";
    public bool HasLeft => row.LeftLine is not null;
    public bool HasRight => row.RightLine is not null;
    public string StatusGlyph => row.Kind switch
    {
        LineDiffKind.Changed => "≠",
        LineDiffKind.Removed => "−",
        LineDiffKind.Added => "+",
        _ => "",
    };
}

/// <summary>
/// Line diff of two text files (JSON, properties, logs…). Files are read and diffed off the UI
/// thread. F7/F8 move between hunks with wrap-around, like the tag view's next/previous change.
/// </summary>
public sealed partial class TextCompareViewModel : ViewModelBase
{
    /// <summary>Larger files are refused rather than rendered as hundreds of thousands of rows.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    private readonly string? _leftPath;
    private readonly string? _rightPath;
    private readonly IUiDispatcher _ui;
    private IReadOnlyList<int> _hunkStarts = [];

    public override string Title => FileDiffSource.PairTitle(_leftPath, _rightPath);
    public string LeftPathText => _leftPath ?? "(missing)";
    public string RightPathText => _rightPath ?? "(missing)";
    public LineDiffResult? Result { get; private set; }
    /// <summary>Completes after the diff has been applied. For tests.</summary>
    public Task? LoadCompletion { get; private set; }

    [ObservableProperty] private IReadOnlyList<TextDiffRowItem> _rows = [];
    [ObservableProperty] private TextDiffRowItem? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;
    [ObservableProperty] private string _summaryText = "";

    public bool HasError => ErrorMessage is not null;
    public bool HasResult => Result is not null;

    public TextCompareViewModel(string? leftPath, string? rightPath, IUiDispatcher ui)
    {
        _leftPath = leftPath is not null && File.Exists(leftPath) ? leftPath : null;
        _rightPath = rightPath is not null && File.Exists(rightPath) ? rightPath : null;
        _ui = ui;
    }

    public Task Load()
    {
        IsBusy = true;
        ErrorMessage = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadCompletion = done.Task;
        string? leftPath = _leftPath, rightPath = _rightPath;
        Task.Run(() =>
        {
            LineDiffResult? result = null;
            string? error = null;
            try
            {
                if (leftPath is null && rightPath is null)
                    error = "Neither file exists";
                else
                    result = LineDiffer.Diff(ReadText(leftPath), ReadText(rightPath));
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            _ui.Post(() =>
            {
                try
                {
                    IsBusy = false;
                    if (error is not null)
                    {
                        ErrorMessage = error;
                        return;
                    }
                    Present(result!);
                }
                finally
                {
                    done.TrySetResult();
                }
            });
        });
        return done.Task;
    }

    private static string ReadText(string? path)
    {
        if (path is null) return "";
        long length = new FileInfo(path).Length;
        if (length > MaxBytes)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is {length / (1024 * 1024)} MB; the text view handles files up to {MaxBytes / (1024 * 1024)} MB");
        return File.ReadAllText(path);
    }

    private void Present(LineDiffResult result)
    {
        Result = result;
        _hunkStarts = result.HunkStarts;
        var starts = new HashSet<int>(result.HunkStarts);
        Rows = result.Rows.Select((r, i) => new TextDiffRowItem(r, i, starts.Contains(i))).ToList();
        SelectedRow = null;
        OnPropertyChanged(nameof(HasResult));
        SummaryText = Summarize(result);
    }

    private static string Summarize(LineDiffResult r)
    {
        if (!r.HasChanges) return $"No differences · {r.Rows.Count} lines";
        var parts = new List<string>();
        if (r.Changed > 0) parts.Add($"{r.Changed} changed");
        if (r.Removed > 0) parts.Add($"{r.Removed} left-only");
        if (r.Added > 0) parts.Add($"{r.Added} right-only");
        return $"{r.HunkStarts.Count} hunk{(r.HunkStarts.Count == 1 ? "" : "s")} · {string.Join(" · ", parts)}";
    }

    [RelayCommand]
    private void NextHunk()
    {
        if (_hunkStarts.Count == 0) return;
        int from = SelectedRow?.Index ?? -1;
        int target = _hunkStarts.FirstOrDefault(h => h > from, _hunkStarts[0]);
        SelectedRow = Rows[target];
    }

    [RelayCommand]
    private void PreviousHunk()
    {
        if (_hunkStarts.Count == 0) return;
        int from = SelectedRow?.Index ?? int.MaxValue;
        int target = _hunkStarts.LastOrDefault(h => h < from, _hunkStarts[^1]);
        SelectedRow = Rows[target];
    }
}
