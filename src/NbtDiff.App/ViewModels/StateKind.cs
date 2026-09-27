using NbtDiff.Core;
using NbtDiff.Core.Diff;

namespace NbtDiff.App.ViewModels;

/// <summary>
/// The one state vocabulary every view colours by (#9): red = different, blue = left only, purple =
/// right only, green = same, amber = error. <see cref="None"/> means "no colour" (an untinted row).
/// Brushes live in Themes/State.axaml as <c>State.{Kind}</c> (fill), <c>State.{Kind}.Tint</c> (row
/// background) and <c>State.{Kind}.Rollup</c> (a lighter tint for a container whose change is inside it).
/// </summary>
public enum StateKind
{
    None,
    Same,
    Different,
    ProbablyDifferent,
    LeftOnly,
    RightOnly,
    Moved,
    Error,
    Pending,
    Empty,
}

public static class StateKinds
{
    public static StateKind Of(RowStatus status) => status switch
    {
        RowStatus.Same => StateKind.Same,
        RowStatus.Different => StateKind.Different,
        RowStatus.ProbablyDifferent => StateKind.ProbablyDifferent,
        RowStatus.LeftOnly => StateKind.LeftOnly,
        RowStatus.RightOnly => StateKind.RightOnly,
        RowStatus.Error => StateKind.Error,
        RowStatus.Pending => StateKind.Pending,
        _ => StateKind.None,
    };

    public static StateKind Of(DiffKind kind) => kind switch
    {
        DiffKind.Unchanged => StateKind.Same,
        DiffKind.ValueChanged or DiffKind.TypeChanged => StateKind.Different,
        DiffKind.Removed => StateKind.LeftOnly,
        DiffKind.Added => StateKind.RightOnly,
        DiffKind.Moved or DiffKind.Renamed => StateKind.Moved,
        _ => StateKind.None,
    };

    public static StateKind Of(ChunkDiffStatus status) => status switch
    {
        ChunkDiffStatus.Same => StateKind.Same,
        ChunkDiffStatus.Different => StateKind.Different,
        ChunkDiffStatus.LeftOnly => StateKind.LeftOnly,
        ChunkDiffStatus.RightOnly => StateKind.RightOnly,
        ChunkDiffStatus.Error => StateKind.Error,
        _ => StateKind.Empty,
    };

    public static StateKind Of(LineDiffKind kind) => kind switch
    {
        LineDiffKind.Changed => StateKind.Different,
        LineDiffKind.Removed => StateKind.LeftOnly,
        LineDiffKind.Added => StateKind.RightOnly,
        _ => StateKind.None,
    };
}

/// <summary>A row's background: a state's tint, or its lighter roll-up tint for a collapsed container with changes inside.</summary>
public readonly record struct RowTint(StateKind Kind, bool Rollup = false)
{
    public static readonly RowTint None = new(StateKind.None);
}
