using Microsoft.Extensions.Time.Testing;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Everything a chunked transfer promises, asserted against the in-memory store: contiguous
/// parts, ordered completion, bounded concurrency, monotonic progress, and — the one that costs real money
/// when it is wrong — exactly one abort, with a token that was not itself cancelled.</summary>
public sealed class ObjectStorageTransferEngineTests
{
    private const int _partSize = 8192;

    [Theory]
    [InlineData(100_000)]
    [InlineData(_partSize * 4)]
    [InlineData(_partSize + 1)]
    public async Task PartBoundariesAreContiguousAndTheStoredObjectMatchesTheSource(int size)
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            var content = Payload(size);
            await File.WriteAllBytesAsync(source, content, token);
            await using var store = ChunkedStore();
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Equal(content, await ReadObjectAsync(store, "/archive/big.bin", token));
            var plan = ObjectPartPlanner.Plan(size, Limits()).Value;
            Assert.Equal([.. Enumerable.Range(1, plan.Parts.Count)], store.CompletedPartNumbers);

            var uploaded = store.PartAttempts
                .Where(attempt => attempt.Succeeded)
                .OrderBy(attempt => attempt.PartNumber)
                .ToArray();
            Assert.Equal(plan.Parts.Count, uploaded.Length);
            var offset = 0L;
            foreach (var (part, attempt) in plan.Parts.Zip(uploaded))
            {
                // No gap and no overlap: each part begins exactly where the last one ended, and the bytes
                // that actually went over the wire are the bytes the plan asked for.
                Assert.Equal(offset, part.Offset);
                Assert.Equal(part.Length, attempt.DeclaredLength);
                Assert.Equal(part.Length, attempt.BytesRead);
                offset += part.Length;
            }

            Assert.Equal(size, offset);
            Assert.InRange(store.MaxConcurrentParts, 1, 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteReceivesPartsInOrderEvenWhenTheyFinishOutOfOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            var content = Payload(_partSize * 6);
            await File.WriteAllBytesAsync(source, content, token);
            await using var store = ChunkedStore();
            store.StallPart(1);
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var upload = engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);
            while (store.CompletedPartCount < 5)
            {
                await Task.Delay(5, token);
            }

            store.ReleasePart(1);
            var result = await upload;

