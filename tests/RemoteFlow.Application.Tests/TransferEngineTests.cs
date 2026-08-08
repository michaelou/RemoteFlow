using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

#pragma warning disable IDE0022 // Expression-bodied pass-through members keep test doubles compact.
#pragma warning disable CA1859 // Interface typing documents the seam exercised by these tests.

namespace RemoteFlow.Application.Tests;

public sealed class TransferEngineTests
{
    [Fact]
    public async Task ZeroAndOneByteFilesRoundTripAndProgressCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var zero = Path.Combine(root, "zero.bin");
            var one = Path.Combine(root, "one.bin");
            await File.WriteAllBytesAsync(zero, [], token);
            await File.WriteAllBytesAsync(one, [42], token);
            var sftp = new FakeSftpService();
            using var engine = new TransferEngine(sftp);
            var updates = new List<TransferProgress>();
            var progress = new InlineProgress<TransferProgress>(updates.Add);

            var zeroUpload = await engine.UploadAsync(zero, "/remote/zero.bin", progress, token);
            var oneUpload = await engine.UploadAsync(one, "/remote/one.bin", progress, token);
            var zeroDownloadPath = Path.Combine(root, "download-zero.bin");
            var oneDownloadPath = Path.Combine(root, "download-one.bin");
            var zeroDownload = await engine.DownloadAsync(
                "/remote/zero.bin",
                zeroDownloadPath,
                progress,
                token);
            var oneDownload = await engine.DownloadAsync(
                "/remote/one.bin",
                oneDownloadPath,
                progress,
                token);

