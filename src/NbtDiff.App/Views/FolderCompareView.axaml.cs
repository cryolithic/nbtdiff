using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
