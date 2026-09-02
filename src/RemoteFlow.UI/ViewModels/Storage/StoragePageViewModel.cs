using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
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
        IConnectionQueryService? connectionQueries = null,
        ILocalFolderMemory? folderMemory = null) : base("Storage")
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _conflictResolvers = conflictResolvers ?? throw new ArgumentNullException(nameof(conflictResolvers));
        Transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _connectionQueries = connectionQueries;

        // One pane class, two instances. The names differ because one control used twice would otherwise
        // give both Refresh buttons the same accessible name and leave a screen-reader user unable to tell
        // the panes apart — a gap no audit catches.
        // Only the local pane gets the folder memory: the remote pane's root is pinned by the connection.
        Local = new FileBrowserPaneViewModel(
            "local folder",
            "Upload",
            confirmation,
            new LocalFileBrowserSource(),
            folderMemory);
        Remote = new FileBrowserPaneViewModel("remote prefix", "Download", confirmation);
        Local.TransferHandler = UploadToAsync;
        Remote.TransferHandler = DownloadToAsync;

        // Files dragged in from the file manager are accepted by the remote pane only. On the local pane
        // the dropped file is already on this machine, and copying it beside itself is not what the
        // gesture means — so that drag is declined visibly rather than ending in nothing.
        Remote.ExternalFilesHandler = UploadDroppedPathsAsync;
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

    /// <summary>Opens the local pane where it was last left, or at its default root the first time. Called
    /// when the page is shown, so the left half is usable before any account is attached.</summary>
    public Task InitializeLocalAsync(CancellationToken cancellationToken = default)
    {
        return Local.Source is { } source && Local.CurrentPath.Length == 0
            ? Local.AttachAsync(source, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Local selection to the remote pane's current prefix.</summary>
    public Task UploadAsync(CancellationToken cancellationToken = default)
    {
        return UploadToAsync(null, cancellationToken);
    }

    /// <summary>Local selection to the prefix a drop landed on. Null means the prefix the remote pane is
    /// showing, which is what the Upload button and the row menu mean.</summary>
    public Task UploadToAsync(string? destination, CancellationToken cancellationToken = default)
    {
        return TransferSelectionAsync(Local, Remote, TransferDirection.Upload, destination, cancellationToken);
    }

    /// <summary>Remote selection to the local pane's current folder.</summary>
    public Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        return DownloadToAsync(null, cancellationToken);
    }

    /// <summary>Remote selection to the folder a drop landed on, or the local pane's current folder.
    /// </summary>
    public Task DownloadToAsync(string? destination, CancellationToken cancellationToken = default)
    {
        return TransferSelectionAsync(Remote, Local, TransferDirection.Download, destination, cancellationToken);
    }

    /// <summary>Files dragged onto the remote pane from outside the application — the file manager, the
    /// desktop, an attachment — and uploaded to the prefix the pointer was released over.
    ///
    /// The drag carries paths and nothing else: these rows were never listed by the local pane, so they are
    /// described here and then take the identical route a pane-to-pane upload takes, counting, conflict
    /// resolution and the one shared queue included.</summary>
    public async Task UploadDroppedPathsAsync(
        IReadOnlyList<string> paths,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (RequireSession() is not { } session)
        {
            return;
        }

        // A path that has been moved or deleted since the drag began is skipped rather than failing the
        // whole drop, and a drag carrying nothing local at all — a browser image, a mail attachment the
        // sender never spooled to disk — says so instead of appearing to have worked.
        var items = paths
            .Select(LocalFileBrowserSource.TryDescribe)
            .OfType<FileBrowserEntry>()
            .Select(entry => new FileBrowserItemViewModel(entry, "Upload"))
            .ToArray();
        if (items.Length == 0)
        {
            Remote.FeedbackMessage = "Nothing in that drop is a file or folder on this computer.";
            return;
        }

        await RunTransferAsync(
            session,
            Local,
            Remote,
            items,
            destination,
            TransferDirection.Upload,
            Remote,
            cancellationToken).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>The selection in one pane to the other, which is what the transfer buttons, the row menus
    /// and a pane-to-pane drag all come down to.</summary>
    private async Task TransferSelectionAsync(
        FileBrowserPaneViewModel from,
        FileBrowserPaneViewModel to,
        TransferDirection direction,
        string? destination,
        CancellationToken cancellationToken)
    {
        if (RequireSession() is not { } session)
        {
            return;
        }

        var selected = from.SelectedItems.ToArray();
        if (selected.Length == 0)
        {
            from.FeedbackMessage = "Select something to transfer first.";
            return;
        }

        await RunTransferAsync(
            session,
            from,
            to,
            selected,
            destination,
            direction,
            from,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Everything a transfer does once what is moving and where it lands are both known: count,
    /// confirm, one conflict resolver for the batch, and the one shared queue.
    ///
    /// <paramref name="from"/> is only asked to expand a folder, so it serves a drop from the file manager
    /// as well as its own rows. <paramref name="destination"/> is the folder a drop landed on, or null for
    /// wherever <paramref name="to"/> is pointed. <paramref name="feedback"/> is the pane the message
    /// belongs on, which is the pane the gesture started in for a selection and the pane it was dropped on
    /// for a drag from outside.</summary>
    private async Task RunTransferAsync(
        StorageWorkspaceSession session,
        FileBrowserPaneViewModel from,
        FileBrowserPaneViewModel to,
        FileBrowserItemViewModel[] items,
        string? destination,
        TransferDirection direction,
        FileBrowserPaneViewModel feedback,
        CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        var folder = destination ?? to.CurrentPath;

        // Counted and confirmed before a byte moves. A folder can expand to tens of thousands of objects,
        // and "transfer 1 item" and "transfer 41,000 items" are the same drag.
        if (Array.Exists(items, item => item.IsDirectory))
        {
            int count;
            try
            {
                count = await from.CountAsync(items, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                feedback.FeedbackMessage = "The transfer was cancelled while counting.";
                return;
            }

            var confirmed = await _confirmation.ConfirmAsync(
                direction == TransferDirection.Upload ? "Upload folder?" : "Download folder?",
                $"This expands to {count:N0} item(s). Continue?",
                direction == TransferDirection.Upload ? "Upload" : "Download",
                cancellationToken).ConfigureAwait(true);
            if (!confirmed)
            {
                feedback.FeedbackMessage = "The transfer was cancelled.";
                return;
            }
        }

        // One resolver per gesture: the object's lifetime is the batch, which is what makes "apply to all"
        // work without a batch identifier on an Application contract.
        var resolver = _conflictResolvers.Create(items.Length);
        var engine = new ObjectStorageTransferEngine(session.Storage, resolver);
        var queued = new List<TransferItemViewModel>();
        foreach (var item in items)
        {
            var destinationPath = to.Source!.Combine(folder, item.Name);
            var source = item.Path;
            queued.Add(await Transfers.QueueAsync(
                new TransferQueueRequest(
                    direction,
                    source,
                    destinationPath,
                    direction == TransferDirection.Upload
                        ? (progress, token) => engine.UploadAsync(source, destinationPath, progress, token)
                        : (progress, token) => engine.DownloadAsync(source, destinationPath, progress, token)),
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
            feedback.FeedbackMessage = $"{queued.Count} item(s) transferred to {folder}.";
        }

        await to.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The attached session, or null with the banner already set. Every transfer needs it, and
    /// "Connect to a storage account first" is the same answer for all of them.</summary>
    private StorageWorkspaceSession? RequireSession()
    {
        if (_session is null)
        {
            ErrorMessage = "Connect to a storage account first.";
        }

        return _session;
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
