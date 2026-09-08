using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace NbtDiff.App.Services;

/// <summary>File/folder pickers, abstracted so view models run in tests.</summary>
public interface IDialogService
{
    /// <summary>Local path of the chosen folder, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startPath = null);

    /// <summary>Local path to save to, or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeLabel);
}

/// <summary>Uses the window's <see cref="IStorageProvider"/>. Resolve the window lazily: it does not exist when the view models are built.</summary>
public sealed class AvaloniaDialogService(Func<TopLevel?> topLevel) : IDialogService
{
    public async Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        var provider = topLevel()?.StorageProvider;
        if (provider is null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (startPath is not null && Directory.Exists(startPath))
            options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(startPath);
        var result = await provider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeLabel)
    {
        var provider = topLevel()?.StorageProvider;
        if (provider is null) return null;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(typeLabel) { Patterns = [$"*.{extension}"] }],
        });
        return file?.TryGetLocalPath();
    }
}
