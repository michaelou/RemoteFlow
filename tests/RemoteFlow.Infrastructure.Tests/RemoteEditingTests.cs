using System.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services;
using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

#pragma warning disable IDE0022 // Compact test doubles keep the scenarios readable.

public sealed class RemoteEditingTests
{
    [Fact]
    public async Task AtomicRenameSaveIsDetectedExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = CreateTempDirectory();
        try
        {
            var target = Path.Combine(directory, "script.cs");
            await File.WriteAllTextAsync(target, "before", token);
            var initial = await RemoteEditService.CaptureLocalSnapshotAsync(target, token);
            var calls = 0;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = new WatchedFileMonitor(TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(10));
            await using var watch = await monitor.WatchAsync(target, initial.Sha256, (change, callbackToken) =>
            {
                _ = change;
                _ = callbackToken;
                _ = Interlocked.Increment(ref calls);
                changed.SetResult();
                return Task.FromResult(true);
            }, token);

            var replacement = Path.Combine(directory, ".script.cs.swp");
            await File.WriteAllTextAsync(replacement, "after", token);
            File.Move(replacement, target, overwrite: true);

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            await Task.Delay(220, token);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TimestampOnlyChangeProducesNoUpload()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = CreateTempDirectory();
        try
        {
            var target = Path.Combine(directory, "notes.txt");
            await File.WriteAllTextAsync(target, "same bytes", token);
            var initial = await RemoteEditService.CaptureLocalSnapshotAsync(target, token);
            var calls = 0;
            var monitor = new WatchedFileMonitor(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(10));
            await using var watch = await monitor.WatchAsync(target, initial.Sha256, (_, _) =>
            {
                _ = Interlocked.Increment(ref calls);
                return Task.FromResult(true);
            }, token);

            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
            await watch.CheckNowAsync(token);

            Assert.Equal(0, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TenRapidSavesAreDebouncedIntoOneUpload()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = CreateTempDirectory();
        try
        {
            var target = Path.Combine(directory, "rapid.txt");
            await File.WriteAllTextAsync(target, "0", token);
            var initial = await RemoteEditService.CaptureLocalSnapshotAsync(target, token);
            var calls = 0;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = new WatchedFileMonitor(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
            await using var watch = await monitor.WatchAsync(target, initial.Sha256, (_, _) =>
            {
                _ = Interlocked.Increment(ref calls);
                changed.SetResult();
                return Task.FromResult(true);
            }, token);

            for (var index = 1; index <= 10; index++)
            {
                await File.WriteAllTextAsync(target, index.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
                await Task.Delay(10, token);
            }

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            await Task.Delay(250, token);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteEditPreservesNameUploadsAndCleansSessionFiles()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedAsync(sftp, "/home/test/build.gradle.kts", "original", token);
        var root = CreateTempDirectory();
        var monitor = new ManualMonitor();
        var launcher = new RecordingEditorLauncher();
        var conflictResolver = new RecordingConflictResolver();
        var service = new RemoteEditService(
            sftp,
            launcher,
            monitor,
            new CloseGuard(true),
            root,
            Guid.NewGuid(),
            conflictResolver);
        try
        {
            var edit = await service.OpenAsync("/home/test/build.gradle.kts", token);
            Assert.Equal("build.gradle.kts", Path.GetFileName(edit.LocalPath));
            Assert.Equal(1, service.ActiveCount);
            Assert.Equal(edit.LocalPath, Assert.Single(launcher.Paths));

            await File.WriteAllTextAsync(edit.LocalPath, "changed", token);
            Assert.True(await monitor.TriggerAsync(token));
            var opened = await sftp.OpenReadAsync(edit.RemotePath, token);
            Assert.True(opened.IsSuccess);
            using var reader = new StreamReader(opened.Value);
            Assert.Equal("changed", await reader.ReadToEndAsync(token));
            Assert.Empty(conflictResolver.Conflicts);

            Assert.True(await service.CloseAsync(edit, token));
            Assert.Equal(0, service.ActiveCount);
            Assert.False(File.Exists(edit.LocalPath));
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveOverAnExistingFileKeepsItsPermissionsAndReportsTheUpload()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedAsync(sftp, "/home/test/deploy.sh", "original", token);
        var executable = (UnixFileMode)Convert.ToInt32("0755", 8);
        Assert.True((await sftp.SetPermissionsAsync("/home/test/deploy.sh", executable, token)).IsSuccess);
        var root = CreateTempDirectory();
        var monitor = new ManualMonitor();
        var service = new RemoteEditService(
            sftp,
            new RecordingEditorLauncher(),
            monitor,
            new CloseGuard(true),
            root,
            Guid.NewGuid());
        var uploads = new List<RemoteEditUploadResult>();
        service.UploadCompleted += (_, result) => uploads.Add(result);
        try
        {
            var edit = await service.OpenAsync("/home/test/deploy.sh", token);
            await File.WriteAllTextAsync(edit.LocalPath, "changed", token);

            Assert.True(await monitor.TriggerAsync(token));

            var opened = await sftp.OpenReadAsync("/home/test/deploy.sh", token);
            Assert.True(opened.IsSuccess);
            using (var reader = new StreamReader(opened.Value))
            {
                Assert.Equal("changed", await reader.ReadToEndAsync(token));
            }
            var stat = await sftp.StatAsync("/home/test/deploy.sh", token);
            Assert.Equal(executable, stat.Value!.Mode);
            Assert.False(edit.IsDirty);
            var upload = Assert.Single(uploads);
            Assert.True(upload.Succeeded);
            Assert.Equal("/home/test/deploy.sh", upload.RemotePath);

            var listed = await sftp.ListAsync("/home/test", token);
            Assert.Equal("deploy.sh", Assert.Single(listed.Value).Name);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AFailedSaveIsReportedAndLeavesTheEditDirtyWithTheServerContentIntact()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedAsync(inner, "/home/test/notes.txt", "original", token);
        var sftp = new RefusingRenameSftpService(inner);
        var root = CreateTempDirectory();
        var monitor = new ManualMonitor();
        var service = new RemoteEditService(
            sftp,
            new RecordingEditorLauncher(),
            monitor,
            new CloseGuard(true),
            root,
            Guid.NewGuid());
        var uploads = new List<RemoteEditUploadResult>();
        service.UploadCompleted += (_, result) => uploads.Add(result);
        try
        {
            var edit = await service.OpenAsync("/home/test/notes.txt", token);
            await File.WriteAllTextAsync(edit.LocalPath, "changed", token);

            Assert.False(await monitor.TriggerAsync(token));

            var upload = Assert.Single(uploads);
            Assert.False(upload.Succeeded);
            Assert.Contains("refused", upload.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(edit.IsDirty);
            var opened = await inner.OpenReadAsync("/home/test/notes.txt", token);
            using var reader = new StreamReader(opened.Value);
            Assert.Equal("original", await reader.ReadToEndAsync(token));
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ConflictComparisonDoesNotTrustSameSecondMtimeOrRequireHashForLargeFiles()
    {
        var sameSecond = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.True(RemoteEditService.HasConflict(
            new RemoteSnapshot(10, sameSecond, null),
            new RemoteSnapshot(11, sameSecond, null)));
        Assert.False(RemoteEditService.HasConflict(
            new RemoteSnapshot(RemoteEditService.HashLimitBytes + 1, sameSecond, null),
            new RemoteSnapshot(RemoteEditService.HashLimitBytes + 1, sameSecond, null)));
    }

    [Theory]
    [InlineData(OperatingSystemFamily.Windows, "document.txt", true)]
    [InlineData(OperatingSystemFamily.MacOs, "open", false)]
    [InlineData(OperatingSystemFamily.Linux, "xdg-open", false)]
    public async Task EditorLauncherUsesPlatformDefault(
        OperatingSystemFamily family,
        string expectedExecutable,
        bool shellExecute)
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "document.txt");
            await File.WriteAllTextAsync(path, "text", TestContext.Current.CancellationToken);
            var runner = new RecordingRunner();
            var platform = new StubPlatform(family);
            var launcher = new FileEditorLauncher(platform, runner);

            await launcher.OpenAsync(path, TestContext.Current.CancellationToken);

            var request = Assert.Single(runner.Requests);
            Assert.Equal(shellExecute, request.UseShellExecute);
            Assert.EndsWith(expectedExecutable, request.FileName, StringComparison.OrdinalIgnoreCase);
            if (family != OperatingSystemFamily.Windows)
            {
                Assert.Equal(path, Assert.Single(request.Arguments));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtensionlessFileFallsBackToTheWindowsOpenWithPicker()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "authorized_keys");
            await File.WriteAllTextAsync(path, "text", TestContext.Current.CancellationToken);
            var runner = new RecordingRunner
            {
                FirstLaunchFailure = new Win32Exception(1155), // ERROR_NO_ASSOCIATION
            };
            var launcher = new FileEditorLauncher(new StubPlatform(OperatingSystemFamily.Windows), runner);

            await launcher.OpenAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Null(runner.Requests[0].Verb);
            Assert.Equal("openas", runner.Requests[1].Verb);
            Assert.All(runner.Requests, request =>
            {
                Assert.Equal(path, request.FileName);
                Assert.True(request.UseShellExecute);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DismissingTheWindowsEditorPickerIsNotReportedAsAFailure()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "authorized_keys");
            await File.WriteAllTextAsync(path, "text", TestContext.Current.CancellationToken);
            var runner = new RecordingRunner
            {
                EveryLaunchFailure = new Win32Exception(1223), // ERROR_CANCELLED
            };
            var launcher = new FileEditorLauncher(new StubPlatform(OperatingSystemFamily.Windows), runner);

            await launcher.OpenAsync(path, TestContext.Current.CancellationToken);

            _ = Assert.Single(runner.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SeedAsync(
        FakeSftpService sftp,
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenWriteAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess);
        await using var stream = opened.Value;
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents.AsMemory(), cancellationToken);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualMonitor : IWatchedFileMonitor, IWatchedFileSubscription
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

        public Task CheckNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<bool> TriggerAsync(CancellationToken cancellationToken)
        {
            var snapshot = await RemoteEditService.CaptureLocalSnapshotAsync(_path!, cancellationToken);
            return await _callback!(new WatchedFileChange(
                _path!,
                snapshot.Size,
                snapshot.MTimeUtc,
                snapshot.Sha256), cancellationToken);
        }
    }

    /// <summary>A server that never allows a rename, so nothing can be published.</summary>
    private sealed class RefusingRenameSftpService(ISftpService inner) : ISftpService
    {
        public Task<SftpResult> RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SftpResult.Fail(SftpError.PermissionDenied, "The scripted server refused the rename."));

        public Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);

        public Task<SftpResult<RemoteFileInfo?>> StatAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.StatAsync(path, cancellationToken);

        public Task<SftpResult> CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<SftpResult> DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default) => inner.DeleteAsync(path, recursive, cancellationToken);

        public Task<SftpResult> SetPermissionsAsync(
            string path,
            UnixFileMode mode,
            CancellationToken cancellationToken = default) => inner.SetPermissionsAsync(path, mode, cancellationToken);

        public Task<SftpResult<string>> GetRealPathAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.GetRealPathAsync(path, cancellationToken);

        public Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);

        public Task<SftpResult<Stream>> OpenWriteAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.OpenWriteAsync(path, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RecordingEditorLauncher : IFileEditorLauncher
    {
        public List<string> Paths { get; } = [];

        public Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Paths.Add(filePath);
            return Task.CompletedTask;
        }
    }

    private sealed class CloseGuard(bool result) : IRemoteEditCloseGuard
    {
        public Task<bool> ConfirmDiscardUnsavedChangesAsync(
            string remotePath,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class RecordingConflictResolver : IRemoteEditConflictResolver
    {
        public List<RemoteEditConflict> Conflicts { get; } = [];

        public Task<RemoteEditConflictResolution> ResolveAsync(
            RemoteEditConflict conflict,
            CancellationToken cancellationToken = default)
        {
            Conflicts.Add(conflict);
            return Task.FromResult(RemoteEditConflictResolution.Cancel);
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public Exception? FirstLaunchFailure { get; init; }

        public Exception? EveryLaunchFailure { get; init; }

        public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var failure = EveryLaunchFailure ?? (Requests.Count == 1 ? FirstLaunchFailure : null);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    private sealed class StubPlatform(OperatingSystemFamily family) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem => family;
        public string CurrentDirectory => Environment.CurrentDirectory;
        public string HomeDirectory => Environment.CurrentDirectory;
        public string? GetEnvironmentVariable(string name) => null;
        public string? FindExecutable(string name) => name;
        public bool FileExists(string path) => true;
        public string? GetLoginShellFromPasswd() => null;
    }
}

#pragma warning restore IDE0022
