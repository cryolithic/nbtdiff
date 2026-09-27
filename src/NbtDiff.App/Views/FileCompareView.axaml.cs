using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Views;

public partial class FileCompareView : UserControl
{
    public FileCompareView()
    {
        InitializeComponent();
        Grid.DoubleTapped += OnGridDoubleTapped;
        // Tunnel: DataGrid handles Enter/Left/Right itself (row and column moves) before a bubbling
        // handler would see them.
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.SelectionChanged += OnGridSelectionChanged;
        // F7/F8 key bindings only fire while focus is inside this view.
        Loaded += (_, _) => Grid.Focus();
        CopyPath.Click += async (_, _) =>
        {
            if (Vm?.SelectedItem is { } item && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(item.NbtPath);
        };
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
        // Modified keys belong to the view's KeyBindings (Alt+Left/Right copy).
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
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
