using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed class ConnectionEditorViewModelFactory(
    IConnectionService connections,
    IConnectionRepository connectionRepository,
    IConnectionCredentialService credentials,
    IFolderRepository folders,
    ITagRepository tags,
    ITagService tagService,
    IRecentConnectionStore recent,
    ISystemTerminalLauncher? systemTerminalLauncher = null,
    ISshKeyService? sshKeyService = null,
    IClipboardService? clipboard = null,
    ISettingsStore? settings = null)
{
    public async Task<ConnectionEditorViewModel> CreateEditorAsync(
        Guid? connectionId,
        CancellationToken cancellationToken = default)
    {
        var editor = new ConnectionEditorViewModel(
            connections,
            connectionRepository,
            credentials,
            folders,
            tags,
            tagService,
            sshKeyService,
            clipboard,
            settings);
        await editor.InitializeAsync(connectionId, cancellationToken).ConfigureAwait(true);
        return editor;
    }

    public async Task<ConnectionDetailsViewModel> CreateDetailsAsync(
        Guid connectionId,
        Func<ConnectionOpenMode, Task> open,
        Func<Task> edit,
        Func<Task> duplicate,
        Func<Task> delete,
        CancellationToken cancellationToken = default)
    {
        var connection = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(true)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        var folderPath = connection.FolderId is { } folderId
            ? (await folders.GetByIdAsync(folderId, cancellationToken).ConfigureAwait(true))?.Path ?? "Unknown folder"
            : "No folder";
        var allTags = await tags.ListAsync(cancellationToken).ConfigureAwait(true);
        var tagIds = connection.Tags.Select(tag => tag.TagId).ToHashSet();
        var tagNames = allTags
            .Where(tag => tagIds.Contains(tag.Id))
            .Select(tag => tag.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recentItem = await recent.GetAsync(connectionId, cancellationToken).ConfigureAwait(true);
        return new ConnectionDetailsViewModel(
            connection,
            folderPath,
            tagNames,
            recentItem?.LastOpenedUtc,
            open,
            edit,
            duplicate,
            delete,
            systemTerminalLauncher is null
                ? null
                : () => systemTerminalLauncher.OpenSshAsync(connection));
    }
}