            Assert.True(result.IsSuccess);
            Assert.NotEqual(1, store.PartCompletionOrder[0]);
            Assert.Equal([1, 2, 3, 4, 5, 6], store.CompletedPartNumbers);
            Assert.Equal(content, await ReadObjectAsync(store, "/archive/big.bin", token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ATransientPartFailureIsRetriedOnTheExactBackoffAndTheTransferSucceeds()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            var content = Payload(_partSize * 5);
            await File.WriteAllBytesAsync(source, content, token);
            await using var store = ChunkedStore();
            store.FailPart(3, times: 2);
            var clock = new FakeTimeProvider();
            var recorder = new RecordingTimeProvider(clock);
            var started = clock.GetUtcNow();
            var engine = new ObjectStorageTransferEngine(
                store,
                options: ChunkedOptions(),
                timeProvider: recorder);

            var result = await engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Equal(3, store.AttemptsFor(3));
            Assert.Equal(content, await ReadObjectAsync(store, "/archive/big.bin", token));

            // Backoff is tested by advancing a virtual clock, never by sleeping: 500 ms then 1 s, with the
            // jitter pinned to zero so the numbers are exact rather than approximately right.
            Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)], recorder.Delays);
            Assert.Equal(TimeSpan.FromMilliseconds(1_500), clock.GetUtcNow() - started);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ANonTransientPartFailureIsNotRetriedAndStillAbortsExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            await File.WriteAllBytesAsync(source, Payload(_partSize * 5), token);
            await using var store = ChunkedStore();
            store.FailPart(2, times: 1, transient: false);
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);

            Assert.False(result.IsSuccess);
            Assert.Equal(1, store.AttemptsFor(2));
            Assert.Equal(0, store.CompleteCount);
            var abort = Assert.Single(store.Aborts);
            Assert.False(abort.TokenWasCancelled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingMidTransferAbortsOnceWithATokenThatWasNotCancelled(bool abortIsNoOp)
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            await File.WriteAllBytesAsync(source, Payload(_partSize * 6), token);
            await using var store = ChunkedStore(abortIsNoOp);
            store.StallPart(1);
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());
            using var cancellation = new CancellationTokenSource();

            var upload = engine.UploadAsync(source, "/archive/big.bin", cancellationToken: cancellation.Token);
            while (store.CompletedPartCount < 3)
            {
                await Task.Delay(5, token);
            }

            await cancellation.CancelAsync();
            var result = await upload;

            Assert.True(result.IsCancelled);
            Assert.Equal(0, store.CompleteCount);

            // The bug this asserts against: handing the abort the cancelled token means the abort is
            // itself cancelled and the parts survive, billed. An adapter whose abort is a no-op — Azure —
            // behaves identically, which is what makes the opaque handle an honest abstraction.
            var abort = Assert.Single(store.Aborts);
            Assert.False(abort.TokenWasCancelled);
            Assert.DoesNotContain(store.Keys, key => key == "archive/big.bin");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteFailingStillAbortsExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            await File.WriteAllBytesAsync(source, Payload(_partSize * 4), token);
            await using var store = ChunkedStore();
            store.FailComplete(times: 5);
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);

            Assert.False(result.IsSuccess);
            Assert.Equal(1, store.CompleteCount);
            var abort = Assert.Single(store.Aborts);
            Assert.False(abort.TokenWasCancelled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AFailingAbortIsReportedRatherThanSwallowed()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            await File.WriteAllBytesAsync(source, Payload(_partSize * 4), token);
            await using var store = ChunkedStore();
            store.FailComplete(times: 5);
            store.FailAbort();
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(source, "/archive/big.bin", cancellationToken: token);

            var item = Assert.Single(result.Items);
            Assert.Equal(TransferItemStatus.Failed, item.Status);

            // Money, not cosmetics: a client that cannot promise the parts are gone must not imply it.
            Assert.Contains("may be billed", item.Failure!.Message, StringComparison.Ordinal);
            _ = Assert.Single(store.Aborts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProgressIsNonDecreasingAcrossARetryAndEndsAtTheTotal()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "big.bin");
            var size = _partSize * 6;
            await File.WriteAllBytesAsync(source, Payload(size), token);
            await using var store = ChunkedStore();
            store.FailPart(2, times: 1);
            var engine = new ObjectStorageTransferEngine(
                store,
                options: ChunkedOptions(progressInterval: TimeSpan.FromTicks(1), retryDelay: TimeSpan.FromMilliseconds(1)));
            var updates = new List<TransferProgress>();
            var progress = new InlineProgress<TransferProgress>(updates.Add);

            var result = await engine.UploadAsync(source, "/archive/big.bin", progress, token);

            Assert.True(result.IsSuccess);
            for (var index = 1; index < updates.Count; index++)
            {
                Assert.True(
                    updates[index].BytesTransferred >= updates[index - 1].BytesTransferred,
                    $"Progress went backwards: {updates[index - 1].BytesTransferred} then " +
                    $"{updates[index].BytesTransferred}.");
            }

            Assert.True(updates[^1].IsCompleted);
            Assert.Equal(size, updates[^1].BytesTransferred);
            Assert.Equal(size, updates[^1].TotalBytes);
            Assert.DoesNotContain(updates, update => double.IsNaN(update.BytesPerSecond));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnObjectAtTheSingleShotThresholdMakesOnePutAndNoMultipartCalls()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "small.bin");
            var content = Payload(1_000);
            await File.WriteAllBytesAsync(source, content, token);
            await using var store = ChunkedStore();
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(source, "/archive/small.bin", cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, store.WriteCount);
            Assert.Equal(0, store.StartUploadCount);
            Assert.Equal(content, await ReadObjectAsync(store, "/archive/small.bin", token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheDownloadPartFileIsPreallocatedAndSurvivesAnOutOfOrderRangeRetry()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var content = Payload(100_000);
            await using var store = ChunkedStore();
            store.Seed("/archive/big.bin", content);

            // The first range comes back short, the way a dropped connection looks from the client, so
            // range zero finishes last and its progress has to be monotonic across the retry.
            store.TruncateRange(0, 1);
            var target = Path.Combine(root, "out.bin");
            var engine = new ObjectStorageTransferEngine(
                store,
                options: ChunkedOptions(
                    progressInterval: TimeSpan.FromTicks(1),
                    retryDelay: TimeSpan.FromMilliseconds(1)));
            var lengths = new List<long>();
            var updates = new List<TransferProgress>();
            var progress = new InlineProgress<TransferProgress>(update =>
            {
                updates.Add(update);
                if (update.BytesTransferred > 0 && lengths.Count == 0)
                {
                    lengths.Add(new FileInfo(target + ".part").Length);
                }
            });

            var result = await engine.DownloadAsync("/archive/big.bin", target, progress, token);

            Assert.True(result.IsSuccess);
            Assert.Equal(content, await File.ReadAllBytesAsync(target, token));

            // Preallocated to the full length before the first byte landed: a full disk is discovered at
            // the start of a 500 GB download rather than 499 GB into it.
            Assert.Equal(content.Length, Assert.Single(lengths));
            Assert.False(File.Exists(target + ".part"));
            for (var index = 1; index < updates.Count; index++)
            {
                Assert.True(updates[index].BytesTransferred >= updates[index - 1].BytesTransferred);
            }

            Assert.InRange(store.MaxConcurrentReads, 1, 4);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingADownloadLeavesNeitherTheFileNorThePartBehind()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            await using var store = ChunkedStore();
            store.Seed("/archive/big.bin", Payload(200_000));
            store.StallRange(0);
            var target = Path.Combine(root, "out.bin");
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());
            using var cancellation = new CancellationTokenSource();

            var download = engine.DownloadAsync(
                "/archive/big.bin",
                target,
                cancellationToken: cancellation.Token);
            while (store.RangedReadCount < 3)
            {
                await Task.Delay(5, token);
            }

            await cancellation.CancelAsync();
            var result = await download;

            Assert.True(result.IsCancelled);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".part"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AZeroOrSmallObjectTakesTheSingleStreamPathWithNoRangedRequest()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            await using var store = ChunkedStore();
            store.Seed("/archive/empty.bin", []);
            store.Seed("/archive/small.bin", Payload(1_000));
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var empty = await engine.DownloadAsync(
                "/archive/empty.bin",
                Path.Combine(root, "empty.bin"),
                cancellationToken: token);
            var small = await engine.DownloadAsync(
                "/archive/small.bin",
                Path.Combine(root, "small.bin"),
                cancellationToken: token);

            Assert.True(empty.IsSuccess);
            Assert.True(small.IsSuccess);
            Assert.Equal(0, store.RangedReadCount);
            Assert.Equal(2, store.WholeReadCount);
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(root, "empty.bin"), token));
            Assert.Equal(1_000, new FileInfo(Path.Combine(root, "small.bin")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ARecursiveDownloadPagesTheListingRatherThanMaterialisingIt()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            await using var store = ChunkedStore();
            store.PageSizeCap = 2;
            store.Seed("/archive/logs/one.txt", Payload(10));
            store.Seed("/archive/logs/two.txt", Payload(20));
            store.Seed("/archive/logs/three.txt", Payload(30));
            store.Seed("/archive/logs/four.txt", Payload(40));
            store.Seed("/archive/logs/2026/app.log", Payload(50));
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.DownloadAsync("/archive/logs", root, cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Equal(5, result.Items.Count);
            Assert.True(File.Exists(Path.Combine(root, "one.txt")));
            Assert.True(File.Exists(Path.Combine(root, "2026", "app.log")));

            // Two keys per page across two prefixes: a single listing call would have been the bug.
            Assert.True(store.ListCount > 2, $"Only {store.ListCount} listing calls were made.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ARecursiveUploadMirrorsTheTreeAndCreatesItsFolderMarkers()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "root", token);
            await File.WriteAllTextAsync(Path.Combine(nested.FullName, "child.txt"), "child", token);
            await using var store = ChunkedStore();
            var engine = new ObjectStorageTransferEngine(store, options: ChunkedOptions());

            var result = await engine.UploadAsync(root, "/archive/backup", cancellationToken: token);

            Assert.True(result.IsSuccess);
            Assert.Contains("archive/backup/root.txt", store.Keys);
            Assert.Contains("archive/backup/nested/child.txt", store.Keys);
            Assert.Contains("archive/backup/nested/", store.Keys);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ObjectPartLimits Limits()
    {
        return new ObjectPartLimits(1024, _partSize, 10_000);
    }

    private static ObjectTransferOptions ChunkedOptions(
        TimeSpan? progressInterval = null,
        TimeSpan? retryDelay = null)
    {
        return new ObjectTransferOptions
        {
            SingleShotThreshold = 4_096,
            CopyBufferSize = 1_024,
            MaxPartsInFlight = 4,
            InitialRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500),
            Jitter = _ => TimeSpan.Zero,
            ProgressInterval = progressInterval ?? TimeSpan.FromMilliseconds(250),
            PartLimits = Limits(),
        };
    }

    private static InMemoryObjectStorage ChunkedStore(bool abortIsNoOp = false)
    {
        // Real provider limits scaled down, so many parts come out of an object a test can hold in memory
        // without changing a line of the ladder under test.
        var store = new InMemoryObjectStorage(abortIsNoOp)
        {
            MinimumPartSize = 1_024,
            MaximumPartSize = _partSize,
            MaximumPartCount = 10_000,
        };
        store.AddContainer("archive");
        return store;
    }

    private static byte[] Payload(int size)
    {
        var content = new byte[size];
        new Random(size).NextBytes(content);
        return content;
    }

    private static async Task<byte[]> ReadObjectAsync(
        InMemoryObjectStorage store,
        string path,
        CancellationToken cancellationToken)
    {
        var read = await store.OpenReadAsync(path, cancellationToken: cancellationToken);
        var stream = read.Value;
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "remoteflow-objects-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    /// <summary>Records the delay every <c>Task.Delay</c> asked for and advances a
    /// <see cref="FakeTimeProvider"/> by exactly that much, so backoff is asserted rather than slept
    /// through. The timer itself fires immediately, which is what keeps the test deterministic.</summary>
    private sealed class RecordingTimeProvider(FakeTimeProvider clock) : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<TimeSpan> _delays = [];

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_sync)
                {
                    return [.. _delays];
                }
            }
        }

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow()
        {
            return clock.GetUtcNow();
        }

        public override long GetTimestamp()
        {
            return clock.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                _delays.Add(dueTime);
                clock.Advance(dueTime);
            }

            return System.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }
}
