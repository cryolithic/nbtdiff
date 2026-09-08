using Avalonia.Threading;

namespace NbtDiff.App.Services;

/// <summary>The UI thread, abstracted so view models run in tests without Avalonia.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
    /// <summary>Starts a repeating timer on the UI thread; dispose to stop.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick());
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
