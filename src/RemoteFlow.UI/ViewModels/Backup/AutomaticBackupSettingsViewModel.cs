using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Backup;

public sealed record AutoBackupDestinationOption(
    AutoBackupDestinationKind Value,
    string DisplayName,
    string Description);

public sealed record AutoBackupConnectionChoice(
    Guid Id,
    string Name,
    string Host,
    int Port,
    ProtocolType Protocol,
    string? FolderPath)
{
    /// <summary>Two connections can easily share a name in different folders, so the folder is part of what
    /// distinguishes them in the list.</summary>
    public string Detail => FolderPath is null
        ? $"{Protocol.GetDisplayName()} · {Host}:{Port}"
        : $"{Protocol.GetDisplayName()} · {Host}:{Port} · {FolderPath}";
}

public sealed partial class AutomaticBackupSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IFilePickerService _filePicker;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConnectionQueryService _connectionQueries;
    private readonly IAutoBackupPassphraseStore _passphrases;
    private readonly IAutoBackupRunner _runner;
    private readonly IVaultUnlockService? _vaultUnlock;
    private bool _initialized;
    private bool _loading;
    private bool _disposed;
    private Task _pendingSave = Task.CompletedTask;
#pragma warning disable IDE0032 // Backing field is explicit because the setter clamps.
    private int _retainedCopies = AutoBackupOptions.DefaultRetainedCopies;
