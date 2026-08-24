using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace RemoteFlow.UI.Services;

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default);

    Task<string?> PickDownloadFolderAsync(string? suggestedPath = null, CancellationToken cancellationToken = default);

    /// <summary>Picks a folder for a caller that gets to say what the dialog is for. The download-specific
    /// method above is now one call into this, so a native dialog never asks about downloads when the user
    /// is choosing somewhere to keep backups.</summary>
    Task<string?> PickFolderAsync(
        string title,
        string? suggestedPath = null,
        CancellationToken cancellationToken = default);
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

    public Task<string?> PickDownloadFolderAsync(
        string? suggestedPath = null,
        CancellationToken cancellationToken = default)
    {
        return PickFolderAsync("Choose download folder", suggestedPath, cancellationToken);
    }

    public async Task<string?> PickFolderAsync(
        string title,
        string? suggestedPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var provider = GetProvider();
        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(suggestedPath))
        {
            suggested = await provider.TryGetFolderFromPathAsync(suggestedPath);
        }
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
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
