using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NbtDiff.Core;

namespace NbtDiff.App.Converters;

/// <summary>Row background by diff kind. Same hues as the folder view: red = changed, blue = left only, purple = right only.</summary>
public sealed class DiffKindBackgroundConverter : IValueConverter
{
    private static readonly IBrush Changed = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
    private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
    private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));
    private static readonly IBrush Moved = new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xB0, 0x30));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffKind.ValueChanged or DiffKind.TypeChanged => Changed,
        DiffKind.Removed => Removed,
        DiffKind.Added => Added,
        DiffKind.Moved or DiffKind.Renamed => Moved,
        _ => Brushes.Transparent,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Chunk cell fill by status; an empty slot is nearly invisible.</summary>
public sealed class ChunkStatusBrushConverter : IValueConverter
{
    private static readonly IBrush Empty = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80));
    private static readonly IBrush Same = new SolidColorBrush(Color.FromArgb(0x60, 0x60, 0xB0, 0x60));
    private static readonly IBrush Different = new SolidColorBrush(Color.FromArgb(0xE0, 0xE0, 0x40, 0x40));
    private static readonly IBrush LeftOnly = new SolidColorBrush(Color.FromArgb(0xD0, 0x30, 0x90, 0xF0));
    private static readonly IBrush RightOnly = new SolidColorBrush(Color.FromArgb(0xD0, 0xA0, 0x50, 0xE0));
    private static readonly IBrush Error = new SolidColorBrush(Color.FromArgb(0xE0, 0xF0, 0xA0, 0x20));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ChunkDiffStatus.Same => Same,
        ChunkDiffStatus.Different => Different,
        ChunkDiffStatus.LeftOnly => LeftOnly,
        ChunkDiffStatus.RightOnly => RightOnly,
        ChunkDiffStatus.Error => Error,
        _ => Empty,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Text view row background by line diff kind, same hues as the tag view.</summary>
public sealed class LineDiffKindBackgroundConverter : IValueConverter
{
    private static readonly IBrush Changed = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
    private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
    private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NbtDiff.Core.Diff.LineDiffKind.Changed => Changed,
        NbtDiff.Core.Diff.LineDiffKind.Removed => Removed,
        NbtDiff.Core.Diff.LineDiffKind.Added => Added,
        _ => Brushes.Transparent,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Highlights array-detail rows whose elements differ.</summary>
public sealed class DifferentRowBackgroundConverter : IValueConverter
{
    private static readonly IBrush Different = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Different : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
