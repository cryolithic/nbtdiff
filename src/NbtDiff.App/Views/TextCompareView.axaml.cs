using Avalonia.Controls;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Views;

public partial class TextCompareView : UserControl
{
    public TextCompareView()
    {
        InitializeComponent();
        Grid.SelectionChanged += OnGridSelectionChanged;
        // F7/F8 key bindings only fire while focus is inside this view.
        Loaded += (_, _) => Grid.Focus();
    }

    // Next/previous hunk set SelectedRow from the view model; keep the row on screen.
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is TextDiffRowItem item)
            Grid.ScrollIntoView(item, null);
    }
}
