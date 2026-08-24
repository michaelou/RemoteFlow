using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Backup;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class AutomaticBackupSettingsViewModelTests
{
    /// <summary>The runner never trusts this flag — it can arrive from an imported archive — but the user
    /// should not be able to arm a configuration that will report Blocked the moment they edit anything.</summary>
    [Fact]
    public async Task TheEnableToggleIsBlockedUntilAPassphraseIsSet()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.ViewModel.LocalFolder = Path.Combine(Path.GetTempPath(), "backups");

        Assert.False(fixture.ViewModel.CanEnable);
        Assert.Contains("passphrase", fixture.ViewModel.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        fixture.ViewModel.NewPassphrase = "correct-horse-Battery9!";
        fixture.ViewModel.ConfirmPassphrase = "correct-horse-Battery9!";
        await fixture.ViewModel.SavePassphraseCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.HasStoredPassphrase);
        Assert.True(fixture.ViewModel.CanEnable);
        Assert.Null(fixture.ViewModel.ValidationMessage);
    }

    [Fact]
    public async Task TheTypedPassphraseIsClearedFromTheBoundFieldsAfterItIsSaved()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.ViewModel.NewPassphrase = "correct-horse-Battery9!";
        fixture.ViewModel.ConfirmPassphrase = "correct-horse-Battery9!";

        await fixture.ViewModel.SavePassphraseCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, fixture.ViewModel.NewPassphrase);
        Assert.Equal(string.Empty, fixture.ViewModel.ConfirmPassphrase);
    }

    [Fact]
    public async Task AMistypedConfirmationIsRefusedWithoutStoringAnything()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.ViewModel.NewPassphrase = "correct-horse-Battery9!";
        fixture.ViewModel.ConfirmPassphrase = "correct-horse-Battery8!";

        await fixture.ViewModel.SavePassphraseCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.HasStoredPassphrase);
        Assert.Contains("do not match", fixture.ViewModel.PassphraseMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWeakPassphraseIsRefusedWithTheReasonShown()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.ViewModel.NewPassphrase = "short";
        fixture.ViewModel.ConfirmPassphrase = "short";

        await fixture.ViewModel.SavePassphraseCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.HasStoredPassphrase);
        Assert.Contains("12 characters", fixture.ViewModel.PassphraseMessage!, StringComparison.Ordinal);
    }

    /// <summary>A connection that could never receive a backup should not be offered at all, rather than
    /// accepted and then rejected by the runner.</summary>
    [Fact]
    public async Task TheConnectionListOffersOnlyProtocolsThatCanReceiveABackup()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        fixture.ViewModel.SelectedDestinationKind =
            fixture.ViewModel.DestinationKinds.Single(kind => kind.Value == AutoBackupDestinationKind.SftpConnection);
        await fixture.ViewModel.FlushAsync();

        Assert.Equal(
            [ProtocolType.Ssh, ProtocolType.Sftp],
            fixture.Queries.LastFilter!.Protocols);

        fixture.ViewModel.SelectedDestinationKind = fixture.ViewModel.DestinationKinds
            .Single(kind => kind.Value == AutoBackupDestinationKind.ObjectStorageConnection);
        await fixture.ViewModel.FlushAsync();

        Assert.Equal(
            [ProtocolType.S3, ProtocolType.AzureBlob],
            fixture.Queries.LastFilter!.Protocols);
    }

    /// <summary>Settings travel inside backup archives, so an imported configuration can name a connection
    /// that only ever existed on another machine. It has to fail loudly rather than silently retarget.</summary>
    [Fact]
    public async Task AStoredConnectionThatNoLongerExistsFailsValidationRatherThanRetargeting()
    {
        using var fixture = Fixture.Create();
        await fixture.Settings.Set(
            SettingKeys.AutoBackup,
            new AutoBackupOptions
            {
                IsEnabled = true,
                Destination = new AutoBackupDestination
                {
                    Kind = AutoBackupDestinationKind.SftpConnection,
                    ConnectionId = Guid.NewGuid(),
                    RemotePath = "/srv/backups",
                },
            },
            TestContext.Current.CancellationToken);

        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(fixture.ViewModel.SelectedConnection);
        Assert.NotNull(fixture.ViewModel.ValidationMessage);
        Assert.False(fixture.ViewModel.IsEnabled);
    }

    [Fact]
    public async Task RetentionIsClampedAsItIsTyped()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        fixture.ViewModel.RetainedCopies = 0;
        Assert.Equal(AutoBackupOptions.MinimumRetainedCopies, fixture.ViewModel.RetainedCopies);

        fixture.ViewModel.RetainedCopies = 100_000;
        Assert.Equal(AutoBackupOptions.MaximumRetainedCopies, fixture.ViewModel.RetainedCopies);
    }

    [Fact]
    public async Task TheDestinationSummaryReadsBackWhatWasUnderstood()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        fixture.ViewModel.LocalFolder = "/home/you/backups";
        Assert.Equal("/home/you/backups", fixture.ViewModel.DestinationSummary);

        fixture.ViewModel.SelectedDestinationKind =
            fixture.ViewModel.DestinationKinds.Single(kind => kind.Value == AutoBackupDestinationKind.SftpConnection);
        await fixture.ViewModel.FlushAsync();
        fixture.ViewModel.SelectedConnection = fixture.ViewModel.AvailableConnections[0];
        fixture.ViewModel.RemotePath = "/srv/backups/remoteflow";

        Assert.Equal("sftp://backup-01:22/srv/backups/remoteflow", fixture.ViewModel.DestinationSummary);
    }

    [Fact]
    public async Task ClearingThePassphraseTurnsAutomaticBackupOffRatherThanLeavingItBlocked()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.ViewModel.LocalFolder = Path.Combine(Path.GetTempPath(), "backups");
        fixture.ViewModel.NewPassphrase = "correct-horse-Battery9!";
        fixture.ViewModel.ConfirmPassphrase = "correct-horse-Battery9!";
        await fixture.ViewModel.SavePassphraseCommand.ExecuteAsync(null);
        fixture.ViewModel.IsEnabled = true;

        await fixture.ViewModel.ClearPassphraseCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.HasStoredPassphrase);
        Assert.False(fixture.ViewModel.IsEnabled);
        Assert.False(fixture.ViewModel.CanEnable);
    }

    /// <summary>The crash this replaced: on Linux without libsecret the file vault is selected, reports
    /// itself available, and then throws on every read until something unlocks it — which nothing in the app
    /// does yet. Opening the tab took the exception straight out of the page.</summary>
    [Fact]
    public async Task ALockedCredentialStoreLeavesThePageUsableInsteadOfThrowing()
    {
        using var fixture = Fixture.Create();
        fixture.Passphrases.Problem = "The credential vault is locked.";

        var exception = await Record.ExceptionAsync(
            () => fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal("The credential vault is locked.", fixture.ViewModel.PassphraseStoreProblem);
        Assert.False(fixture.ViewModel.HasStoredPassphrase);
        Assert.False(fixture.ViewModel.CanEnable);
        Assert.False(fixture.ViewModel.IsEnabled);
    }

    /// <summary>Declining the startup prompt should not mean restarting RemoteFlow to change your mind.</summary>
    [Fact]
    public async Task ALockedStoreOffersAWayToUnlockItWithoutRestarting()
    {
        using var fixture = Fixture.Create();
        fixture.Passphrases.Problem = "The credential vault is locked.";
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.ViewModel.CanUnlockVault);

        // The vault opens on the second ask, the way it would if the user typed the right passphrase.
        fixture.VaultUnlock.OnUnlock = () => fixture.Passphrases.Problem = null;
        await fixture.ViewModel.UnlockVaultCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.PassphraseStoreProblem);
        Assert.False(fixture.ViewModel.CanUnlockVault);
        Assert.True(fixture.ViewModel.CanEditPassphrase);
    }

    [Fact]
    public async Task DecliningTheUnlockPromptLeavesTheExplanationInPlace()
    {
        using var fixture = Fixture.Create();
        fixture.Passphrases.Problem = "The credential vault is locked.";
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        fixture.VaultUnlock.Result = new VaultUnlockStatus
        {
            IsUsable = false,
            WasPrompted = true,
            Problem = "The credential vault was not unlocked, so saved passwords and keys are unavailable.",
        };

        await fixture.ViewModel.UnlockVaultCommand.ExecuteAsync(null);

        Assert.NotNull(fixture.ViewModel.PassphraseStoreProblem);
        Assert.False(fixture.ViewModel.CanEnable);
        Assert.Contains("not unlocked", fixture.ViewModel.PassphraseMessage!, StringComparison.Ordinal);
    }

    /// <summary>A locked store is not fixed by typing a new passphrase, so the page must not invite it.</summary>
    [Fact]
    public async Task ALockedStoreIsExplainedRatherThanOfferingToSetAPassphrase()
    {
        using var fixture = Fixture.Create();
        fixture.Passphrases.Problem = "The credential vault is locked.";
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(fixture.ViewModel.CanEditPassphrase);
        Assert.DoesNotContain("No passphrase is set", fixture.ViewModel.PassphraseStatus, StringComparison.Ordinal);
        Assert.Contains("could not be opened", fixture.ViewModel.PassphraseStatus, StringComparison.Ordinal);
        Assert.Contains("could not be opened", fixture.ViewModel.ValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastRunStatusIsRenderedWithoutADialog()
    {
        using var fixture = Fixture.Create();
        fixture.Runner.Publish(new AutoBackupStatus
        {
            RunUtc = DateTimeOffset.UtcNow,
            Outcome = AutoBackupOutcome.Failed,
            Destination = "sftp://backup-01:22/srv/backups",
            Message = "The host could not be reached.",
        });

        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.ViewModel.HasLastRun);
        Assert.True(fixture.ViewModel.LastRunFailed);
        Assert.False(fixture.ViewModel.LastRunSucceeded);
        Assert.Contains("failed", fixture.ViewModel.LastRunHeadline, StringComparison.Ordinal);
        Assert.Equal("The host could not be reached.", fixture.ViewModel.LastRunMessage);
    }

    /// <summary>The run finishes on whichever thread it was on, so the status change has to be marshalled
    /// before it touches bound properties.</summary>
    [Fact]
    public async Task AStatusChangeRaisedOffTheUiThreadIsMarshalled()
    {
        using var fixture = Fixture.Create();
        await fixture.ViewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await Task.Run(
            () => fixture.Runner.Publish(new AutoBackupStatus
            {
                RunUtc = DateTimeOffset.UtcNow,
                Outcome = AutoBackupOutcome.Succeeded,
                Destination = "/home/you/backups",
                Message = "Backed up 4 connections.",
            }),
            TestContext.Current.CancellationToken);
        await fixture.Dispatcher.DrainAsync();

        Assert.True(fixture.Dispatcher.MarshalledAtLeastOnce);
        Assert.True(fixture.ViewModel.LastRunSucceeded);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            InMemorySettingsStore settings,
            StubConnectionQueries queries,
            FakePassphraseStore passphrases,
            FakeRunner runner,
            RecordingDispatcher dispatcher,
            FakeVaultUnlock vaultUnlock)
        {
            Settings = settings;
            Queries = queries;
            Passphrases = passphrases;
            Runner = runner;
            Dispatcher = dispatcher;
            VaultUnlock = vaultUnlock;
            ViewModel = new AutomaticBackupSettingsViewModel(
                settings, new StubFilePicker(), dispatcher, queries, passphrases, runner, vaultUnlock);
        }

        public InMemorySettingsStore Settings { get; }

        public StubConnectionQueries Queries { get; }

        public FakePassphraseStore Passphrases { get; }

        public FakeRunner Runner { get; }

        public RecordingDispatcher Dispatcher { get; }

        public FakeVaultUnlock VaultUnlock { get; }

        public AutomaticBackupSettingsViewModel ViewModel { get; }

        public static Fixture Create()
        {
            return new Fixture(
                new InMemorySettingsStore(),
                new StubConnectionQueries(
                [
                    new ConnectionListItem(
                        Guid.NewGuid(), "Backup host", "backup-01", 22, ProtocolType.Sftp,
                        EnvironmentKind.Production, false, null, null, null, null, [], null),
                ]),
                new FakePassphraseStore(),
                new FakeRunner(),
                new RecordingDispatcher(),
                new FakeVaultUnlock());
        }

        public void Dispose()
        {
            ViewModel.Dispose();
        }
    }

    private sealed class StubConnectionQueries(IReadOnlyList<ConnectionListItem> items) : IConnectionQueryService
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

    private sealed class StubFilePicker : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickDownloadFolderAsync(
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> PickFolderAsync(
            string title,
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakePassphraseStore : IAutoBackupPassphraseStore
    {
        private string? _passphrase;

        public bool IsAvailable => true;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("the test keyring");
        }

        public string? Problem { get; set; }

        public Task<AutoBackupPassphraseState> InspectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Problem is not null
                ? new AutoBackupPassphraseState(false, Problem)
                : _passphrase is null
                    ? AutoBackupPassphraseState.Missing
                    : AutoBackupPassphraseState.Present);
        }

        public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_passphrase is null ? null : new SecretHandle(_passphrase));
        }

        public Task<Result<bool>> SetAsync(
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default)
        {
            if (!PassphrasePolicy.IsStrong(passphrase.Span))
            {
                return Task.FromResult(Result<bool>.Failure(RemoteFlowError.Validation(
                    "autobackup.weak_passphrase", PassphrasePolicy.Requirement)));
            }

            _passphrase = new string(passphrase.Span);
            return Task.FromResult(Result<bool>.Success(true));
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _passphrase = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner : IAutoBackupRunner
    {
        public event EventHandler? StatusChanged;

        public AutoBackupStatus? LastStatus { get; private set; }

        public void Publish(AutoBackupStatus status)
        {
            LastStatus = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<AutoBackupStatus> RunNowAsync(CancellationToken cancellationToken = default)
        {
            var status = new AutoBackupStatus { Outcome = AutoBackupOutcome.Succeeded };
            Publish(status);
            return Task.FromResult(status);
        }

        public Task DrainAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVaultUnlock : IVaultUnlockService
    {
        public VaultUnlockStatus? Result { get; set; }

        public Action? OnUnlock { get; set; }

        public Task<VaultUnlockStatus> EnsureUnlockedAsync(CancellationToken cancellationToken = default)
        {
            OnUnlock?.Invoke();
            return Task.FromResult(Result ?? new VaultUnlockStatus { IsUsable = true, WasPrompted = true });
        }
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        private readonly List<Task> _pending = [];

        public bool MarshalledAtLeastOnce { get; private set; }

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            MarshalledAtLeastOnce = true;
            action();
            lock (_pending)
            {
                _pending.Add(Task.CompletedTask);
            }

            return ValueTask.CompletedTask;
        }

        public async Task DrainAsync()
        {
            // Gives the fire-and-forget marshalling task a turn to run.
            await Task.Yield();
            Task[] pending;
            lock (_pending)
            {
                pending = [.. _pending];
            }

            await Task.WhenAll(pending);
        }
    }
}