#pragma warning restore IDE0032

    public AutomaticBackupSettingsViewModel(
        ISettingsStore settings,
        IFilePickerService filePicker,
        IUiDispatcher dispatcher,
        IConnectionQueryService connectionQueries,
        IAutoBackupPassphraseStore passphrases,
        IAutoBackupRunner runner,
        IVaultUnlockService? vaultUnlock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _connectionQueries = connectionQueries ?? throw new ArgumentNullException(nameof(connectionQueries));
        _passphrases = passphrases ?? throw new ArgumentNullException(nameof(passphrases));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _vaultUnlock = vaultUnlock;
        _runner.StatusChanged += OnStatusChanged;
    }

    public IReadOnlyList<AutoBackupDestinationOption> DestinationKinds { get; } =
    [
        new(AutoBackupDestinationKind.LocalFolder, "Local folder",
            "A folder on this computer or a mounted drive."),
        new(AutoBackupDestinationKind.SftpConnection, "SFTP connection",
            "An SSH or SFTP connection you have already saved."),
        new(AutoBackupDestinationKind.ObjectStorageConnection, "Object storage connection",
            "An S3 or Azure Blob connection you have already saved."),
    ];

    public ObservableCollection<AutoBackupConnectionChoice> AvailableConnections { get; } = [];

    public bool IsAvailable => _passphrases.IsAvailable;

    public static string UnavailableMessage =>
        "Automatic backup needs somewhere to keep its passphrase, and no credential store is available on " +
        "this machine. Manual export from the Export tab still works.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnable))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalDestination))]
    [NotifyPropertyChangedFor(nameof(IsConnectionDestination))]
    [NotifyPropertyChangedFor(nameof(IsObjectStorage))]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    [NotifyPropertyChangedFor(nameof(NoConnectionMessage))]
    [NotifyPropertyChangedFor(nameof(RemotePathLabel))]
    [NotifyPropertyChangedFor(nameof(RemotePathWatermark))]
    [NotifyPropertyChangedFor(nameof(RemotePathHint))]
    [NotifyPropertyChangedFor(nameof(DestinationSummary))]
    public partial AutoBackupDestinationOption? SelectedDestinationKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationSummary))]
    public partial string LocalFolder { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationSummary))]
    public partial AutoBackupConnectionChoice? SelectedConnection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationSummary))]
    public partial string RemotePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PassphraseStatus))]
    [NotifyPropertyChangedFor(nameof(PassphraseWarning))]
    public partial string CredentialStoreName { get; private set; } = "your system credential store";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PassphraseStatus))]
    [NotifyPropertyChangedFor(nameof(EditPassphraseLabel))]
    [NotifyPropertyChangedFor(nameof(CanEnable))]
    [NotifyCanExecuteChangedFor(nameof(ClearPassphraseCommand))]
    public partial bool HasStoredPassphrase { get; private set; }

    /// <summary>Set when the credential store itself will not open — a locked vault, a refused keyring.
    /// Different from having no passphrase, and not fixed by typing one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PassphraseStatus))]
    [NotifyPropertyChangedFor(nameof(CanEditPassphrase))]
    [NotifyPropertyChangedFor(nameof(CanUnlockVault))]
    [NotifyPropertyChangedFor(nameof(CanEnable))]
    [NotifyCanExecuteChangedFor(nameof(EditPassphraseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnlockVaultCommand))]
    public partial string? PassphraseStoreProblem { get; private set; }

    [ObservableProperty]
    public partial bool IsEditingPassphrase { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePassphraseCommand))]
    public partial string NewPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePassphraseCommand))]
    public partial string ConfirmPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? PassphraseMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnable))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    public partial string? ValidationMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool HasLastRun { get; private set; }

    [ObservableProperty]
    public partial bool LastRunSucceeded { get; private set; }

    [ObservableProperty]
    public partial bool LastRunFailed { get; private set; }

    [ObservableProperty]
    public partial bool LastRunBlocked { get; private set; }

    [ObservableProperty]
    public partial string LastRunHeadline { get; private set; } = "No automatic backup has run yet.";

    [ObservableProperty]
    public partial string LastRunDestination { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastRunMessage { get; private set; } = string.Empty;

    /// <summary>Clamped in the setter rather than validated afterwards, so a typed or spun value can never
    /// reach the pruner out of range. Same treatment as the terminal font size and scrollback.</summary>
    public int RetainedCopies
    {
        get => _retainedCopies;
        set
        {
            var clamped = Math.Clamp(
                value, AutoBackupOptions.MinimumRetainedCopies, AutoBackupOptions.MaximumRetainedCopies);
            if (SetProperty(ref _retainedCopies, clamped))
            {
                OnPropertyChanged(nameof(RetentionHint));
                OnConfigurationChanged();
            }
        }
    }

    public bool IsLocalDestination =>
        SelectedDestinationKind?.Value is not (AutoBackupDestinationKind.SftpConnection
            or AutoBackupDestinationKind.ObjectStorageConnection);

    public bool IsConnectionDestination => !IsLocalDestination;

    public bool IsObjectStorage =>
        SelectedDestinationKind?.Value == AutoBackupDestinationKind.ObjectStorageConnection;

    public bool HasConnectionChoices => AvailableConnections.Count > 0;

    public bool HasValidationMessage => ValidationMessage is not null;

    public string ConnectionLabel => IsObjectStorage ? "Object storage connection" : "SFTP connection";

    public string NoConnectionMessage => IsObjectStorage
        ? "No S3 or Azure Blob connections are saved yet. Create one on the Connections page first."
        : "No SSH or SFTP connections are saved yet. Create one on the Connections page first.";

    public string RemotePathLabel => IsObjectStorage ? "Bucket and prefix" : "Remote directory";

    public string RemotePathWatermark => IsObjectStorage
        ? "media-backups/remoteflow"
        : "/srv/backups/remoteflow";

    public string RemotePathHint => IsObjectStorage
        ? "The bucket or container first, then an optional prefix. RemoteFlow creates the prefix if it is missing."
        : "An absolute path. RemoteFlow creates the directory if it is missing.";

    public string RetentionHint =>
        $"Keeps the {RetainedCopies} newest archives at the destination. Older ones are deleted after each " +
        "successful run. Files RemoteFlow did not write are never touched.";

    /// <summary>Reads back what the machine understood, in the same shape the last-run line prints, so the
    /// two can never disagree about where backups are going.</summary>
    public string DestinationSummary
    {
        get
        {
            if (IsLocalDestination)
            {
                return string.IsNullOrWhiteSpace(LocalFolder) ? "No folder chosen yet." : LocalFolder;
            }

            if (SelectedConnection is null)
            {
                return "No connection chosen yet.";
            }

            var path = string.IsNullOrWhiteSpace(RemotePath) ? string.Empty : RemotePath.Trim();
            return IsObjectStorage
                ? $"{(SelectedConnection.Protocol == ProtocolType.S3 ? "s3" : "azure")}://{path.TrimStart('/')}"
                : $"sftp://{SelectedConnection.Host}:{SelectedConnection.Port}/{path.TrimStart('/')}";
        }
    }

    public string PassphraseWarning =>
        "Automatic backups always include your saved credentials, encrypted with this passphrase. It is kept " +
        $"in {CredentialStoreName} and never in RemoteFlow's database. If you lose it, the credentials in " +
        "those archives cannot be recovered, and changing it does not re-encrypt archives already written.";

    public string PassphraseStatus => PassphraseStoreProblem is not null
        ? $"{CredentialStoreName} could not be opened: {PassphraseStoreProblem} Automatic backup stays off " +
            "until it can be read."
        : HasStoredPassphrase
            ? $"A passphrase is stored in {CredentialStoreName}."
            : "No passphrase is set.";

    /// <summary>There is no point offering the passphrase boxes when the store that would hold one will not
    /// open — saving would fail the same way reading did.</summary>
    public bool CanEditPassphrase => PassphraseStoreProblem is null;

    /// <summary>Offered when the store is shut and there is something that can ask for the way in. Declining
    /// the prompt at startup should not mean restarting RemoteFlow to change your mind.</summary>
    public bool CanUnlockVault => PassphraseStoreProblem is not null && _vaultUnlock is not null;

    public string EditPassphraseLabel => HasStoredPassphrase ? "Change passphrase" : "Set passphrase";

    /// <summary>Automatic backup cannot be switched on until it could actually run. The runner never trusts
    /// this — the flag can arrive from an imported archive — but the user should not be able to arm a
    /// configuration that is going to report Blocked the moment they edit anything.</summary>
    public bool CanEnable => HasStoredPassphrase && PassphraseStoreProblem is null && ValidationMessage is null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !IsAvailable)
        {
            return;
        }

        _initialized = true;
        _loading = true;
        try
        {
            CredentialStoreName = await _passphrases.GetProviderNameAsync(cancellationToken).ConfigureAwait(true);
            await RefreshPassphraseStateAsync(cancellationToken).ConfigureAwait(true);
            var options = await _settings.Get(SettingKeys.AutoBackup, cancellationToken).ConfigureAwait(true)
                ?? AutoBackupOptions.Disabled;
            SelectedDestinationKind = DestinationKinds.FirstOrDefault(
                option => option.Value == options.Destination.Kind) ?? DestinationKinds[0];
            LocalFolder = options.Destination.LocalFolder ?? string.Empty;
            RemotePath = options.Destination.RemotePath ?? string.Empty;
            RetainedCopies = options.ClampedRetainedCopies;
            IsEnabled = options.IsEnabled;
            await LoadConnectionsAsync(options.Destination.ConnectionId, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _loading = false;
        }

        RefreshStatus();
        Validate();
    }

    public Task FlushAsync()
    {
        return _pendingSave;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runner.StatusChanged -= OnStatusChanged;
    }

    [RelayCommand]
    private async Task BrowseForFolderAsync(CancellationToken cancellationToken)
    {
        var chosen = await _filePicker
            .PickFolderAsync("Choose a folder for automatic backups", NullIfBlank(LocalFolder), cancellationToken)
            .ConfigureAwait(true);
        if (chosen is not null)
        {
            LocalFolder = chosen;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePassphrase))]
    private async Task SavePassphraseAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(NewPassphrase, ConfirmPassphrase, StringComparison.Ordinal))
        {
            PassphraseMessage = "The two passphrases do not match.";
            return;
        }

        var buffer = NewPassphrase.ToCharArray();
        try
        {
            var result = await _passphrases.SetAsync(buffer, cancellationToken).ConfigureAwait(true);
            if (result.IsFailure)
            {
                PassphraseMessage = result.Error.Message;
                // The store may have gone unusable since the page loaded, which changes what to show.
                await RefreshPassphraseStateAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            PassphraseStoreProblem = null;
            HasStoredPassphrase = true;
            IsEditingPassphrase = false;
            PassphraseMessage = "Passphrase saved.";
            Validate();
        }
        finally
        {
            // The bound strings are cleared too: leaving them in place would keep the passphrase in a
            // managed string that cannot be wiped.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
            NewPassphrase = string.Empty;
            ConfirmPassphrase = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnlockVault))]
    private async Task UnlockVaultAsync(CancellationToken cancellationToken)
    {
        var status = await _vaultUnlock!.EnsureUnlockedAsync(cancellationToken).ConfigureAwait(true);
        await RefreshPassphraseStateAsync(cancellationToken).ConfigureAwait(true);
        PassphraseMessage = status.IsUsable ? null : status.Problem;
        Validate();
    }

    private async Task RefreshPassphraseStateAsync(CancellationToken cancellationToken)
    {
        var state = await _passphrases.InspectAsync(cancellationToken).ConfigureAwait(true);
        PassphraseStoreProblem = state.Problem;
        HasStoredPassphrase = state.HasPassphrase;
    }

    [RelayCommand(CanExecute = nameof(CanEditPassphrase))]
    private void EditPassphrase()
    {
        IsEditingPassphrase = true;
        PassphraseMessage = null;
    }

    [RelayCommand]
    private void CancelPassphraseEdit()
    {
        IsEditingPassphrase = false;
        NewPassphrase = string.Empty;
        ConfirmPassphrase = string.Empty;
        PassphraseMessage = null;
    }

    [RelayCommand(CanExecute = nameof(HasStoredPassphrase))]
    private async Task ClearPassphraseAsync(CancellationToken cancellationToken)
    {
        await _passphrases.ClearAsync(cancellationToken).ConfigureAwait(true);
        HasStoredPassphrase = false;
        // Turning automatic backup off as well, rather than leaving it armed and permanently blocked.
        IsEnabled = false;
        PassphraseMessage = "Passphrase cleared. Automatic backup is off until you set a new one.";
        Validate();
    }

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private async Task RunNowAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        try
        {
            await FlushAsync().ConfigureAwait(true);
            _ = await _runner.RunNowAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            RefreshStatus();
        }
    }

    private bool CanSavePassphrase =>
        NewPassphrase.Length > 0 && ConfirmPassphrase.Length > 0;

    private bool CanRunNow => !IsRunning && ValidationMessage is null && HasStoredPassphrase;

    private async Task LoadConnectionsAsync(Guid? selectedId, CancellationToken cancellationToken)
    {
        AvailableConnections.Clear();
        if (IsLocalDestination)
        {
            OnPropertyChanged(nameof(HasConnectionChoices));
            return;
        }

        // Filtered at the query, so a connection that could never receive a backup is not merely rejected
        // later — it is never offered.
        ProtocolType[] protocols = IsObjectStorage
            ? [ProtocolType.S3, ProtocolType.AzureBlob]
            : [ProtocolType.Ssh, ProtocolType.Sftp];
        try
        {
            var items = await _connectionQueries.QueryAsync(
                new ConnectionFilter { Protocols = protocols, SortBy = ConnectionSortBy.Name },
                cancellationToken).ConfigureAwait(true);
            foreach (var item in items)
            {
                AvailableConnections.Add(new AutoBackupConnectionChoice(
                    item.Id, item.Name, item.Host, item.Port, item.Protocol, item.FolderPath));
            }

            SelectedConnection = AvailableConnections.FirstOrDefault(choice => choice.Id == selectedId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigating away mid-load.
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            ValidationMessage = $"The connection list could not be loaded: {exception.Message}";
        }

        OnPropertyChanged(nameof(HasConnectionChoices));
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        // The run finishes on whichever thread it happened to be on, so this has to come back to the UI one.
        _ = MarshalRefreshAsync();
    }

    private async Task MarshalRefreshAsync()
    {
        await _dispatcher.InvokeAsync(RefreshStatus).ConfigureAwait(false);
    }

    private void RefreshStatus()
    {
        var status = _runner.LastStatus;
        HasLastRun = status is not null;
        if (status is null)
        {
            LastRunSucceeded = false;
            LastRunFailed = false;
            LastRunBlocked = false;
            LastRunHeadline = "No automatic backup has run yet.";
            LastRunDestination = string.Empty;
            LastRunMessage = string.Empty;
            return;
        }

        LastRunSucceeded = status.Outcome == AutoBackupOutcome.Succeeded;
        LastRunFailed = status.Outcome == AutoBackupOutcome.Failed;
        LastRunBlocked = status.Outcome == AutoBackupOutcome.Blocked;
        LastRunHeadline = status.Outcome switch
        {
            AutoBackupOutcome.Succeeded => $"Last backup succeeded {Describe(status.RunUtc)}.",
            AutoBackupOutcome.Failed => $"Last backup failed {Describe(status.RunUtc)}.",
            AutoBackupOutcome.Blocked => "Automatic backup is waiting on something.",
            _ => "Automatic backup has not reported an outcome.",
        };
        LastRunDestination = status.Destination;
        LastRunMessage = status.Message;
    }

    private static string Describe(DateTimeOffset runUtc)
    {
        var elapsed = DateTimeOffset.UtcNow - runUtc;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} minutes ago",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} hours ago",
            { TotalDays: < 30 } => $"{(int)elapsed.TotalDays} days ago",
            _ => $"on {runUtc.ToLocalTime():d}",
        };
    }

    private void Validate()
    {
        ValidationMessage = BuildValidationMessage();
        if (IsEnabled && ValidationMessage is not null)
        {
            // Never leave the toggle on over a configuration that cannot run.
            IsEnabled = false;
        }
    }

    /// <summary>The first thing that is missing, phrased as what to do about it. Null means the
    /// configuration would actually run.</summary>
    private string? BuildValidationMessage()
    {
        return this switch
        {
            { PassphraseStoreProblem: not null } =>
                $"{CredentialStoreName} could not be opened, so automatic backup cannot encrypt anything.",
            { HasStoredPassphrase: false } =>
                "Set an encryption passphrase before turning automatic backup on.",
            { IsLocalDestination: true, LocalFolder: var folder } when string.IsNullOrWhiteSpace(folder) =>
                "Choose a folder to keep automatic backups in.",
            { IsLocalDestination: true, LocalFolder: var folder } when !Path.IsPathRooted(folder) =>
                "Enter a full path to the backup folder.",
            { IsLocalDestination: true } => null,
            { SelectedConnection: null } when AvailableConnections.Count == 0 => NoConnectionMessage,
            { SelectedConnection: null } =>
                "Choose the connection automatic backups should be sent to.",
            { RemotePath: var path } when string.IsNullOrWhiteSpace(path) =>
                $"Enter the {RemotePathLabel.ToLowerInvariant()} automatic backups should be written to.",
            _ => null,
        };
    }

    private void OnConfigurationChanged()
    {
        if (_loading || !_initialized)
        {
            return;
        }

        Validate();
        _pendingSave = SaveAsync(_pendingSave, BuildOptions());
    }

    private AutoBackupOptions BuildOptions()
    {
        return new AutoBackupOptions
        {
            IsEnabled = IsEnabled,
            RetainedCopies = RetainedCopies,
            Destination = new AutoBackupDestination
            {
                Kind = SelectedDestinationKind?.Value ?? AutoBackupDestinationKind.LocalFolder,
                LocalFolder = IsLocalDestination ? NullIfBlank(LocalFolder) : null,
                ConnectionId = IsLocalDestination ? null : SelectedConnection?.Id,
                RemotePath = IsLocalDestination ? null : NullIfBlank(RemotePath),
            },
        };
    }

    private async Task SaveAsync(Task previousSave, AutoBackupOptions options)
    {
        await previousSave.ConfigureAwait(false);
        await _settings.Set(SettingKeys.AutoBackup, options).ConfigureAwait(false);
    }

    private static string? NullIfBlank(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnConfigurationChanged();
    }

    partial void OnLocalFolderChanged(string value)
    {
        OnConfigurationChanged();
    }

    partial void OnRemotePathChanged(string value)
    {
        OnConfigurationChanged();
    }

    partial void OnSelectedConnectionChanged(AutoBackupConnectionChoice? value)
    {
        OnConfigurationChanged();
    }

    partial void OnSelectedDestinationKindChanged(AutoBackupDestinationOption? value)
    {
        if (_loading || !_initialized)
        {
            return;
        }

        // The connection list depends on the kind, so it is rebuilt before the configuration is saved.
        _pendingSave = ReloadThenSaveAsync(_pendingSave);
    }

    private async Task ReloadThenSaveAsync(Task previousSave)
    {
        await previousSave.ConfigureAwait(true);
        await LoadConnectionsAsync(SelectedConnection?.Id, CancellationToken.None).ConfigureAwait(true);
        Validate();
        await _settings.Set(SettingKeys.AutoBackup, BuildOptions()).ConfigureAwait(false);
    }
}
