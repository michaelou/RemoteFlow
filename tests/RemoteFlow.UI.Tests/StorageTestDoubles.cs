using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Storage;
using RemoteFlow.UI.ViewModels.Transfers;

namespace RemoteFlow.UI.Tests;

/// <summary>Everything the Storage page needs that would otherwise be a window, a cloud account or a
/// dispatcher. Shared between the workspace tests, the conflict-resolver tests and the session-opener
/// tests, so all three drive the same page the application composes.</summary>
internal static class StorageTestDoubles
{
    public static Connection StorageConnection(string name = "Objects", string? container = "media")
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            name,
            "s3.eu-west-2.amazonaws.com",
            ProtocolType.S3,
            DateTimeOffset.UnixEpoch).Value;
        var options = ObjectStorageOptions.Default();
        _ = options.Configure(region: "eu-west-2", container: container);
        return connection.SetOptions(
            connection.Ssh,
            connection.Sftp,
            connection.Rdp,
            options,
            SystemGuidProvider.Instance,
            DateTimeOffset.UnixEpoch);
    }

    public static TransfersPageViewModel Transfers()
    {
        return new TransfersPageViewModel(new InlineDispatcher(), new StubReveal());
    }

    public static StorageFixture CreateFixture(
        InMemoryObjectStorage? storage = null,
        bool confirmationResult = true,
        StorageConflictDefault conflictDefault = StorageConflictDefault.Overwrite,
        IConnectionQueryService? connectionQueries = null)
    {
        var connection = StorageConnection();
        var store = storage ?? new InMemoryObjectStorage();
        store.AddContainer("media");
        var session = new StorageWorkspaceSession(connection, store);
        var confirmation = new RecordingConfirmation(confirmationResult);
        var settings = new InMemorySettingsStore();
        settings.Set(SettingKeys.StorageConflictDefault, conflictDefault, CancellationToken.None)
            .GetAwaiter().GetResult();
        var dialogs = new RecordingConflictDialog();
        var transfers = Transfers();
        var page = new StoragePageViewModel(
            new StubStorageSessionFactory(session),
            confirmation,
            new TransferConflictResolverFactory(dialogs, settings),
            transfers,
            connectionQueries);
        return new StorageFixture(connection, store, page, transfers, confirmation, dialogs, settings);
    }

    internal sealed record StorageFixture(
        Connection Connection,
        InMemoryObjectStorage Storage,
        StoragePageViewModel Page,
        TransfersPageViewModel Transfers,
        RecordingConfirmation Confirmation,
        RecordingConflictDialog Dialogs,
        InMemorySettingsStore Settings) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Page.DisposeAsync();
            Transfers.Dispose();
        }
    }

    internal sealed class StubStorageSessionFactory(StorageWorkspaceSession session)
        : IStorageWorkspaceSessionFactory
    {
        public int OpenCount { get; private set; }

        public Task<StorageWorkspaceSession> OpenAsync(
            Guid connectionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(session);
        }
    }

    internal sealed class RecordingConfirmation(bool result) : IConfirmationDialogService
    {
        public List<string> Messages { get; } = [];

        public bool Result { get; set; } = result;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
        }
    }

    internal sealed class RecordingConflictDialog : ITransferConflictDialogService
    {
        public int ShowCount { get; private set; }

        public List<bool> OfferedApplyToAll { get; } = [];

        public TransferConflictDecision Decision { get; set; } = TransferConflictDecision.Overwrite;

        public bool ApplyToAll { get; set; }

        public Task<TransferConflictChoice> ShowAsync(
            TransferConflict conflict,
            bool offerApplyToAll,
            CancellationToken cancellationToken = default)
        {
            ShowCount++;
            OfferedApplyToAll.Add(offerApplyToAll);
            return Task.FromResult(new TransferConflictChoice(Decision, ApplyToAll));
        }
    }

    internal sealed class InlineDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class StubReveal : IFileRevealService
    {
        public Task<FileRevealResult> RevealAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(FileRevealResult.Success);
        }
    }

    internal sealed class StubConnectionQueries(IReadOnlyList<ConnectionListItem> items) : IConnectionQueryService
    {
        public ConnectionFilter? LastFilter { get; private set; }

        public Task<IReadOnlyList<ConnectionListItem>> QueryAsync(
            ConnectionFilter filter,
            CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            return Task.FromResult(items);
        }

        public Task<IReadOnlyList<ConnectionListItem>> SearchPaletteAsync(
            string text,
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(items);
        }
    }
}
