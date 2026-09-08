using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Views;

public partial class RegionCompareView : UserControl
{
    public RegionCompareView()
    {
        InitializeComponent();
        Cells.AddHandler(PointerPressedEvent, OnCellPressed, RoutingStrategies.Bubble);
        Cells.AddHandler(DoubleTappedEvent, OnCellDoubleTapped, RoutingStrategies.Bubble);
        GridHost.KeyDown += OnKeyDown;
        Loaded += (_, _) => GridHost.Focus();
    }

    private RegionCompareViewModel? Vm => DataContext as RegionCompareViewModel;

    private static ChunkCellItem? CellOf(RoutedEventArgs e) => (e.Source as Control)?.DataContext as ChunkCellItem;

    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CellOf(e) is { } cell)
        {
            Vm?.SelectCommand.Execute(cell);
            GridHost.Focus();
        }
    }

    private void OnCellDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CellOf(e) is { } cell)
        {
            Vm?.SelectCommand.Execute(cell);
            Vm?.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        string? direction = e.Key switch
        {
            Key.Left => "left",
            Key.Right => "right",
            Key.Up => "up",
            Key.Down => "down",
            _ => null,
        };
        if (direction is not null)
        {
            vm.MoveCommand.Execute(direction);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            vm.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
