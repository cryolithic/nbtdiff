using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NbtDiff.Core;

namespace NbtDiff.App.Converters;

/// <summary>Strikethrough for rows that could not be compared.</summary>
public sealed class StatusDecorationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RowStatus.Error ? TextDecorations.Strikethrough : null;

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
