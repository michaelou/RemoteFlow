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

/// <summary>Retention deletes files from somewhere the user chose. These tests are about what it must
/// refuse to touch at least as much as what it should clean up.</summary>
public sealed class AutoBackupRetentionTests
{
    private static readonly DateTimeOffset _start = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task KeepsTheConfiguredNumberOfNewestArchives()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 3);

        for (var run = 0; run < 5; run++)
        {
            _ = await harness.RunAsync();
        }

        Assert.Equal(3, harness.Destination.Names.Count);
        Assert.Equal(
            [.. harness.Destination.Names.OrderByDescending(name => name, StringComparer.Ordinal).Take(3)],
            [.. harness.Destination.Names.OrderByDescending(name => name, StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task NeverDeletesAFileItCannotParse()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 1);
        harness.Destination.Foreign.Add("notes.zip");
        harness.Destination.Foreign.Add("RemoteFlow-backup-20260824-120000.zip");
        harness.Destination.Foreign.Add("remoteflow-auto-20260824T131500Z-9f3a01bc.rfbak.zip.part");

        _ = await harness.RunAsync();
        _ = await harness.RunAsync();
        _ = await harness.RunAsync();

        _ = Assert.Single(harness.Destination.Names);
        Assert.Equal(3, harness.Destination.Foreign.Count);
    }

    [Fact]
    public async Task NeverDeletesTheArchiveItJustWrote()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 1);

        _ = await harness.RunAsync();
        var first = Assert.Single(harness.Destination.Names);
        _ = await harness.RunAsync();

        var second = Assert.Single(harness.Destination.Names);
        Assert.NotEqual(first, second);
    }

    /// <summary>If the listing does not contain the archive we just published then this is not the view of
    /// the destination we think it is — and deleting on that assumption is how everything gets lost at once.</summary>
    [Fact]
    public async Task AbortsWhenTheNewArchiveIsMissingFromTheListing()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 1);
        _ = await harness.RunAsync();
        _ = await harness.RunAsync();
        _ = Assert.Single(harness.Destination.Names);

        harness.Destination.HideNewestFromListing = true;
        var status = await harness.RunAsync();

        Assert.Equal(AutoBackupOutcome.Succeeded, status.Outcome);
        Assert.Equal(2, harness.Destination.Names.Count);
        Assert.Contains("not visible", status.Message, StringComparison.Ordinal);
    }

    /// <summary>An imported configuration can carry a zero. Read literally that means "keep nothing", which
    /// would delete the archive the run just made.</summary>
    [Fact]
    public async Task ClampsARetentionOfZeroToOne()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 0);

        _ = await harness.RunAsync();
        _ = await harness.RunAsync();

        _ = Assert.Single(harness.Destination.Names);
    }

    [Fact]
    public async Task ADeleteFailureIsReportedWithoutFailingTheRun()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 1);
        _ = await harness.RunAsync();
        harness.Destination.RefuseDeletes = true;

        var status = await harness.RunAsync();

        // A destination that will not let us prune should still receive backups.
        Assert.Equal(AutoBackupOutcome.Succeeded, status.Outcome);
        Assert.Equal(2, harness.Destination.Names.Count);
        Assert.Contains("could not be deleted", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoweringRetentionDeletesNothingUntilTheNextSuccessfulRun()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 5);
        for (var run = 0; run < 5; run++)
        {
            _ = await harness.RunAsync();
        }

        await harness.SetRetentionAsync(2).ConfigureAwait(true);
        Assert.Equal(5, harness.Destination.Names.Count);

        _ = await harness.RunAsync();

        Assert.Equal(2, harness.Destination.Names.Count);
    }

    [Fact]
    public async Task AListingFailureLeavesEveryArchiveAlone()
    {
        await using var harness = await RetentionHarness.CreateAsync(retainedCopies: 1);
        _ = await harness.RunAsync();
        _ = await harness.RunAsync();
        harness.Destination.RefuseListing = true;

        var status = await harness.RunAsync();

        Assert.Equal(AutoBackupOutcome.Succeeded, status.Outcome);
        Assert.Equal(2, harness.Destination.Names.Count);
        Assert.Contains("could not be listed", status.Message, StringComparison.Ordinal);
    }

    private sealed class RetentionHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly InMemorySettingsStore _settings = new();
        private readonly FakeClock _clock = new(_start);

        private RetentionHarness(string root)
        {
            _root = root;
            Destination = new CountingDestination(Path.Combine(root, "staging"));
            Runner = new AutoBackupRunner(
                _settings,
                new StubBackupService(),
                new SingleDestinationFactory(Destination),
                new StubPassphraseStore(),
                new NullStatusStore(),
                new ConnectionChangeNotifier(),
                new WorkspaceChangeNotifier(),
                _clock,
                SystemGuidProvider.Instance,
                new StubAppPaths(root),
                new FakeTimeProvider(_start));
        }

        public CountingDestination Destination { get; }

        public AutoBackupRunner Runner { get; }

        public static async Task<RetentionHarness> CreateAsync(int retainedCopies)
        {
            var root = Path.Combine(Path.GetTempPath(), "remoteflow-retention-tests", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(root);
            var harness = new RetentionHarness(root);
            await harness.SetRetentionAsync(retainedCopies);
            await harness.Runner.InitializeAsync(CancellationToken.None);
            return harness;
        }

        public Task SetRetentionAsync(int retainedCopies)
        {
            return _settings.Set(
                SettingKeys.AutoBackup,
                new AutoBackupOptions { IsEnabled = true, RetainedCopies = retainedCopies },
                CancellationToken.None);
        }

        public async Task<AutoBackupStatus> RunAsync()
        {
            // Each run gets its own second so the names sort the way real ones would.
            _clock.Advance(TimeSpan.FromMinutes(1));
            return await Runner.RunNowAsync(CancellationToken.None);
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
                // Not a test failure.
            }
        }
    }

    private sealed class CountingDestination(string stagingRoot) : IAutoBackupDestination
    {
        public string Description => "counting destination";

        public List<string> Names { get; } = [];

        /// <summary>Files that are not ours. Nothing in here may ever be deleted.</summary>
        public List<string> Foreign { get; } = [];

        public bool RefuseDeletes { get; set; }

        public bool RefuseListing { get; set; }

        public bool HideNewestFromListing { get; set; }

        public string GetStagingPath(string fileName)
        {
            _ = Directory.CreateDirectory(stagingRoot);
            return Path.Combine(stagingRoot, fileName);
        }

        public Task<SftpResult> PublishAsync(
            string stagingPath,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            Names.Add(fileName);
            return Task.FromResult(SftpResult.Success());
        }

        public Task<SftpResult<IReadOnlyList<AutoBackupArchive>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            if (RefuseListing)
            {
                return Task.FromResult(SftpResult<IReadOnlyList<AutoBackupArchive>>.Fail(
                    SftpError.PermissionDenied, "listing is not allowed"));
            }

            // Foreign names go through the same parser a real destination would use, so anything the parser
            // rejects simply never reaches retention.
            var candidates = Names.Concat(Foreign).ToList();
            if (HideNewestFromListing && Names.Count > 0)
            {
                _ = candidates.Remove(Names[^1]);
            }

            IReadOnlyList<AutoBackupArchive> archives =
            [
                .. candidates
                    .Where(name => AutoBackupNaming.TryParse(name, out _))
                    .Select(name =>
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
            if (RefuseDeletes)
            {
                return Task.FromResult(SftpResult.Fail(SftpError.PermissionDenied, "deletes are not allowed"));
            }

            Assert.DoesNotContain(archive.Name, Foreign);
            _ = Names.Remove(archive.Name);
            return Task.FromResult(SftpResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleDestinationFactory(IAutoBackupDestination destination) : IAutoBackupDestinationFactory
    {
        public Task<SftpResult<IAutoBackupDestination>> CreateAsync(
            AutoBackupDestination target,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SftpResult<IAutoBackupDestination>.Success(destination));
        }
    }

    private sealed class StubBackupService : IBackupService
    {
        public bool CanExportCredentials => true;

        public async Task<BackupExportResult> ExportAsync(
            BackupExportRequest request,
            IProgress<BackupProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(request.DestinationPath, "archive", cancellationToken);
            return new BackupExportResult(request.DestinationPath, new BackupEntityCounts(1, 1, 1, 1, 1, 1));
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

    private sealed class StubPassphraseStore : IAutoBackupPassphraseStore
    {
        public bool IsAvailable => true;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult("test keyring");
        }

        public Task<AutoBackupPassphraseState> InspectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AutoBackupPassphraseState.Present);
        }

        public Task<SecretHandle?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(new SecretHandle("correct-horse-Battery9!"));
        }

        public Task<Result<bool>> SetAsync(
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NullStatusStore : IAutoBackupStatusStore
    {
        public Task<AutoBackupStatus?> ReadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AutoBackupStatus?>(
                new AutoBackupStatus { Outcome = AutoBackupOutcome.Succeeded, PendingChanges = false });
        }

        public Task WriteAsync(AutoBackupStatus status, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
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
            _ = Directory.CreateDirectory(DataDirectory);
        }
    }
}
