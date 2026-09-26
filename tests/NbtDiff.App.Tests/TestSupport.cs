using NbtDiff.App.Services;

namespace NbtDiff.App.Tests;

/// <summary>Runs posted work inline; timers are driven manually with <see cref="Tick"/>.</summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    private readonly List<Action> _timers = [];

    public int Posted { get; private set; }

    public void Post(Action action)
    {
        Posted++;
        action();
    }

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        _timers.Add(tick);
        return new Remover(_timers, tick);
    }

    public int ActiveTimers => _timers.Count;

    public void Tick()
    {
        foreach (var t in _timers.ToArray()) t();
    }

    private sealed class Remover(List<Action> timers, Action tick) : IDisposable
    {
        public void Dispose() => timers.Remove(tick);
    }
}

internal sealed class FakeDialogService : IDialogService
{
    public string? NextFolder { get; set; }
    public string? NextOpenFile { get; set; }
    public string? NextSaveFile { get; set; }
    /// <summary>What ConfirmAsync answers; true by default so existing flows are undisturbed.</summary>
    public bool NextConfirm { get; set; } = true;
    public List<string> Requests { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        Requests.Add($"folder:{title}");
        return Task.FromResult(NextFolder);
    }

    public Task<string?> PickOpenFileAsync(string title, string? startPath = null)
    {
        Requests.Add($"file:{title}");
        return Task.FromResult(NextOpenFile);
    }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeLabel)
    {
        Requests.Add($"save:{suggestedName}");
        return Task.FromResult(NextSaveFile);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Requests.Add($"confirm:{title}");
        return Task.FromResult(NextConfirm);
    }
}
