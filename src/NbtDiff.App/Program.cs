using Avalonia;

namespace NbtDiff.App;

internal static class Program
{
    // Avalonia configuration; also used by the previewer, so keep it a plain static method.
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
