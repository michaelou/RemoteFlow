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

    /// <summary>Picks one existing file, for a field that holds a path someone would otherwise have to
    /// type from memory. Returns null when the dialog is dismissed, so a cancelled browse leaves whatever
    /// was already typed alone.</summary>
    Task<string?> PickFileAsync(
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

    public async Task<string?> PickFileAsync(
        string title,
        string? suggestedPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var provider = GetProvider();
        // A file path is the useful suggestion here, but a picker opens at a folder: the dialog starts in
        // the directory the current value names, so browsing from a filled-in field lands beside it.
        IStorageFolder? suggested = null;
        var startDirectory = DirectoryOf(suggestedPath);
        if (startDirectory is not null)
        {
            suggested = await provider.TryGetFolderFromPathAsync(startDirectory);
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggested,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // The value may be a bare command name rather than a path — "bash", "pwsh" — and there is no
            // directory to open for one of those.
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IStorageProvider GetProvider()
    {
        return global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is not null
            ? desktop.MainWindow.StorageProvider
            : throw new InvalidOperationException("A desktop window is required to pick files.");
    }
}
