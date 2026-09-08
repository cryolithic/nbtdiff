using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Views;

public partial class FolderCompareView : UserControl
{
    public FolderCompareView()
    {
        InitializeComponent();
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.KeyDown += OnGridKeyDown;
        // The view is rebuilt when the user comes Back from a file/region view while the view model
        // (and its SelectedRow) survives; put the row back on screen and give the grid the keyboard so
        // the next row is one arrow key away.
        Loaded += (_, _) => Dispatcher.UIThread.Post(RestoreSelection, DispatcherPriority.Background);
    }

    private void RestoreSelection()
    {
        if (Vm?.SelectedRow is { } row && Grid.ItemsSource is not null)
        {
            Grid.SelectedItem = row;
            Grid.ScrollIntoView(row, null);
        }
        Grid.Focus();
    }

    private FolderCompareViewModel? Vm => DataContext as FolderCompareViewModel;

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-clicks on the header.
        if (e.Source is Visual v && v.FindAncestorOfType<DataGridColumnHeader>() is not null) return;
        Vm?.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.ExpandSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                vm.CollapseSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
