using System.IO.Compression;
using Microsoft.Extensions.Time.Testing;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Services.Backup;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Backup;
using RemoteFlow.Infrastructure.Security.Crypto;
using RemoteFlow.Persistence.Backup;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>The whole feature wired to real parts: real SQLite, the real zip serializer, real Argon2id and
/// AES-GCM, the real local destination. The unit tests prove each piece behaves; this proves an automatic
/// backup is an archive you can actually restore from, which is the only claim that matters.</summary>
public sealed class AutomaticBackupEndToEndTests
{
    private const string _passphrase = "correct-horse-Battery9!";

    [Fact]
    public async Task AChangeToAConnectionProducesARestorableEncryptedArchive()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        await world.EnableAsync(retainedCopies: 3, token);

        _ = await world.Connections.CreateAsync(
            new ConnectionInput("Web server", "web-01.test", 22, ProtocolType.Ssh, "deploy"), token);
        await world.SettleAsync();

        var archive = Assert.Single(Directory.GetFiles(world.BackupFolder));
        Assert.True(AutoBackupNaming.TryParse(Path.GetFileName(archive), out _));
        Assert.Equal(AutoBackupOutcome.Succeeded, world.Runner.LastStatus!.Outcome);
        Assert.False(world.Runner.LastStatus.PendingChanges);

