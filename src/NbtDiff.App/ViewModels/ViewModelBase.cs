using CommunityToolkit.Mvvm.ComponentModel;

namespace NbtDiff.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Shown in the window title.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// Where the view was opened from inside a folder compare (a relative path such as
    /// <c>region/r.0.0.mca</c>); the breadcrumb shows its folders and file name. Null otherwise.
    /// </summary>
    public string? CrumbPath { get; set; }

    /// <summary>This view's breadcrumb labels (#13): its folders and file name, or its title.</summary>
    public virtual IReadOnlyList<string> CrumbLabels =>
        CrumbPath is { Length: > 0 } p ? p.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries) : [Title];

    /// <summary>Muted text after the last crumb (e.g. a region's chunk range); null for none.</summary>
    public virtual string? CrumbNote => null;
}

/// <summary>One breadcrumb: its label, the navigation level it belongs to, and whether it is the view being shown.</summary>
public sealed record Crumb(string Label, int Level, bool IsCurrent, bool IsFirst);

/// <summary>Stand-in view for things not built yet, and for start-up errors.</summary>
public sealed class PlaceholderViewModel(string title, string message, bool isError = false) : ViewModelBase
{
    public override string Title => title;
    public string Message => message;
    public bool IsError => isError;
}
