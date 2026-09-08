using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NbtDiff.App.Services;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow? window = null;
            var dialogs = new AvaloniaDialogService(() => window);
            var shell = new MainWindowViewModel(dialogs, AvaloniaUiDispatcher.Instance);
            window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            shell.Start(desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
