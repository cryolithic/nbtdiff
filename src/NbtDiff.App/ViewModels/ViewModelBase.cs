using CommunityToolkit.Mvvm.ComponentModel;

namespace NbtDiff.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Shown in the window header.</summary>
    public abstract string Title { get; }
}

/// <summary>Stand-in view for things not built yet, and for start-up errors.</summary>
public sealed class PlaceholderViewModel(string title, string message, bool isError = false) : ViewModelBase
{
    public override string Title => title;
    public string Message => message;
    public bool IsError => isError;
}