            Assert.True(zeroUpload.IsSuccess);
            Assert.True(oneUpload.IsSuccess);
            Assert.True(zeroDownload.IsSuccess);
            Assert.True(oneDownload.IsSuccess);
            Assert.Empty(await File.ReadAllBytesAsync(zeroDownloadPath, token));
            Assert.Equal([42], await File.ReadAllBytesAsync(oneDownloadPath, token));
            Assert.Contains(updates, update =>
                update.IsCompleted && update.TotalBytes == 0 && update.BytesTransferred == 0);
            Assert.Contains(updates, update =>
                update.IsCompleted && update.TotalBytes == 1 && update.BytesTransferred == 1);
            Assert.DoesNotContain(updates, update => double.IsNaN(update.BytesPerSecond));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecursiveUploadPreservesStructureAndReportsEveryFile()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "root", token);
            await File.WriteAllTextAsync(Path.Combine(nested.FullName, "child.txt"), "child", token);
            var sftp = new FakeSftpService();
            using var engine = new TransferEngine(sftp);
            var completed = new HashSet<string>(StringComparer.Ordinal);
            var progress = new InlineProgress<TransferProgress>(update =>
            {
                if (update.IsCompleted)
                {
                    _ = completed.Add(update.DestinationPath);
                }
            });

            var result = await engine.UploadAsync(root, "/tree", progress, token);

            Assert.True(result.IsSuccess);
            Assert.NotNull((await sftp.StatAsync("/tree/root.txt", token)).Value);
            Assert.NotNull((await sftp.StatAsync("/tree/nested/child.txt", token)).Value);
            Assert.Contains("/tree/root.txt", completed);
            Assert.Contains("/tree/nested/child.txt", completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingTargetRequiresResolverAndNeverClobbersByDefault()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "new", token);
            var sftp = new FakeSftpService();
            await SeedRemoteAsync(sftp, "/target.txt", "old"u8.ToArray(), token);
            using var engineWithoutPrompt = new TransferEngine(sftp);

            var blocked = await engineWithoutPrompt.UploadAsync(source, "/target.txt", cancellationToken: token);
            await using var existingStream = (await sftp.OpenReadAsync("/target.txt", token)).Value;
            using var existingReader = new StreamReader(existingStream);

            Assert.False(blocked.IsSuccess);
            Assert.Equal(TransferItemStatus.Conflict, Assert.Single(blocked.Items).Status);
            Assert.Equal("old", await existingReader.ReadToEndAsync(token));

            var resolver = new RecordingResolver(TransferConflictDecision.Overwrite);
            using var engineWithPrompt = new TransferEngine(sftp, resolver);
            var overwritten = await engineWithPrompt.UploadAsync(source, "/target.txt", cancellationToken: token);

            Assert.True(overwritten.IsSuccess);
            await using var replacedStream = (await sftp.OpenReadAsync("/target.txt", token)).Value;
            using var replacedReader = new StreamReader(replacedStream);
            Assert.Equal("new", await replacedReader.ReadToEndAsync(token));
            var conflict = Assert.Single(resolver.Conflicts);
            Assert.Equal(TransferDirection.Upload, conflict.Direction);
            Assert.Equal("/target.txt", conflict.DestinationPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledDownloadLeavesNeitherFinalNorPartFile()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var inner = new FakeSftpService();
            await SeedRemoteAsync(inner, "/large.bin", new byte[128], token);
            var blocking = new BlockingReadSftpService(inner);
            using var engine = new TransferEngine(blocking);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var destination = Path.Combine(root, "large.bin");

            var transfer = engine.DownloadAsync("/large.bin", destination, cancellationToken: cancellation.Token);
            await blocking.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            cancellation.Cancel();
            var result = await transfer.WaitAsync(TimeSpan.FromSeconds(5), token);

            Assert.True(result.IsCancelled);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledUploadLeavesNeitherFinalNorPartFile()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "large.bin");
            await File.WriteAllBytesAsync(source, new byte[128], token);
            var inner = new FakeSftpService();
            var gated = new GatedWriteSftpService(inner);
            using var engine = new TransferEngine(gated);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

            var transfer = engine.UploadAsync(source, "/large.bin", cancellationToken: cancellation.Token);
            await WaitUntilAsync(() => gated.ActiveWrites == 1, TimeSpan.FromSeconds(5), token);
            cancellation.Cancel();
            var result = await transfer.WaitAsync(TimeSpan.FromSeconds(5), token);

            Assert.True(result.IsCancelled);
            Assert.Null((await inner.StatAsync("/large.bin", token)).Value);
            Assert.Null((await inner.StatAsync("/large.bin.part", token)).Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecursiveDownloadPreservesDirectoryStructure()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var sftp = new FakeSftpService();
            Assert.True((await sftp.CreateDirectoryAsync("/tree", token)).IsSuccess);
            Assert.True((await sftp.CreateDirectoryAsync("/tree/nested", token)).IsSuccess);
            await SeedRemoteAsync(sftp, "/tree/root.txt", "root"u8.ToArray(), token);
            await SeedRemoteAsync(sftp, "/tree/nested/child.txt", "child"u8.ToArray(), token);
            using var engine = new TransferEngine(sftp);

            var result = await engine.DownloadAsync("/tree", root, cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Equal("root", await File.ReadAllTextAsync(Path.Combine(root, "root.txt"), token));
            Assert.Equal(
                "child",
                await File.ReadAllTextAsync(Path.Combine(root, "nested", "child.txt"), token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCapsConcurrencyAndCancellingOneWaiterDoesNotDisturbOthers()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var files = Enumerable.Range(0, 6)
                .Select(index => Path.Combine(root, $"file-{index}.bin"))
                .ToArray();
            foreach (var file in files)
            {
                await File.WriteAllBytesAsync(file, [1], token);
            }

            var gated = new GatedWriteSftpService(new FakeSftpService());
            using var engine = new TransferEngine(
                gated,
                options: new TransferOptions { MaxConcurrentTransfers = 3 });
            using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var tasks = files.Select((file, index) => engine.UploadAsync(
                file,
                $"/file-{index}.bin",
                cancellationToken: index == 3 ? queuedCancellation.Token : token)).ToArray();

            await WaitUntilAsync(() => gated.ActiveWrites == 3, TimeSpan.FromSeconds(5), token);
            queuedCancellation.Cancel();
            gated.ReleaseWrites();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), token);

            Assert.Equal(3, gated.MaximumActiveWrites);
            Assert.True(results[3].IsCancelled);
            Assert.All(results.Where((_, index) => index != 3), result => Assert.True(result.IsSuccess));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TransientFailureRetriesOnceButPermissionFailureDoesNotRetry()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var inner = new FakeSftpService();
            await SeedRemoteAsync(inner, "/source.bin", [7], token);
            var transient = new FailingOpenReadSftpService(
                inner,
                [new SftpFailure(SftpError.ConnectionLost, "transient")]);
            using var retryingEngine = new TransferEngine(transient);

            var retried = await retryingEngine.DownloadAsync(
                "/source.bin",
                Path.Combine(root, "retried.bin"),
                cancellationToken: token);

            Assert.True(retried.IsSuccess);
            Assert.Equal(2, transient.OpenReadCalls);

            var denied = new FailingOpenReadSftpService(
                inner,
                [new SftpFailure(SftpError.PermissionDenied, "denied")]);
            using var nonRetryingEngine = new TransferEngine(denied);
            var failed = await nonRetryingEngine.DownloadAsync(
                "/source.bin",
                Path.Combine(root, "denied.bin"),
                cancellationToken: token);

            Assert.False(failed.IsSuccess);
            Assert.Equal(SftpError.PermissionDenied, Assert.Single(failed.Items).Failure!.Error);
            Assert.Equal(1, denied.OpenReadCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedRemoteAsync(
        ISftpService sftp,
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        await using var stream = (await sftp.OpenWriteAsync(path, cancellationToken)).Value;
        await stream.WriteAsync(contents, cancellationToken);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remoteflow-transfer-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (!predicate())
        {
            await Task.Delay(10, timeoutSource.Token);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private sealed class RecordingResolver(TransferConflictDecision decision) : ITransferConflictResolver
    {
        public List<TransferConflict> Conflicts { get; } = [];

        public ValueTask<TransferConflictDecision> ResolveAsync(
            TransferConflict conflict,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Conflicts.Add(conflict);
            return ValueTask.FromResult(decision);
        }
    }

    private abstract class DelegatingSftpService(ISftpService inner) : ISftpService
    {
        protected ISftpService Inner { get; } = inner;

        public virtual Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.ListAsync(path, cancellationToken);

        public virtual Task<SftpResult<RemoteFileInfo?>> StatAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.StatAsync(path, cancellationToken);

        public virtual Task<SftpResult> CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.CreateDirectoryAsync(path, cancellationToken);

        public virtual Task<SftpResult> RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default) =>
            Inner.RenameAsync(sourcePath, destinationPath, cancellationToken);

        public virtual Task<SftpResult> DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default) => Inner.DeleteAsync(path, recursive, cancellationToken);

        public virtual Task<SftpResult> SetPermissionsAsync(
            string path,
            UnixFileMode mode,
            CancellationToken cancellationToken = default) =>
            Inner.SetPermissionsAsync(path, mode, cancellationToken);

        public virtual Task<SftpResult<string>> GetRealPathAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.GetRealPathAsync(path, cancellationToken);

        public virtual Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.OpenReadAsync(path, cancellationToken);

        public virtual Task<SftpResult<Stream>> OpenWriteAsync(
            string path,
            CancellationToken cancellationToken = default) => Inner.OpenWriteAsync(path, cancellationToken);

        public virtual ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class BlockingReadSftpService(ISftpService inner) : DelegatingSftpService(inner)
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SftpResult<Stream>.Success(new BlockingReadStream(ReadStarted)));
        }
    }

    private sealed class BlockingReadStream(TaskCompletionSource started) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingOpenReadSftpService(
        ISftpService inner,
        IEnumerable<SftpFailure> failures) : DelegatingSftpService(inner)
    {
        private readonly Queue<SftpFailure> _failures = new(failures);

        public int OpenReadCalls { get; private set; }

        public override Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            OpenReadCalls++;
            return _failures.TryDequeue(out var failure)
                ? Task.FromResult(SftpResult<Stream>.Fail(failure.Error, failure.Message))
                : base.OpenReadAsync(path, cancellationToken);
        }
    }

    private sealed class GatedWriteSftpService(ISftpService inner) : DelegatingSftpService(inner)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeWrites;
        private int _maximumActiveWrites;

        public int ActiveWrites => Volatile.Read(ref _activeWrites);

        public int MaximumActiveWrites => Volatile.Read(ref _maximumActiveWrites);

        public void ReleaseWrites()
        {
            _ = _release.TrySetResult();
        }

        public override async Task<SftpResult<Stream>> OpenWriteAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var opened = await base.OpenWriteAsync(path, cancellationToken);
            if (opened.IsFailure)
            {
                return opened;
            }

            var active = Interlocked.Increment(ref _activeWrites);
            SetMaximum(active);
            return SftpResult<Stream>.Success(new GatedWriteStream(
                opened.Value,
                _release.Task,
                () => Interlocked.Decrement(ref _activeWrites)));
        }

        private void SetMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveWrites);
                if (current >= value || Interlocked.CompareExchange(ref _maximumActiveWrites, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class GatedWriteStream(
        Stream inner,
        Task release,
        Action disposed) : Stream
    {
        private int _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                disposed();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync();
                disposed();
            }

            await base.DisposeAsync();
        }
    }
}

#pragma warning restore CA1859
#pragma warning restore IDE0022
