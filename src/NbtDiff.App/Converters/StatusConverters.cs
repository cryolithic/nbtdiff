using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NbtDiff.Core;

namespace NbtDiff.App.Converters;

/// <summary>Row background by status. Translucent so it works over both theme variants.</summary>
public sealed class StatusBackgroundConverter : IValueConverter
{
    private static readonly IBrush Different = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x40, 0x40));
    private static readonly IBrush Probably = new SolidColorBrush(Color.FromArgb(0x22, 0xE0, 0x40, 0x40));
    private static readonly IBrush LeftOnly = new SolidColorBrush(Color.FromArgb(0x38, 0x30, 0x90, 0xF0));
    private static readonly IBrush RightOnly = new SolidColorBrush(Color.FromArgb(0x38, 0xA0, 0x50, 0xE0));
    private static readonly IBrush Error = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowStatus.Different => Different,
        RowStatus.ProbablyDifferent => Probably,
        RowStatus.LeftOnly => LeftOnly,
        RowStatus.RightOnly => RightOnly,
        RowStatus.Error => Error,
        _ => Brushes.Transparent,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Strikethrough for rows that could not be compared.</summary>
public sealed class StatusDecorationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RowStatus.Error ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Dims the glyph of rows that are not final yet.</summary>
public sealed class StatusOpacityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowStatus.ProbablyDifferent => 0.55,
        RowStatus.Pending => 0.4,
        _ => 1.0,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Indents a tree row by its depth.</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public double Step { get; set; } = 16;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int d ? d * Step : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound enum equals the parameter (by name); for radio-style filter buttons.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
