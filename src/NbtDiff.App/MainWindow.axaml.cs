using Avalonia;
using Avalonia.Controls;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private ISettingsService? Settings => (DataContext as MainWindowViewModel)?.Settings;

    /// <summary>Restores the saved placement, but only onto a screen that still exists.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Settings?.Current.Window is not { } w) return;
        if (w.IsMaximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        var anchor = new PixelPoint(w.X + 40, w.Y + 40);
        if (Screens.All.Any(s => s.Bounds.Contains(anchor)))
        {
            Position = new PixelPoint(w.X, w.Y);
            Width = w.Width;
            Height = w.Height;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Settings is { } settings)
        {
            bool maximized = WindowState == WindowState.Maximized;
            var previous = settings.Current.Window;
            // Keep the last normal-state size when closing maximized, so un-maximizing later is sane.
            settings.Current.Window = maximized && previous is not null
                ? previous with { IsMaximized = true }
                : new WindowPlacement(Position.X, Position.Y, Width, Height, maximized);
            settings.Save();
        }
        base.OnClosing(e);
    }
}
