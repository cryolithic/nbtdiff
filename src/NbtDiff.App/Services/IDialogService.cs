using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace NbtDiff.App.Services;

/// <summary>File/folder pickers and confirmations, abstracted so view models run in tests.</summary>
public interface IDialogService
{
    /// <summary>Local path of the chosen folder, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startPath = null);

    /// <summary>Local path of the chosen file, or null if cancelled.</summary>
    Task<string?> PickOpenFileAsync(string title, string? startPath = null);

    /// <summary>Local path to save to, or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string typeLabel);

    /// <summary>Modal question; true means proceed. False when no window is up to ask in.</summary>
    Task<bool> ConfirmAsync(string title, string message);
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

    public async Task<string?> PickOpenFileAsync(string title, string? startPath = null)
    {
        var provider = topLevel()?.StorageProvider;
        if (provider is null) return null;
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("All files") { Patterns = ["*.*"] },
                new FilePickerFileType("NBT files") { Patterns = ["*.dat", "*.mca", "*.mcr", "*.nbt", "*.snbt"] },
            ],
        };
        if (startPath is not null)
        {
            string? dir = Path.GetDirectoryName(startPath);
            if (dir is not null && Directory.Exists(dir))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(dir);
        }
        var result = await provider.OpenFilePickerAsync(options);
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

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (topLevel() is not Window owner) return false;
        bool proceed = false;

        // Built in code rather than XAML so the service stays self-contained; Escape cancels.
        var dialog = new Window
        {
            Title = title,
            ShowInTaskbar = false,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button Button(string caption, bool result)
        {
            var button = new Button { Content = caption, MinWidth = 88 };
            if (result) button.Classes.Add("accent");
            button.Click += (_, _) => { proceed = result; dialog.Close(); };
            return button;
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { Button("Cancel", false), Button("Discard", true) },
                },
            },
        };
        dialog.AddHandler(InputElement.KeyDownEvent, (_, _) => dialog.Close(), RoutingStrategies.Tunnel);

        await dialog.ShowDialog(owner);
        return proceed;
    }
}
