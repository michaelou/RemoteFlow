using System.Text;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Exercises the object-storage contracts against the in-memory store. No network, no SDK: what
/// is under test is that the contract shape supports the operations the Storage page and the transfer
/// engine will need.</summary>
public sealed class ObjectStorageContractTests
{
    [Fact]
    public async Task ListReturnsOneLevelWithFoldersGroupedAheadOfObjects()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var page = await store.ListAsync("/archive", cancellationToken: token);

        Assert.True(page.IsSuccess);
        Assert.Equal(
            [("logs", ObjectEntryKind.Prefix), ("readme.txt", ObjectEntryKind.Object)],
            [.. page.Value.Entries.Select(entry => (entry.Name, entry.Kind))]);
        Assert.Null(page.Value.ContinuationToken);
    }

    [Fact]
    public async Task ListAtTheAccountRootReturnsContainers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var page = await store.ListAsync("/", cancellationToken: token);

        Assert.True(page.IsSuccess);
        var entry = Assert.Single(page.Value.Entries);
        Assert.Equal("archive", entry.Name);
        Assert.Equal(ObjectEntryKind.Container, entry.Kind);
        Assert.True(entry.IsDirectory);
    }

    [Fact]
    public async Task StatAnswersForAContainerAPrefixAnObjectAndSomethingAbsent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var container = await store.StatAsync("/archive", token);
        var prefix = await store.StatAsync("/archive/logs", token);
        var item = await store.StatAsync("/archive/readme.txt", token);
        var absent = await store.StatAsync("/archive/nothing-here", token);

        Assert.Equal(ObjectEntryKind.Container, container.Value!.Kind);
        Assert.Equal(ObjectEntryKind.Prefix, prefix.Value!.Kind);
        Assert.Equal(ObjectEntryKind.Object, item.Value!.Kind);
        Assert.Equal(11, item.Value.Size);
        Assert.Null(absent.Value);
    }

    [Fact]
    public async Task CreatedFolderIsListedOnceAsAFolderAndNotAlsoAsAnEmptyFile()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        Assert.True((await store.CreateFolderAsync("/archive/reports", token)).IsSuccess);

        var page = await store.ListAsync("/archive", cancellationToken: token);
        var reports = page.Value.Entries.Where(entry => entry.Name == "reports").ToArray();
        var single = Assert.Single(reports);
        Assert.Equal(ObjectEntryKind.Prefix, single.Kind);

        // The marker object is really there; it is simply not shown twice.
        Assert.Contains("archive/reports/", store.Keys);
    }

    [Fact]
    public async Task CreateFolderRefusesWhenSomethingIsAlreadyThere()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var again = await store.CreateFolderAsync("/archive/logs", token);

        Assert.True(again.IsFailure);
        Assert.Equal(SftpError.AlreadyExists, again.Failure.Error);
    }

    [Fact]
    public async Task CreateFolderRefusesToCreateAContainer()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var refused = await store.CreateFolderAsync("/new-bucket", token);

        Assert.True(refused.IsFailure);
        Assert.Equal(SftpError.NotSupported, refused.Failure.Error);
    }

    [Fact]
    public async Task NonRecursiveDeleteOfANonEmptyPrefixSaysToDeleteItRecursively()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var refused = await store.DeleteAsync("/archive/logs", recursive: false, token);

        Assert.True(refused.IsFailure);
        // The one place the reused error enum is genuinely lossy — see ADR-0019.
        Assert.Equal(SftpError.NotSupported, refused.Failure.Error);
        Assert.Equal("The folder is not empty. Delete it recursively.", refused.Failure.Message);
    }

    [Fact]
    public async Task RecursiveDeleteRemovesEveryKeyUnderThePrefix()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var deleted = await store.DeleteAsync("/archive/logs", recursive: true, token);

        Assert.True(deleted.IsSuccess);
        Assert.DoesNotContain(store.Keys, key => key.StartsWith("archive/logs/", StringComparison.Ordinal));
        Assert.Contains("archive/readme.txt", store.Keys);
    }

    [Fact]
    public async Task WriteThenReadRoundTripsAnObject()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();
        var payload = Encoding.UTF8.GetBytes("uploaded body");

        var written = await store.WriteAsync(
            "/archive/upload.bin",
            new MemoryStream(payload),
            payload.Length,
            token);

        Assert.True(written.IsSuccess);
        Assert.Equal(payload.Length, written.Value.Size);
        var read = await store.OpenReadAsync("/archive/upload.bin", cancellationToken: token);
        await using var stream = read.Value;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task RangedReadReturnsExactlyTheRequestedBytes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var read = await store.OpenReadAsync(
            "/archive/readme.txt",
            new ObjectReadOptions { Offset = 6, Length = 3 },
            token);

        Assert.True(read.IsSuccess);
        await using var stream = read.Value;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("wor", await reader.ReadToEndAsync(token));
    }

    [Fact]
    public async Task ARangedReadAgainstAStaleETagIsAPreconditionFailure()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();

        var read = await store.OpenReadAsync(
            "/archive/readme.txt",
            new ObjectReadOptions { Offset = 0, Length = 4, IfMatchETag = "etag-from-a-previous-attempt" },
            token);

        Assert.True(read.IsFailure);
        // Not NotFound and not PermissionDenied: "the object changed under you, restart the transfer".
        Assert.Equal(SftpError.PreconditionFailed, read.Failure.Error);
    }

    [Fact]
    public async Task MultipartUploadRoundTripsThroughPartsThatEachGetAFreshStream()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = Seeded();
        var upload = await store.StartUploadAsync("/archive/big.bin", token);
        Assert.True(upload.IsSuccess);

        await using (var session = upload.Value)
        {
            Assert.True(session.MinimumPartSize > 0);
            Assert.True(session.MaximumPartSize >= session.MinimumPartSize);
            Assert.True(session.MaximumPartCount > 1);

            var firstStreams = 0;
            Assert.True((await session.UploadPartAsync(
                1,
                5,
                _ =>
                {
                    firstStreams++;
                    return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("first")));
                },
                token)).IsSuccess);
            Assert.True((await session.UploadPartAsync(
                2,
                6,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("second"))),
                token)).IsSuccess);

            // The factory is what makes a retried part possible: it is invoked once per attempt.
            Assert.Equal(1, firstStreams);
            var completed = await session.CompleteAsync(token);
            Assert.True(completed.IsSuccess);
        }

        var read = await store.OpenReadAsync("/archive/big.bin", cancellationToken: token);
        await using var stream = read.Value;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("firstsecond", await reader.ReadToEndAsync(token));
    }

    [Fact]
    public async Task AbortOnAnAzureShapedUploadWhoseAbortIsANoOpStillReportsSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = new InMemoryObjectStorage(abortIsNoOp: true);
        store.AddContainer("archive");
        var upload = await store.StartUploadAsync("/archive/big.bin", token);

        await using var session = upload.Value;
        var aborted = await session.AbortAsync(token);

        // Azure has no abort call: uncommitted blocks are invisible, unbilled, and expire after seven
        // days. Reporting failure for a no-op would make every cancelled transfer look broken.
        Assert.True(aborted.IsSuccess);
    }

    private static InMemoryObjectStorage Seeded()
    {
        var store = new InMemoryObjectStorage();
        store.Seed("/archive/readme.txt", Encoding.UTF8.GetBytes("hello world"));
        store.Seed("/archive/logs/", []);
        store.Seed("/archive/logs/2026/app.log", Encoding.UTF8.GetBytes("log line"));
        return store;
    }
}
