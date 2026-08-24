using Microsoft.Extensions.Time.Testing;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services.Backup;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class AutoBackupRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 24, 13, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task CoalescesABurstOfChangesIntoOneArchive()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        // What one save of a connection actually looks like from out here, plus a folder and a tag edit.
        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        harness.Workspace.Notify(WorkspaceEntityKind.Folder, Guid.NewGuid(), WorkspaceChangeKind.Updated);
        harness.Workspace.Notify(WorkspaceEntityKind.Tag, Guid.NewGuid(), WorkspaceChangeKind.Updated);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(1, harness.Backup.ExportCount);
        Assert.Equal(AutoBackupOutcome.Succeeded, harness.Runner.LastStatus!.Outcome);
        Assert.False(harness.Runner.LastStatus.PendingChanges);
    }

    [Fact]
    public async Task TheQuietPeriodRestartsWhileChangesKeepArriving()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        harness.Time.Advance(TimeSpan.FromSeconds(25));
        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        harness.Time.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(0, harness.Backup.ExportCount);

        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(1, harness.Backup.ExportCount);
    }

    /// <summary>Reloaded is raised after a backup import. Acting on it would overwrite the newest archive
    /// with a copy of what was just restored.</summary>
    [Fact]
    public async Task IgnoresTheReloadedKindRaisedByAnImport()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        harness.Connections.NotifyReloaded();
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(0, harness.Backup.ExportCount);
    }

    /// <summary>The signal arrives synchronously from inside a save, sometimes with a write transaction
    /// still open. Whatever goes wrong in here must not become a connection that failed to save.</summary>
    [Fact]
    public async Task AnExceptionInsideTheHandlerNeverReachesTheCaller()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);
        harness.Backup.Throw = new InvalidOperationException("the archive could not be written");
        harness.Status.Throw = true;

        var exception = Record.Exception(() =>
            harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated));
        Assert.Null(exception);

        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(AutoBackupOutcome.Failed, harness.Runner.LastStatus!.Outcome);
        Assert.True(harness.Runner.LastStatus.PendingChanges);
    }

    /// <summary>Deleting a folder together with its connections signals from inside the ambient transaction.
    /// If scheduling touched the settings store or the destination on that thread it could block on, or
    /// read through, a transaction that has not committed.</summary>
    [Fact]
    public async Task SchedulingPerformsNoIoOnTheCallingThread()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);
        harness.Settings.FailIfRead = true;
        harness.Destinations.FailIfCreated = true;

        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Deleted);

        // Nothing has run yet: the quiet period has not elapsed, so nothing has been read.
        Assert.Equal(0, harness.Backup.ExportCount);
        harness.Settings.FailIfRead = false;
        harness.Destinations.FailIfCreated = false;
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(1, harness.Backup.ExportCount);
    }

    [Fact]
    public async Task ADisabledConfigurationDoesNotExport()
    {
        await using var harness = await Harness.CreateAsync(enabled: false);

        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(0, harness.Backup.ExportCount);
    }

    /// <summary>The important half of the credential decision: no passphrase means no archive, never a
    /// quietly credential-free one that looks restorable and is not.</summary>
    [Fact]
    public async Task EnabledWithoutAPassphraseRecordsBlockedAndDoesNotExport()
    {
        await using var harness = await Harness.CreateAsync(enabled: true, passphrase: null);

        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(0, harness.Backup.ExportCount);
        Assert.Equal(AutoBackupOutcome.Blocked, harness.Runner.LastStatus!.Outcome);
        Assert.Contains("no passphrase is set", harness.Runner.LastStatus.Message, StringComparison.Ordinal);
        Assert.True(harness.Runner.LastStatus.PendingChanges);
    }

    [Fact]
    public async Task AlwaysExportsEverythingWithCredentialsAndRefusesAWeakPassphrase()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        _ = await harness.Runner.RunNowAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(harness.Backup.Requests);
        Assert.True(request.IncludeCredentials);
        Assert.False(request.AllowWeakPassphrase);
        Assert.True(request.IncludeSettings);
        Assert.True(request.IncludeHostKeys);
        Assert.Equal(BackupExportScopeKind.All, request.Scope.Kind);
        Assert.Equal(Harness.Passphrase, Assert.Single(harness.Backup.Passphrases));
    }

    [Fact]
    public async Task TheStagingFileIsDeletedAfterSuccessAndAfterFailure()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        _ = await harness.Runner.RunNowAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(harness.Destinations.LastStagingPath!));

        harness.Backup.Throw = new InvalidOperationException("nope");
        _ = await harness.Runner.RunNowAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(harness.Destinations.LastStagingPath!));
    }

    [Fact]
    public async Task AFailedPublishIsReportedAndLeavesTheChangesPending()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);
        harness.Destinations.Destination.PublishFailure = SftpError.ConnectionLost;

        var status = await harness.Runner.RunNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AutoBackupOutcome.Failed, status.Outcome);
        Assert.True(status.PendingChanges);
        Assert.Empty(harness.Destinations.Destination.Archives);
    }

    [Fact]
    public async Task AnUnreachableDestinationIsReportedRatherThanThrown()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);
        harness.Destinations.CreateFailure = "The connection no longer exists on this machine.";

        var status = await harness.Runner.RunNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AutoBackupOutcome.Failed, status.Outcome);
        Assert.Contains("no longer exists", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatchUpIsScheduledWhenTheLastStatusHasPendingChanges()
    {
        await using var harness = await Harness.CreateAsync(enabled: true, initialize: false);
        await harness.Status.WriteAsync(
            new AutoBackupStatus { Outcome = AutoBackupOutcome.Succeeded, PendingChanges = true },
            TestContext.Current.CancellationToken);

        await harness.Runner.InitializeAsync(TestContext.Current.CancellationToken);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(1, harness.Backup.ExportCount);
    }

    [Fact]
    public async Task NoCatchUpWhenTheLastRunSucceededCleanly()
    {
        await using var harness = await Harness.CreateAsync(enabled: true, initialize: false);
        await harness.Status.WriteAsync(
            new AutoBackupStatus { Outcome = AutoBackupOutcome.Succeeded, PendingChanges = false },
            TestContext.Current.CancellationToken);

        await harness.Runner.InitializeAsync(TestContext.Current.CancellationToken);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(0, harness.Backup.ExportCount);
    }

    [Fact]
    public async Task TheFirstLaunchAfterEnablingMakesABaselineArchive()
    {
        await using var harness = await Harness.CreateAsync(enabled: true, initialize: false);

        await harness.Runner.InitializeAsync(TestContext.Current.CancellationToken);
        await harness.AdvancePastQuietPeriodAsync();

        Assert.Equal(1, harness.Backup.ExportCount);
    }

    [Fact]
    public async Task DisposeCancelsAPendingQuietPeriodWithoutRunning()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);

        harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated);
        harness.Runner.Dispose();
        harness.Time.Advance(AutoBackupRunner.QuietPeriod * 2);
        await Task.Yield();

        Assert.Equal(0, harness.Backup.ExportCount);
    }

    [Fact]
    public async Task AChangeArrivingAfterDisposeIsIgnored()
    {
        await using var harness = await Harness.CreateAsync(enabled: true);
        harness.Runner.Dispose();

        var exception = Record.Exception(() =>
            harness.Connections.Notify(Guid.NewGuid(), ConnectionChangeKind.Updated));

        Assert.Null(exception);
        Assert.Equal(0, harness.Backup.ExportCount);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string Passphrase = "correct-horse-Battery9!";

        private readonly string _root;

        private Harness(string root, AutoBackupOptions options, string? passphrase, bool settled)
        {
            _root = root;
            Paths = new TempAppPaths(root);
            Settings = new RecordingSettingsStore();
            _ = Settings.Set(SettingKeys.AutoBackup, options, CancellationToken.None);
            Backup = new FakeBackupService();
            Destinations = new FakeDestinationFactory(Path.Combine(root, "staging"));
            Passphrases = new FakePassphraseStore(passphrase);
            Status = new FakeStatusStore();
            if (settled)
            {
                _ = Status.WriteAsync(
                    new AutoBackupStatus { Outcome = AutoBackupOutcome.Succeeded, PendingChanges = false },
                    CancellationToken.None);
            }

            Runner = new AutoBackupRunner(
                Settings, Backup, Destinations, Passphrases, Status,
                Connections, Workspace, new FakeClock(_now), SystemGuidProvider.Instance, Paths, Time);
        }

        public FakeTimeProvider Time { get; } = new(_now);

        public ConnectionChangeNotifier Connections { get; } = new();

        public WorkspaceChangeNotifier Workspace { get; } = new();

        public RecordingSettingsStore Settings { get; }

        public FakeBackupService Backup { get; }

        public FakeDestinationFactory Destinations { get; }

        public FakePassphraseStore Passphrases { get; }

        public FakeStatusStore Status { get; }

        public TempAppPaths Paths { get; }

        public AutoBackupRunner Runner { get; }

        public static async Task<Harness> CreateAsync(
            bool enabled,
            string? passphrase = Passphrase,
            bool initialize = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "remoteflow-autobackup-tests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(root);
            var options = new AutoBackupOptions
            {
                IsEnabled = enabled,
                RetainedCopies = 3,
                Destination = new AutoBackupDestination
                {
                    Kind = AutoBackupDestinationKind.LocalFolder,
                    LocalFolder = Path.Combine(root, "backups"),
                },
            };
            // Initialised harnesses start from a settled status so each test observes only the signals it
            // raises; the catch-up-on-launch tests opt out with initialize: false and seed their own.
            var harness = new Harness(root, options, passphrase, settled: initialize);
            if (initialize)
            {
                await harness.Runner.InitializeAsync(CancellationToken.None);
            }

            return harness;
        }

        public async Task AdvancePastQuietPeriodAsync()
        {
            Time.Advance(AutoBackupRunner.QuietPeriod + TimeSpan.FromSeconds(1));
            await Runner.DrainAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            await Task.Yield();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temp directory that will not go is not a test failure.
            }
        }
    }

    private sealed class TempAppPaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");

        public string DataDirectory { get; } = Path.Combine(root, "data");

        public string CacheDirectory { get; } = Path.Combine(root, "cache");

        public string LogDirectory { get; } = Path.Combine(root, "logs");

        public void EnsureDirectories()
        {
            _ = Directory.CreateDirectory(ConfigDirectory);
            _ = Directory.CreateDirectory(DataDirectory);
            _ = Directory.CreateDirectory(CacheDirectory);
            _ = Directory.CreateDirectory(LogDirectory);
        }
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        private readonly InMemorySettingsStore _inner = new();

        public event EventHandler<SettingChangedEventArgs>? SettingChanged
        {
            add => _inner.SettingChanged += value;
            remove => _inner.SettingChanged -= value;
        }

        public bool FailIfRead { get; set; }

        public Task<T> Get<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfRead, "Settings were read on a thread that must not touch the database.");
            return _inner.Get(key, cancellationToken);
        }

        public Task Set<T>(SettingKey<T> key, T value, CancellationToken cancellationToken = default)
        {
            return _inner.Set(key, value, cancellationToken);
        }

        public Task SeedDefaults(CancellationToken cancellationToken = default)
        {
            return _inner.SeedDefaults(cancellationToken);
        }
    }

    private sealed class FakeBackupService : IBackupService
    {
        public List<BackupExportRequest> Requests { get; } = [];

        /// <summary>Captured during the call. Reading request.CredentialPassphrase afterwards yields zeros,
        /// because the runner disposes the SecretHandle as soon as the export returns.</summary>
        public List<string> Passphrases { get; } = [];

        public int ExportCount => Requests.Count;

        public Exception? Throw { get; set; }

        public bool CanExportCredentials => true;

        public async Task<BackupExportResult> ExportAsync(
            BackupExportRequest request,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Passphrases.Add(new string(request.CredentialPassphrase.Span));
            if (Throw is not null)
            {
                throw Throw;
            }

            await File.WriteAllTextAsync(request.DestinationPath, "archive", cancellationToken);
            return new BackupExportResult(request.DestinationPath, new BackupEntityCounts(4, 2, 3, 1, 5, 0));
        }

        public Task<BackupInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<BackupImportResult> ApplyAsync(
            BackupApplyRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeDestinationFactory(string stagingRoot) : IAutoBackupDestinationFactory
    {
        public FakeDestination Destination { get; } = new(stagingRoot);

        public bool FailIfCreated { get; set; }

        public string? CreateFailure { get; set; }

        public string? LastStagingPath => Destination.LastStagingPath;

        public Task<SftpResult<IAutoBackupDestination>> CreateAsync(
            AutoBackupDestination destination,
            CancellationToken cancellationToken = default)
        {
            Assert.False(FailIfCreated, "A destination was opened on a thread that must not do I/O.");
            return Task.FromResult(CreateFailure is null
                ? SftpResult<IAutoBackupDestination>.Success(Destination)
                : SftpResult<IAutoBackupDestination>.Fail(SftpError.NotFound, CreateFailure));
        }
    }

    private sealed class FakeDestination(string stagingRoot) : IAutoBackupDestination
    {
        public string Description => "test destination";

        public List<string> Archives { get; } = [];

        public string? LastStagingPath { get; private set; }

        public SftpError? PublishFailure { get; set; }

        public string GetStagingPath(string fileName)
        {
            _ = Directory.CreateDirectory(stagingRoot);
            LastStagingPath = Path.Combine(stagingRoot, fileName);
            return LastStagingPath;
        }

        public Task<SftpResult> PublishAsync(
            string stagingPath,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (PublishFailure is { } error)
            {
                return Task.FromResult(SftpResult.Fail(error, "the upload failed"));
            }

            Archives.Add(fileName);
            return Task.FromResult(SftpResult.Success());
        }

        public Task<SftpResult<IReadOnlyList<AutoBackupArchive>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AutoBackupArchive> archives =
            [
                .. Archives.Select(name =>
                {
                    _ = AutoBackupNaming.TryParse(name, out var created);
                    return new AutoBackupArchive(name, name, created, 1);
                }),
            ];
            return Task.FromResult(SftpResult<IReadOnlyList<AutoBackupArchive>>.Success(archives));
        }

        public Task<SftpResult> DeleteAsync(
            AutoBackupArchive archive,
            CancellationToken cancellationToken = default)
        {
            _ = Archives.Remove(archive.Name);
            return Task.FromResult(SftpResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePassphraseStore(string? passphrase) : IAutoBackupPassphraseStore
    {
        public bool IsAvailable => true;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("test keyring");
        }

        public Task<AutoBackupPassphraseState> InspectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(passphrase is null
                ? AutoBackupPassphraseState.Missing
                : AutoBackupPassphraseState.Present);
        }

        public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(passphrase is null ? null : new SecretHandle(passphrase));
        }

        public Task<Result<bool>> SetAsync(
            ReadOnlyMemory<char> value,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeStatusStore : IAutoBackupStatusStore
    {
        private AutoBackupStatus? _status;

        public bool Throw { get; set; }

        public Task<AutoBackupStatus?> ReadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_status);
        }

        public Task WriteAsync(AutoBackupStatus status, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new IOException("the status file is unwritable");
            }

            _status = status;
            return Task.CompletedTask;
        }
    }
}
