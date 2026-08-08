using System.Diagnostics;
using System.Text;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

[Collection<SshServerTestGroup>]
public sealed class SftpServiceTests(SshServerFixture fixture)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task NamesMetadataSymlinksAndEveryOperationRoundTrip()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        var names = new[]
        {
            "UTF-8-文件-🚀.txt",
            "spaces 'single' \"double\".txt",
            "literal\nnewline.txt",
            new string('x', 255),
        };

        try
        {
            Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
            foreach (var name in names)
            {
                var path = SftpPath.Combine(directory, name);
                await using var stream = (await sftp.OpenWriteAsync(path, token)).Value;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(name), token);
            }

            var link = SftpPath.Combine(directory, "readme-link");
            var target = SftpPath.Combine(directory, names[0]);
            var linked = await connection.ExecuteAsync(
                $"ln -s {SftpPath.ToShellLiteral(target)} {SftpPath.ToShellLiteral(link)}",
                token);
            Assert.True(linked.IsSuccess);
            Assert.Equal(0, linked.Value.ExitCode);

            var listed = await sftp.ListAsync(directory + "/./", token);
            Assert.True(listed.IsSuccess);
            Assert.All(names, name => Assert.Contains(listed.Value, entry => entry.Name == name));
            var linkInfo = Assert.Single(listed.Value, entry => entry.Name == "readme-link");
            Assert.True(linkInfo.IsSymlink);
            Assert.False(linkInfo.IsDirectory);
            Assert.Equal(target, linkInfo.SymlinkTarget);

            var original = SftpPath.Combine(directory, names[0]);
            var stat = await sftp.StatAsync(original, token);
            Assert.True(stat.IsSuccess);
            Assert.Equal(Encoding.UTF8.GetByteCount(names[0]), stat.Value!.Size);

            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            Assert.True((await sftp.SetPermissionsAsync(original, mode, token)).IsSuccess);
            Assert.Equal(mode, (await sftp.StatAsync(original, token)).Value!.Mode);

            var renamed = SftpPath.Combine(directory, "renamed.txt");
            Assert.True((await sftp.RenameAsync(original, renamed, token)).IsSuccess);
            var missing = await sftp.StatAsync(original, token);
            Assert.True(missing.IsSuccess, missing.IsFailure ? missing.Failure.Message : "Stat unexpectedly failed.");
            Assert.Null(missing.Value);
            Assert.NotNull((await sftp.StatAsync(renamed, token)).Value);
        }
        finally
        {
            Assert.True((await sftp.DeleteAsync(directory, recursive: true, token)).IsSuccess);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PathsPermissionFailuresAndDisposedStreamsAreSafe()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        var child = SftpPath.Combine(directory, "child");
        var file = SftpPath.Combine(directory, "payload.bin");
        try
        {
            Assert.Equal("/alpha/gamma", SftpPath.Normalize("\\alpha\\beta\\..\\gamma\\.\\"));
            Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
            Assert.True((await sftp.CreateDirectoryAsync(child, token)).IsSuccess);
            var mixedPath = directory.Replace('/', '\\') + "\\child\\..\\payload.bin";
            await using (var write = (await sftp.OpenWriteAsync(mixedPath, token)).Value)
            {
                await write.WriteAsync(new byte[256 * 1024], token);
            }

            var opened = await sftp.OpenReadAsync(file, token);
            Assert.True(opened.IsSuccess);
            var buffer = new byte[8192];
            _ = await opened.Value.ReadAsync(buffer, token);
            await opened.Value.DisposeAsync();
            Assert.True((await sftp.ListAsync(directory, token)).IsSuccess);

            var denied = SftpPath.Combine(directory, "denied");
            Assert.True((await sftp.CreateDirectoryAsync(denied, token)).IsSuccess);
            Assert.True((await sftp.SetPermissionsAsync(denied, default, token)).IsSuccess);
            var failure = await sftp.ListAsync(denied, token);
            Assert.True(failure.IsFailure);
            Assert.Equal(SftpError.PermissionDenied, failure.Failure.Error);

            var home = await sftp.GetRealPathAsync("~", token);
            var relative = await sftp.GetRealPathAsync(".", token);
            Assert.True(home.IsSuccess);
            Assert.True(relative.IsSuccess);
            Assert.StartsWith("/home/", home.Value, StringComparison.Ordinal);
            Assert.Equal(home.Value, relative.Value);
        }
        finally
        {
            _ = await fixture.Server.ExecAsync(["chmod", "0700", SftpPath.Combine(directory, "denied")], token);
            _ = await fixture.Server.ExecAsync(["rm", "-rf", directory], token);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FiveThousandEntriesListWithinFifteenSeconds()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        try
        {
            var created = await connection.ExecuteAsync(
                $"mkdir {SftpPath.ToShellLiteral(directory)} && " +
                $"i=1; while [ $i -le 5000 ]; do : > {SftpPath.ToShellLiteral(directory)}/file-$i; i=$((i+1)); done",
                token);
            Assert.True(created.IsSuccess);
            Assert.Equal(0, created.Value.ExitCode);

            var stopwatch = Stopwatch.StartNew();
            var result = await sftp.ListAsync(directory, token);
            stopwatch.Stop();

            Assert.True(result.IsSuccess);
            Assert.Equal(5000, result.Value.Count);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Listing took {stopwatch.Elapsed}.");
        }
        finally
        {
            _ = await fixture.Server.ExecAsync(["rm", "-rf", directory], token);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TransferEngineReportsLargeProgressAndCancelsWithoutFinalOrPartFile()
    {
        const long length = 200L * 1024 * 1024;
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        using var engine = new TransferEngine(sftp);
        var remotePath = $"/tmp/remoteflow-transfer-{Guid.NewGuid():N}.bin";
        var localDirectory = Path.Combine(Path.GetTempPath(), $"remoteflow-transfer-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(localDirectory);
        try
        {
            var created = await connection.ExecuteAsync(
                $"truncate -s {length} {SftpPath.ToShellLiteral(remotePath)}",
                token);
            Assert.True(created.IsSuccess);
            Assert.Equal(0, created.Value.ExitCode);
            var destination = Path.Combine(localDirectory, "complete.bin");
            var updates = new List<TransferProgress>();

            var completed = await engine.DownloadAsync(
                remotePath,
                destination,
                new InlineProgress<TransferProgress>(updates.Add),
                token);

            Assert.True(completed.IsSuccess);
            Assert.Equal(length, new FileInfo(destination).Length);
            Assert.True(updates.Count > 10);
            var final = updates[^1];
            Assert.True(final.IsCompleted);
            Assert.Equal(length, final.BytesTransferred);
            Assert.True(final.BytesPerSecond > 0);
            Assert.Contains(updates, update =>
                !update.IsCompleted && update.BytesPerSecond > 0 && update.EstimatedRemaining is not null);

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var cancelledDestination = Path.Combine(localDirectory, "cancelled.bin");
            var cancellationLatency = new Stopwatch();
            var cancellationStarted = 0;
            var cancelled = await engine.DownloadAsync(
                remotePath,
                cancelledDestination,
                new InlineProgress<TransferProgress>(update =>
                {
                    if (update.BytesTransferred > 0 &&
                        !update.IsCompleted &&
                        Interlocked.Exchange(ref cancellationStarted, 1) == 0)
                    {
                        cancellationLatency.Start();
                        cancellation.Cancel();
                    }
                }),
                cancellation.Token);
            cancellationLatency.Stop();

            Assert.True(cancelled.IsCancelled);
            Assert.True(cancellationLatency.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(File.Exists(cancelledDestination));
            Assert.False(File.Exists(cancelledDestination + ".part"));
            Assert.True((await sftp.StatAsync(remotePath, token)).IsSuccess);
        }
        finally
        {
            _ = await sftp.DeleteAsync(remotePath, recursive: false, CancellationToken.None);
            Directory.Delete(localDirectory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoteEditRestatsImmediatelyBeforeUploadAndCancelKeepsWatching()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-edit-{Guid.NewGuid():N}";
        var remotePath = SftpPath.Combine(directory, "shared.txt");
        var localRoot = CreateTempDirectory();
        Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
        try
        {
            await WriteRemoteAsync(sftp, remotePath, "original", token);
            var monitor = new ManualEditMonitor();
            var resolver = new RecordingEditConflictResolver(RemoteEditConflictResolution.Cancel);
            await using var edits = new RemoteEditService(
                sftp,
                new NoOpEditorLauncher(),
                monitor,
                new AllowCloseGuard(),
                localRoot,
                Guid.NewGuid(),
                resolver,
                new FakeClock(new DateTimeOffset(2026, 8, 8, 12, 34, 56, TimeSpan.Zero)));
            var edit = await edits.OpenAsync(remotePath, token);

            await WriteRemoteAsync(sftp, remotePath, "external", token); // same byte count, possibly same-second mtime
            await File.WriteAllTextAsync(edit.LocalPath, "my-local", token);
            Assert.True(await monitor.TriggerAsync(token));

            var conflict = Assert.Single(resolver.Conflicts);
            Assert.Equal(edit.OriginalRemotePath, conflict.RemotePath);
            Assert.Equal(conflict.DownloadedSnapshot.Size, conflict.CurrentSnapshot.Size);
            Assert.NotEqual(conflict.DownloadedSnapshot.Sha256, conflict.CurrentSnapshot.Sha256);
            Assert.Equal("external", await ReadRemoteAsync(sftp, remotePath, token));
            Assert.True(edit.IsDirty);
            Assert.Equal(1, edits.ActiveCount);
        }
        finally
        {
            _ = await sftp.DeleteAsync(directory, recursive: true, CancellationToken.None);
            Directory.Delete(localRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoteEditKeepBothLeavesOriginalByteIdenticalAndUsesTimestampedName()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-edit-{Guid.NewGuid():N}";
        var remotePath = SftpPath.Combine(directory, "settings.json");
        var keepBothPath = SftpPath.Combine(directory, "settings.remoteflow-20260808-123456.json");
        var localRoot = CreateTempDirectory();
        Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
        try
        {
            await WriteRemoteAsync(sftp, remotePath, "downloaded", token);
            var monitor = new ManualEditMonitor();
            await using var edits = new RemoteEditService(
                sftp,
                new NoOpEditorLauncher(),
                monitor,
                new AllowCloseGuard(),
                localRoot,
                Guid.NewGuid(),
                new RecordingEditConflictResolver(RemoteEditConflictResolution.KeepBoth),
                new FakeClock(new DateTimeOffset(2026, 8, 8, 12, 34, 56, TimeSpan.Zero)));
            var edit = await edits.OpenAsync(remotePath, token);

            await WriteRemoteAsync(sftp, remotePath, "someone-else", token);
            await File.WriteAllTextAsync(edit.LocalPath, "local-copy", token);
            Assert.True(await monitor.TriggerAsync(token));

            Assert.Equal("someone-else", await ReadRemoteAsync(sftp, remotePath, token));
            Assert.Equal("local-copy", await ReadRemoteAsync(sftp, keepBothPath, token));
            Assert.Equal(keepBothPath, edit.RemotePath);
            Assert.False(edit.IsDirty);
        }
        finally
        {
            _ = await sftp.DeleteAsync(directory, recursive: true, CancellationToken.None);
            Directory.Delete(localRoot, recursive: true);
        }
    }

    private async Task<ISshConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var verifier = new HostKeyVerifier(
            new InMemoryHostKeyStore(),
            new AcceptingPrompt(),
            new FakeClock(new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero)),
            new FakeGuidProvider());
        var result = await new TmdsSshTransport(verifier).ConnectAsync(new SshConnectRequest
        {
            Host = fixture.Server.Hostname,
            Port = fixture.Server.Port,
            Username = SshTestServer.PasswordUsername,
            Authentication = new SshAuthMaterial.Password(SshTestServer.Password),
            HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse,
        }, cancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Failure.Message : null);
        return result.Value;
    }

    private static async Task WriteRemoteAsync(
        ISftpService sftp,
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenWriteAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Failure.Message : null);
        await using var stream = opened.Value;
        var bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task<string> ReadRemoteAsync(
        ISftpService sftp,
        string path,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenReadAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Failure.Message : null);
        await using var stream = opened.Value;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualEditMonitor : IWatchedFileMonitor, IWatchedFileSubscription
    {
        private string? _path;
        private Func<WatchedFileChange, CancellationToken, Task<bool>>? _callback;

        public Task<IWatchedFileSubscription> WatchAsync(
            string filePath,
            string initialSha256,
            Func<WatchedFileChange, CancellationToken, Task<bool>> onChanged,
            CancellationToken cancellationToken = default)
        {
            _path = filePath;
            _callback = onChanged;
            return Task.FromResult<IWatchedFileSubscription>(this);
        }

        public Task CheckNowAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public async Task<bool> TriggerAsync(CancellationToken cancellationToken)
        {
            var local = await RemoteEditService.CaptureLocalSnapshotAsync(_path!, cancellationToken);
            return await _callback!(new WatchedFileChange(
                _path!,
                local.Size,
                local.MTimeUtc,
                local.Sha256), cancellationToken);
        }
    }

    private sealed class RecordingEditConflictResolver(RemoteEditConflictResolution resolution) :
        IRemoteEditConflictResolver
    {
        public List<RemoteEditConflict> Conflicts { get; } = [];

        public Task<RemoteEditConflictResolution> ResolveAsync(
            RemoteEditConflict conflict,
            CancellationToken cancellationToken = default)
        {
            Conflicts.Add(conflict);
            return Task.FromResult(resolution);
        }
    }

    private sealed class NoOpEditorLauncher : IFileEditorLauncher
    {
        public Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class AllowCloseGuard : IRemoteEditCloseGuard
    {
        public Task<bool> ConfirmDiscardUnsavedChangesAsync(
            string remotePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class AcceptingPrompt : IHostKeyPrompt
    {
        public ValueTask<bool> ConfirmTrustAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask<HostKeyPromptDecision> PromptAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                prompt.IsMismatch ? HostKeyPromptDecision.Reject : HostKeyPromptDecision.AcceptAndSave);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
