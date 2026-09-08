using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>Boxed <see cref="ChunkDiffStatus"/> values for the region view's legend swatches (XAML needs a static source per swatch).</summary>
public static class LegendStatus
{
    public static readonly object Same = ChunkDiffStatus.Same;
    public static readonly object Different = ChunkDiffStatus.Different;
    public static readonly object LeftOnly = ChunkDiffStatus.LeftOnly;
    public static readonly object RightOnly = ChunkDiffStatus.RightOnly;
    public static readonly object Error = ChunkDiffStatus.Error;
}
