using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Transfers;

namespace RemoteFlow.UI.ViewModels.Storage;

/// <summary>One entry in the Storage page's connection picker. Only object-storage accounts get one.
/// </summary>
public sealed record StorageConnectionChoice(Guid Id, string Name, string Endpoint);

/// <summary>The dual-pane Storage page: the local filesystem on the left, the bucket on the right, and
/// the transfer queue along the bottom.
///
/// <see cref="Transfers"/> is the injected <see cref="TransfersPageViewModel"/> singleton, not a second
/// queue. A second one would mean two independent three-slot gates — six concurrent transfers with
/// neither aware of the other — and a duplicate of 523 tested lines. The sidebar status bar already shows
/// this singleton on every page, so users already experience it as <em>the</em> queue. The accepted
/// consequence is that clearing completed from either surface clears both.</summary>
public sealed partial class StoragePageViewModel : PageViewModel, IAsyncDisposable
{
    private readonly IStorageWorkspaceSessionFactory _sessions;
    private readonly ITransferConflictResolverFactory _conflictResolvers;
    private readonly IConnectionQueryService? _connectionQueries;
    private readonly IConfirmationDialogService _confirmation;
    private StorageWorkspaceSession? _session;
    private Guid? _attachedConnectionId;

    public StoragePageViewModel(
        IStorageWorkspaceSessionFactory sessions,
        IConfirmationDialogService confirmation,
        ITransferConflictResolverFactory conflictResolvers,
        TransfersPageViewModel transfers,
        IConnectionQueryService? connectionQueries = null) : base("Storage")
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _conflictResolvers = conflictResolvers ?? throw new ArgumentNullException(nameof(conflictResolvers));
        Transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _connectionQueries = connectionQueries;

