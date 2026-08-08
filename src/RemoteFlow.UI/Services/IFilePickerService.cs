using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace RemoteFlow.UI.Services;

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default);

    Task<string?> PickDownloadFolderAsync(string? suggestedPath = null, CancellationToken cancellationToken = default);
}

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default)
    {
        var provider = GetProvider();
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload files",
            AllowMultiple = true,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return [.. files.Select(item => item.TryGetLocalPath()).Where(path => path is not null).Cast<string>()];
    }

    public async Task<string?> PickDownloadFolderAsync(
        string? suggestedPath = null,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider();
        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            suggested = await provider.TryGetFolderFromPathAsync(suggestedPath);
        }
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose download folder",
            AllowMultiple = false,
            SuggestedStartLocation = suggested,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static IStorageProvider GetProvider()
    {
        return global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is not null
            ? desktop.MainWindow.StorageProvider
            : throw new InvalidOperationException("A desktop window is required to pick files.");
    }
}