        // The archive is a real zip carrying the documented entries, with credentials encrypted inside it.
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("connections.json"));
            Assert.NotNull(zip.GetEntry("credentials.enc"));
        }

        var inspection = await world.Backups.InspectAsync(archive, token);

        Assert.True(inspection.ContainsCredentials);
        Assert.Equal(1, inspection.Counts.Connections);
    }

    /// <summary>A rename, a folder edit and a tag edit inside one quiet period are one archive, not three.</summary>
    [Fact]
    public async Task ABurstOfEditsAcrossConnectionsFoldersAndTagsProducesOneArchive()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        var connection = (await world.Connections.CreateAsync(
            new ConnectionInput("Web server", "web-01.test", 22, ProtocolType.Ssh, "deploy"), token)).Value;
        await world.EnableAsync(retainedCopies: 5, token);

        _ = await world.Connections.RenameAsync(connection.Id, "Web server 01", token);
        var folder = (await world.Folders.CreateAsync("Production", cancellationToken: token)).Value;
        _ = await world.Connections.MoveToFolderAsync(connection.Id, folder.Id, token);
        var tag = (await world.Tags.CreateAsync("critical", cancellationToken: token)).Value;
        _ = await world.Tags.AssignAsync(connection.Id, tag.Id, token);
        await world.SettleAsync();

        _ = Assert.Single(Directory.GetFiles(world.BackupFolder));
    }

    [Fact]
    public async Task RetentionKeepsTheNewestAndLeavesEverythingElseAlone()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        await world.EnableAsync(retainedCopies: 3, token);

        // Files a user might reasonably keep in the same folder, including a manual export.
        var manualExport = Path.Combine(world.BackupFolder, "RemoteFlow-backup-20260824-120000.zip");
        var notes = Path.Combine(world.BackupFolder, "notes.zip");
        await File.WriteAllTextAsync(manualExport, "mine", token);
        await File.WriteAllTextAsync(notes, "mine", token);

        for (var run = 0; run < 5; run++)
        {
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            _ = await world.Runner.RunNowAsync(token);
        }

        var automatic = Directory.GetFiles(world.BackupFolder)
            .Select(Path.GetFileName)
            .Where(AutoBackupNaming.IsAutoBackupName)
            .ToArray();
        Assert.Equal(3, automatic.Length);
        Assert.True(File.Exists(manualExport));
        Assert.True(File.Exists(notes));
    }

    /// <summary>The point of including credentials. An automatic archive has to restore into a working
    /// connection, secret and all — not a list of hostnames.</summary>
    [Fact]
    public async Task AnAutomaticArchiveRestoresTheConnectionAndItsCredential()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        await world.EnableAsync(retainedCopies: 3, token);
        var connection = (await world.Connections.CreateAsync(
            new ConnectionInput("Web server", "web-01.test", 22, ProtocolType.Ssh, "deploy"), token)).Value;
        _ = await world.Credentials.StoreAsync(
            connection.Id, CredentialKind.Password, "hunter2-is-not-great".AsMemory(), "Web server", token);
        await world.SettleAsync();
        var archive = Directory.GetFiles(world.BackupFolder).Single(path => AutoBackupNaming.IsAutoBackupName(Path.GetFileName(path)));

        // Wipe the store, then restore from the archive the runner made on its own.
        var deleted = await world.Connections.DeleteAsync(connection.Id, token);
        Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Message : string.Empty);
        Assert.Empty(await world.ConnectionRepository.ListAsync(token));

        var applied = await world.Backups.ApplyAsync(
            new BackupApplyRequest(archive, MergeStrategy.Replace, ReplaceConfirmation: "REPLACE",
                CredentialPassphrase: _passphrase.AsMemory()),
            token);

        Assert.Equal(1, applied.AppliedCounts.Connections);
        Assert.Empty(applied.MissingCredentials);
        var restored = Assert.Single(await world.ConnectionRepository.ListAsync(token));

        Assert.Equal("Web server", restored.Name);
        using var secret = await world.CredentialStore.GetAsync(restored.Credential.StoreKey, token);
        Assert.NotNull(secret);
        Assert.Equal("hunter2-is-not-great", secret.Secret.ToString());
    }

    [Fact]
    public async Task WithoutAPassphraseNothingIsWrittenAndTheReasonIsRecorded()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        await world.Settings.Set(
            SettingKeys.AutoBackup,
            new AutoBackupOptions
            {
                IsEnabled = true,
                RetainedCopies = 3,
                Destination = new AutoBackupDestination
                {
                    Kind = AutoBackupDestinationKind.LocalFolder,
                    LocalFolder = world.BackupFolder,
                },
            },
            token);
        await world.Runner.InitializeAsync(token);

        var status = await world.Runner.RunNowAsync(token);

        Assert.Equal(AutoBackupOutcome.Blocked, status.Outcome);
        Assert.Empty(Directory.GetFiles(world.BackupFolder));
        Assert.Contains("no passphrase is set", status.Message, StringComparison.Ordinal);
    }

    /// <summary>Importing an archive raises Reloaded. Acting on it would overwrite the newest backup with a
    /// copy of what was just restored.</summary>
    [Fact]
    public async Task RestoringABackupDoesNotItselfTriggerABackup()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await World.CreateAsync(token);
        await world.EnableAsync(retainedCopies: 3, token);
        _ = await world.Connections.CreateAsync(
            new ConnectionInput("Web server", "web-01.test", 22, ProtocolType.Ssh, "deploy"), token);
        await world.SettleAsync();
        var archive = Directory.GetFiles(world.BackupFolder).Single(path => AutoBackupNaming.IsAutoBackupName(Path.GetFileName(path)));
        var before = Directory.GetFiles(world.BackupFolder).Length;

        _ = await world.Backups.ApplyAsync(
            new BackupApplyRequest(archive, MergeStrategy.Merge, CredentialPassphrase: _passphrase.AsMemory()),
            token);
        await world.SettleAsync();

        Assert.Equal(before, Directory.GetFiles(world.BackupFolder).Length);
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly SqliteTempDbFixture _database;
        private readonly string _root;

        private World(SqliteTempDbFixture database, string root)
        {
            _database = database;
            _root = root;
            BackupFolder = Path.Combine(root, "backups");
            _ = Directory.CreateDirectory(BackupFolder);

            var guids = SystemGuidProvider.Instance;
            ConnectionRepository = new ConnectionRepository(database.Factory);
            var unitOfWork = new UnitOfWork(database.Factory);
            CredentialStore = new MemoryCredentialProvider("test keyring");
            Connections = new ConnectionService(
                ConnectionRepository, new RecentConnectionStore(database.Factory), [CredentialStore],
                unitOfWork, guids, Clock, ChangeNotifier);
            Folders = new FolderService(
                new FolderRepository(database.Factory), ConnectionRepository, Connections,
                unitOfWork, guids, Clock, WorkspaceNotifier);
            Tags = new TagService(
                new TagRepository(database.Factory), ConnectionRepository, unitOfWork, guids, Clock, WorkspaceNotifier);

            var selector = new StaticSelector(CredentialStore);
            Credentials = new ConnectionCredentialService(
                ConnectionRepository, selector, [CredentialStore], unitOfWork, guids, Clock, ChangeNotifier);
            var envelope = new CredentialEnvelope(
                new Argon2idPassphraseKdf(),
                new AesGcmAuthenticatedCipher(),
                new CryptoSecureRandom(),
                selector,
                [CredentialStore]);
            Backups = new BackupService(
                new EfBackupDataSource(database.Factory),
                new ZipBackupArchiveSerializer(),
                Clock,
                new EfBackupImportStore(database.Factory, database.DatabasePath),
                envelope,
                ChangeNotifier);
            Settings = new SettingsStore(database.Factory);
            Passphrases = new AutoBackupPassphraseStore(selector, [CredentialStore]);
            Runner = new AutoBackupRunner(
                Settings, Backups,
                new AutoBackupDestinationFactory(
                    ConnectionRepository, new UnusedAuthProvider(), new UnusedTransport(),
                    new UnusedObjectStorageFactory(), new StubAppPaths(root)),
                Passphrases,
                new FileAutoBackupStatusStore(new StubAppPaths(root)),
                ChangeNotifier, WorkspaceNotifier, Clock, guids, new StubAppPaths(root), Time);
        }

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        public ConnectionChangeNotifier ChangeNotifier { get; } = new();

        public WorkspaceChangeNotifier WorkspaceNotifier { get; } = new();

        public string BackupFolder { get; }

        public ConnectionRepository ConnectionRepository { get; }

        public ConnectionService Connections { get; }

        public FolderService Folders { get; }

        public TagService Tags { get; }

        public ConnectionCredentialService Credentials { get; }

        public MemoryCredentialProvider CredentialStore { get; }

        public BackupService Backups { get; }

        public SettingsStore Settings { get; }

        public AutoBackupPassphraseStore Passphrases { get; }

        public AutoBackupRunner Runner { get; }

        public static async Task<World> CreateAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(), "remoteflow-autobackup-e2e", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(root);
            return new World(await SqliteTempDbFixture.CreateAsync(cancellationToken), root);
        }

        public async Task EnableAsync(int retainedCopies, CancellationToken cancellationToken)
        {
            var stored = await Passphrases.SetAsync(_passphrase.AsMemory(), cancellationToken);
            Assert.True(stored.IsSuccess);
            await Settings.Set(
                SettingKeys.AutoBackup,
                new AutoBackupOptions
                {
                    IsEnabled = true,
                    RetainedCopies = retainedCopies,
                    Destination = new AutoBackupDestination
                    {
                        Kind = AutoBackupDestinationKind.LocalFolder,
                        LocalFolder = BackupFolder,
                    },
                },
                cancellationToken);
            await Runner.InitializeAsync(cancellationToken);
            // Clears the baseline archive the first launch owes, so each test counts only its own edits.
            await SettleAsync();
            foreach (var file in Directory.GetFiles(BackupFolder))
            {
                File.Delete(file);
            }
        }

        /// <summary>Waits out the quiet period on the virtual clock. It advances in a loop because the
        /// debounce task writes its pending marker — real file I/O — before it reaches the timer, and a
        /// clock advanced before the timer is registered would leave it waiting for a tick that never
        /// comes. Every advance is virtual; only the short yields between them are real.</summary>
        public async Task SettleAsync()
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var drain = Runner.DrainAsync(CancellationToken.None);
            for (var attempt = 0; attempt < 200; attempt++)
            {
                Time.Advance(AutoBackupRunner.QuietPeriod + TimeSpan.FromSeconds(1));
                if (await Task.WhenAny(drain, Task.Delay(25)).ConfigureAwait(false) == drain)
                {
                    await drain.ConfigureAwait(false);
                    return;
                }
            }

            throw new TimeoutException("The automatic backup never settled.");
        }

        public async ValueTask DisposeAsync()
        {
            Runner.Dispose();
            Settings.Dispose();
            await _database.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not a test failure.
            }
        }
    }

    private sealed class StubAppPaths(string root) : IAppPaths
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

    private sealed class StaticSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    internal sealed class MemoryCredentialProvider(string name) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.TryGetValue(storeKey, out var secret) ? new SecretHandle(secret) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            _values[storeKey] = new string(secret.Span);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            _ = _values.Remove(storeKey);
            return Task.CompletedTask;
        }
    }

    /// <summary>The destination factory needs these to construct. Every test here writes to a local folder,
    /// so nothing ever calls them — and if something did, the test should fail loudly rather than pretend.</summary>
    private sealed class UnusedAuthProvider : ISshAuthenticationMaterialProvider
    {
        public Task<IReadOnlyList<SshAuthMaterial>> CreateAsync(
            RemoteFlow.Domain.Entities.Connection connection,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("These tests never open a remote destination.");
        }
    }

    private sealed class UnusedTransport : ISshTransport
    {
        public Task<SshResult<ISshConnection>> ConnectAsync(
            SshConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("These tests never open a remote destination.");
        }
    }

    private sealed class UnusedObjectStorageFactory : IObjectStorageClientFactory
    {
        public Task<SftpResult<IObjectStorageService>> CreateAsync(
            Guid connectionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("These tests never open a remote destination.");
        }
    }

    private sealed class CryptoSecureRandom : ISecureRandom
    {
        public byte[] GetBytes(int count)
        {
            return System.Security.Cryptography.RandomNumberGenerator.GetBytes(count);
        }
    }
}