        // One pane class, two instances. The names differ because one control used twice would otherwise
        // give both Refresh buttons the same accessible name and leave a screen-reader user unable to tell
        // the panes apart — a gap no audit catches.
        Local = new FileBrowserPaneViewModel("local folder", "Upload", confirmation, new LocalFileBrowserSource());
        Remote = new FileBrowserPaneViewModel("remote prefix", "Download", confirmation);
        Local.TransferHandler = UploadAsync;
        Remote.TransferHandler = DownloadAsync;
    }

    public FileBrowserPaneViewModel Local { get; }

    public FileBrowserPaneViewModel Remote { get; }

    /// <summary>The application-wide queue, unfiltered, under a header that says "All transfers".</summary>
    public TransfersPageViewModel Transfers { get; }

    public ObservableCollection<StorageConnectionChoice> AvailableConnections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedCommand))]
    public partial StorageConnectionChoice? SelectedConnection { get; set; }

    public bool HasConnectionChoices => AvailableConnections.Count > 0;

    public string NoConnectionMessage => HasConnectionChoices
        ? "Choose an account above and select Connect to browse its objects."
        : "No S3 or Azure Blob connections are saved yet. Create one on the Connections page first.";

    [ObservableProperty]
    public partial string ConnectionTitle { get; private set; } = "Storage";

    [ObservableProperty]
    public partial bool IsConnecting { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public bool IsConnected => _session is not null;

    [RelayCommand]
    public async Task LoadConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_connectionQueries is null)
        {
            return;
        }

        try
        {
            var items = await _connectionQueries.QueryAsync(
                new ConnectionFilter
                {
                    Protocols = [ProtocolType.S3, ProtocolType.AzureBlob],
                    SortBy = ConnectionSortBy.Name,
                },
                cancellationToken).ConfigureAwait(true);
            var previous = SelectedConnection?.Id ?? _attachedConnectionId;
            AvailableConnections.Clear();
            foreach (var item in items)
            {
                AvailableConnections.Add(new StorageConnectionChoice(item.Id, item.Name, item.Host));
            }

            SelectedConnection = AvailableConnections.FirstOrDefault(choice => choice.Id == previous);
            NotifyConnectionChoicesChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The connection list could not be loaded: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectSelected))]
    public Task ConnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        return SelectedConnection is { } choice
            ? AttachAsync(choice.Id, cancellationToken)
            : Task.CompletedTask;
    }

    public async Task AttachAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        IsConnecting = true;
        ErrorMessage = null;
        try
        {
            var next = await _sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            await DisposeSessionAsync().ConfigureAwait(true);
            _session = next;
            _attachedConnectionId = connectionId;
            ConnectionTitle = next.DisplayName;
            SyncPickerWithSession(next.Definition.Id, next.Definition.Name, next.Definition.Host);
            _ = await Remote.AttachAsync(
                new ObjectStorageFileBrowserSource(next.Storage, next.DisplayName, next.RootPath),
                cancellationToken).ConfigureAwait(true);
            if (Local.CurrentPath.Length == 0 && Local.Source is { } local)
            {
                _ = await Local.AttachAsync(local, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "Connecting to the storage account was cancelled.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The storage account could not be opened: {exception.Message}";
        }
        finally
        {
            IsConnecting = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    /// <summary>Opens the local pane at its default root. Called when the page is shown, so the left half
    /// is usable before any account is attached.</summary>
    public Task InitializeLocalAsync(CancellationToken cancellationToken = default)
    {
        return Local.Source is { } source && Local.CurrentPath.Length == 0
            ? Local.AttachAsync(source, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Local selection to the remote pane's current prefix.</summary>
    public Task UploadAsync(CancellationToken cancellationToken = default)
    {
        return TransferAsync(Local, Remote, TransferDirection.Upload, cancellationToken);
    }

    /// <summary>Remote selection to the local pane's current folder.</summary>
    public Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        return TransferAsync(Remote, Local, TransferDirection.Download, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task TransferAsync(
        FileBrowserPaneViewModel from,
        FileBrowserPaneViewModel to,
        TransferDirection direction,
        CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            ErrorMessage = "Connect to a storage account first.";
            return;
        }

        var selected = from.SelectedItems.ToArray();
        if (selected.Length == 0)
        {
            from.FeedbackMessage = "Select something to transfer first.";
            return;
        }

        ErrorMessage = null;

        // Counted and confirmed before a byte moves. A folder can expand to tens of thousands of objects,
        // and "transfer 1 item" and "transfer 41,000 items" are the same drag.
        if (selected.Any(item => item.IsDirectory))
        {
            int count;
            try
            {
                count = await from.CountAsync(selected, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                from.FeedbackMessage = "The transfer was cancelled while counting.";
                return;
            }

            var confirmed = await _confirmation.ConfirmAsync(
                direction == TransferDirection.Upload ? "Upload folder?" : "Download folder?",
                $"This expands to {count:N0} item(s). Continue?",
                direction == TransferDirection.Upload ? "Upload" : "Download",
                cancellationToken).ConfigureAwait(true);
            if (!confirmed)
            {
                from.FeedbackMessage = "The transfer was cancelled.";
                return;
            }
        }

        // One resolver per gesture: the object's lifetime is the batch, which is what makes "apply to all"
        // work without a batch identifier on an Application contract.
        var resolver = _conflictResolvers.Create(selected.Length);
        var engine = new ObjectStorageTransferEngine(_session.Storage, resolver);
        var queued = new List<TransferItemViewModel>();
        foreach (var item in selected)
        {
            var destination = to.Source!.Combine(to.CurrentPath, item.Name);
            var source = item.Path;
            queued.Add(await Transfers.QueueAsync(
                new TransferQueueRequest(
                    direction,
                    source,
                    destination,
                    direction == TransferDirection.Upload
                        ? (progress, token) => engine.UploadAsync(source, destination, progress, token)
                        : (progress, token) => engine.DownloadAsync(source, destination, progress, token)),
                cancellationToken).ConfigureAwait(true));
        }

        var results = await Task.WhenAll(queued.Select(item => item.Completion)).ConfigureAwait(true);
        var failed = results.FirstOrDefault(result => !result.IsSuccess);
        if (failed is not null)
        {
            ErrorMessage = DescribeFailure(failed, direction);
        }
        else
        {
            from.FeedbackMessage = $"{queued.Count} item(s) transferred to {to.CurrentPath}.";
        }

        await to.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    private static string DescribeFailure(TransferResult result, TransferDirection direction)
    {
        var failure = result.Items.FirstOrDefault(item => item.Failure is not null)?.Failure;
        var verb = direction == TransferDirection.Upload ? "upload" : "download";
        return failure?.Error == SftpError.PermissionDenied
            ? $"The {verb} was denied. Check the access key's permissions on this bucket."
            : failure?.Message ?? $"The {verb} did not complete.";
    }

    private bool CanConnectSelected()
    {
        return SelectedConnection is not null;
    }

    private void NotifyConnectionChoicesChanged()
    {
        OnPropertyChanged(nameof(HasConnectionChoices));
        OnPropertyChanged(nameof(NoConnectionMessage));
    }

    private void SyncPickerWithSession(Guid connectionId, string name, string endpoint)
    {
        var match = AvailableConnections.FirstOrDefault(choice => choice.Id == connectionId);
        if (match is null)
        {
            match = new StorageConnectionChoice(connectionId, name, endpoint);
            AvailableConnections.Add(match);
            NotifyConnectionChoicesChanged();
        }

        SelectedConnection = match;
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is not null)
        {
            Remote.Detach();
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
