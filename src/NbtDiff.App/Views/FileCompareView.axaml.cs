using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Views;

public partial class FileCompareView : UserControl
{
    public FileCompareView()
    {
        InitializeComponent();
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.KeyDown += OnGridKeyDown;
        Grid.SelectionChanged += OnGridSelectionChanged;
        // F7/F8 key bindings only fire while focus is inside this view.
        Loaded += (_, _) => Grid.Focus();
    }

    private FileCompareViewModel? Vm => DataContext as FileCompareViewModel;

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<DataGridColumnHeader>() is not null) return;
        Vm?.ToggleSelectedCommand.Execute(null);
        e.Handled = true;
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.ToggleSelectedCommand.Execute(null);
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

    // Next/previous change set SelectedItem from the view model; keep the row on screen.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is { } item)
            Grid.ScrollIntoView(item, null);
    }
}
