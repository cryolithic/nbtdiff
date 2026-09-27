using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Converters;

/// <summary>
/// Resolves a <see cref="StateKind"/> (fill; parameter "Tint" or "Rollup" for the row variants) or a
/// <see cref="RowTint"/> to the brush from Themes/State.axaml for the current theme variant.
/// <see cref="StateKind.None"/> and unknown values are transparent.
/// </summary>
public sealed class StateBrushConverter : IValueConverter
{
    public static readonly StateBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowTint { Kind: not StateKind.None } t => Lookup($"State.{t.Kind}.{(t.Rollup ? "Rollup" : "Tint")}"),
        StateKind k and not StateKind.None => Lookup(parameter is string { Length: > 0 } variant ? $"State.{k}.{variant}" : $"State.{k}"),
        _ => Brushes.Transparent,
    };

    private static IBrush Lookup(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
