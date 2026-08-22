using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Storage;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>Drives the S3 adapter against a substituted client. What is under test is the mapping the
/// adapter does — marker suppression, prefix grouping, the stat fallback, batched recursive delete — not
/// the SDK.</summary>
public sealed class S3ObjectStorageServiceTests
{
    private static readonly ObjectStorageEndpoint _endpoint = new(
        ProtocolType.S3,
        "s3.eu-west-2.amazonaws.com",
        443,
        "AKIAEXAMPLE",
        "eu-west-2",
        null,
        false,
        null,
        null);

    [Fact]
    public async Task AFolderIsListedOnceAsAFolderAndNotAlsoAsAZeroByteFile()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                CommonPrefixes = ["reports/"],
                S3Objects =
                [
                    // The marker the console (and RemoteFlow) writes for the folder itself...
                    new S3Object { Key = "reports/", Size = 0 },
                    // ...and the listed prefix coming back as an object, which it does when a marker
                    // exists for it.
                    new S3Object { Key = string.Empty, Size = 0 },
                    new S3Object { Key = "readme.txt", Size = 11, ETag = "\"abc\"" },
                ],
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var page = await service.ListAsync("/archive", cancellationToken: token);

        Assert.True(page.IsSuccess);
        Assert.Equal(
            [("reports", ObjectEntryKind.Prefix), ("readme.txt", ObjectEntryKind.Object)],
            [.. page.Value.Entries.Select(entry => (entry.Name, entry.Kind))]);
        Assert.Equal("abc", page.Value.Entries[1].ETag);
    }

    [Fact]
    public async Task ListingIsOneLevelWithADelimiterAndThePagingItWasGiven()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response { NextContinuationToken = "next-page" }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var page = await service.ListAsync(
            "/archive/logs",
            new ObjectStoragePaging { PageSize = 25, ContinuationToken = "here" },
            token);

        Assert.Equal("next-page", page.Value.ContinuationToken);
        var request = (ListObjectsV2Request)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.ListObjectsV2Async))
            .GetArguments()[0]!;
        Assert.Equal("archive", request.BucketName);
        Assert.Equal("logs/", request.Prefix);
        Assert.Equal("/", request.Delimiter);
        Assert.Equal(25, request.MaxKeys);
        Assert.Equal("here", request.ContinuationToken);
    }

    [Fact]
    public async Task ListingTheAccountRootWithoutTheRightToListBucketsNamesTheRemedy()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListBucketsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<ListBucketsResponse>>(_ => throw Denied());
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var page = await service.ListAsync("/", cancellationToken: token);

        Assert.True(page.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, page.Failure.Error);
        Assert.Equal(
            "This key cannot list buckets. Set a bucket name on the connection to browse it directly.",
            page.Failure.Message);
    }

    [Fact]
    public async Task APinnedBucketNeverCallsListBuckets()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response()));
        await using var service = new S3ObjectStorageService(client, _endpoint with { Container = "archive" });

        var page = await service.ListAsync("/", cancellationToken: token);

        Assert.True(page.IsSuccess);
        Assert.DoesNotContain(
            client.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAmazonS3.ListBucketsAsync));
    }

    [Fact]
    public async Task NavigatingOutOfAPinnedBucketIsAnInvalidPath()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        await using var service = new S3ObjectStorageService(client, _endpoint with { Container = "archive" });

        var page = await service.ListAsync("/somewhere-else", cancellationToken: token);

        Assert.True(page.IsFailure);
        Assert.Equal(SftpError.InvalidPath, page.Failure.Error);
    }

    [Fact]
    public async Task StatOnAContainerProvesExistenceWithAOneKeyListingRatherThanHeadBucket()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response()));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var stat = await service.StatAsync("/archive", token);

        Assert.Equal(ObjectEntryKind.Container, stat.Value!.Kind);
        var request = (ListObjectsV2Request)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.ListObjectsV2Async))
            .GetArguments()[0]!;
        Assert.Equal(1, request.MaxKeys);
    }

    [Fact]
    public async Task StatOnAKeyUsesHeadObject()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetObjectMetadataResponse
            {
                ContentLength = 42,
                ETag = "\"etag\"",
                LastModified = new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc),
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var stat = await service.StatAsync("/archive/readme.txt", token);

        Assert.Equal(ObjectEntryKind.Object, stat.Value!.Kind);
        Assert.Equal(42, stat.Value.Size);
        Assert.Equal("etag", stat.Value.ETag);
        Assert.Equal(new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero), stat.Value.LastModifiedUtc);
    }

    [Fact]
    public async Task StatFallsBackToAPrefixListingWhenHeadObjectIsAFourOhFour()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectMetadataResponse>>(_ => throw NotFound());
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "logs/2026/app.log", Size = 8 }],
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var stat = await service.StatAsync("/archive/logs", token);

        Assert.Equal(ObjectEntryKind.Prefix, stat.Value!.Kind);
    }

    [Fact]
    public async Task StatReturnsNothingWhenNeitherAKeyNorAPrefixExists()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectMetadataResponse>>(_ => throw NotFound());
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response()));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var stat = await service.StatAsync("/archive/absent", token);

        Assert.True(stat.IsSuccess);
        Assert.Null(stat.Value);
    }

    [Fact]
    public async Task CreateFolderPutsAZeroByteMarkerAfterAOneKeyGuard()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response()));
        _ = client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PutObjectResponse()));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var created = await service.CreateFolderAsync("/archive/reports", token);

        Assert.True(created.IsSuccess);
        var put = (PutObjectRequest)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.PutObjectAsync))
            .GetArguments()[0]!;
        Assert.Equal("reports/", put.Key);
        Assert.Equal(0, put.InputStream!.Length);
    }

    [Fact]
    public async Task CreateFolderRefusesWhenTheGuardFindsSomethingThere()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "reports/", Size = 0 }],
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var created = await service.CreateFolderAsync("/archive/reports", token);

        Assert.True(created.IsFailure);
        Assert.Equal(SftpError.AlreadyExists, created.Failure.Error);
        Assert.DoesNotContain(
            client.ReceivedCalls(),
            call => call.GetMethodInfo().Name == nameof(IAmazonS3.PutObjectAsync));
    }

    [Fact]
    public async Task BucketCreationAndDeletionAreRefusedAndPointAtTheConsole()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var created = await service.CreateFolderAsync("/new-bucket", token);
        var deleted = await service.DeleteAsync("/new-bucket", recursive: true, token);

        Assert.Equal(SftpError.NotSupported, created.Failure.Error);
        Assert.Contains("AWS console", created.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(SftpError.NotSupported, deleted.Failure.Error);
        Assert.Contains("AWS console", deleted.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonRecursiveDeleteOfANonEmptyPrefixSaysToDeleteItRecursively()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectMetadataResponse>>(_ => throw NotFound());
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "logs/", Size = 0 }, new S3Object { Key = "logs/a", Size = 1 }],
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var deleted = await service.DeleteAsync("/archive/logs", recursive: false, token);

        Assert.True(deleted.IsFailure);
        Assert.Equal(SftpError.NotSupported, deleted.Failure.Error);
        Assert.Equal("The folder is not empty. Delete it recursively.", deleted.Failure.Message);
    }

    [Fact]
    public async Task RecursiveDeleteBatchesTheKeysAndRemovesTheMarkerLast()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectMetadataResponse>>(_ => throw NotFound());
        var listed = 0;
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(listed++ switch
            {
                // The stat probe, then the first page, then the last page.
                0 => new ListObjectsV2Response { S3Objects = [new S3Object { Key = "logs/a", Size = 1 }] },
                1 => new ListObjectsV2Response
                {
                    S3Objects = [new S3Object { Key = "logs/", Size = 0 }, new S3Object { Key = "logs/a", Size = 1 }],
                    NextContinuationToken = "page-two",
                },
                _ => new ListObjectsV2Response { S3Objects = [new S3Object { Key = "logs/b", Size = 1 }] },
            }));
        _ = client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteObjectsResponse()));
        _ = client.DeleteObjectAsync("archive", "logs/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteObjectResponse()));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var deleted = await service.DeleteAsync("/archive/logs", recursive: true, token);

        Assert.True(deleted.IsSuccess);
        var batches = client.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAmazonS3.DeleteObjectsAsync))
            .Select(call => (DeleteObjectsRequest)call.GetArguments()[0]!)
            .ToArray();
        Assert.Equal(2, batches.Length);
        // The marker is never in a batch: it goes last, on its own, so an interrupted delete leaves a
        // visible empty folder rather than an invisible half-deleted one.
        Assert.DoesNotContain(
            batches.SelectMany(batch => batch.Objects!),
            item => item.Key == "logs/");
        Assert.Equal(["logs/a"], [.. batches[0].Objects!.Select(item => item.Key)]);
        Assert.Equal(["logs/b"], [.. batches[1].Objects!.Select(item => item.Key)]);
        _ = client.Received(1).DeleteObjectAsync("archive", "logs/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecursiveDeleteSurfacesAPerKeyFailureRatherThanReportingSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectMetadataResponse>>(_ => throw NotFound());
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "logs/a", Size = 1 }],
            }));
        _ = client.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeleteObjectsResponse
            {
                DeleteErrors = [new DeleteError { Key = "logs/a", Message = "Access Denied" }],
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var deleted = await service.DeleteAsync("/archive/logs", recursive: true, token);

        Assert.True(deleted.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, deleted.Failure.Error);
        Assert.Contains("logs/a", deleted.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARangedReadAsksForExactlyThoseBytesUnderAnIfMatchEtag()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetObjectResponse
            {
                ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("wor")),
            }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var read = await service.OpenReadAsync(
            "/archive/readme.txt",
            new ObjectReadOptions { Offset = 6, Length = 3, IfMatchETag = "abc" },
            token);

        Assert.True(read.IsSuccess);
        await using (var stream = read.Value)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("wor", await reader.ReadToEndAsync(token));
        }

        var request = (GetObjectRequest)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.GetObjectAsync))
            .GetArguments()[0]!;
        Assert.Equal(6, request.ByteRange!.Start);
        Assert.Equal(8, request.ByteRange.End);
        Assert.Equal("abc", request.EtagToMatch);
    }

    [Fact]
    public async Task AnOpenEndedRangeAsksForEverythingFromTheOffset()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream() }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var read = await service.OpenReadAsync(
            "/archive/readme.txt",
            new ObjectReadOptions { Offset = 100 },
            token);
        await read.Value.DisposeAsync();

        var request = (GetObjectRequest)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.GetObjectAsync))
            .GetArguments()[0]!;
        Assert.Equal("bytes=100-", request.ByteRange!.FormattedByteRange);
    }

    [Fact]
    public async Task APreconditionFailureOnARangedReadComesBackAsPreconditionFailed()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<GetObjectResponse>>(_ => throw new AmazonS3Exception(
                "At least one of the pre-conditions you specified did not hold",
                ErrorType.Sender,
                "PreconditionFailed",
                "request-id",
                HttpStatusCode.PreconditionFailed));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var read = await service.OpenReadAsync(
            "/archive/readme.txt",
            new ObjectReadOptions { Offset = 0, Length = 4, IfMatchETag = "stale" },
            token);

        Assert.True(read.IsFailure);
        Assert.Equal(SftpError.PreconditionFailed, read.Failure.Error);
    }

    [Fact]
    public async Task WriteSendsTheContentLengthItWasGiven()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PutObjectResponse { ETag = "\"written\"" }));
        await using var service = new S3ObjectStorageService(client, _endpoint);
        var payload = Encoding.UTF8.GetBytes("body");

        var written = await service.WriteAsync("/archive/x.bin", new MemoryStream(payload), payload.Length, token);

        Assert.True(written.IsSuccess);
        Assert.Equal("written", written.Value.ETag);
        var put = (PutObjectRequest)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.PutObjectAsync))
            .GetArguments()[0]!;
        Assert.Equal(payload.Length, put.Headers.ContentLength);
        Assert.False(put.AutoCloseStream);
    }

    [Fact]
    public async Task TheRootPrefixIsAppliedGoingInAndHiddenComingBack()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "logs/2026/app.log", Size = 8 }],
            }));
        await using var service = new S3ObjectStorageService(
            client,
            _endpoint with { Container = "archive", RootPrefix = "logs/2026" });

        var page = await service.ListAsync("/archive", cancellationToken: token);

        var request = (ListObjectsV2Request)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.ListObjectsV2Async))
            .GetArguments()[0]!;
        Assert.Equal("logs/2026/", request.Prefix);
        Assert.Equal("/archive/app.log", Assert.Single(page.Value.Entries).Path);
    }

    [Fact]
    public async Task AMultipartUploadStagesPartsFromAFactoryAndCompletesInPartOrder()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.InitiateMultipartUploadAsync(
                Arg.Any<InitiateMultipartUploadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload-1" }));
        var parts = 0;
        _ = client.UploadPartAsync(Arg.Any<UploadPartRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new UploadPartResponse { ETag = $"\"part-{++parts}\"" }));
        _ = client.CompleteMultipartUploadAsync(
                Arg.Any<CompleteMultipartUploadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CompleteMultipartUploadResponse { ETag = "\"joined\"" }));
        await using var service = new S3ObjectStorageService(client, _endpoint);

        var upload = await service.StartUploadAsync("/archive/big.bin", token);
        Assert.True(upload.IsSuccess);
        Assert.Equal(5L * 1024 * 1024, upload.Value.MinimumPartSize);
        Assert.Equal(10_000, upload.Value.MaximumPartCount);

        // Out of order on purpose: CompleteMultipartUpload requires ascending part numbers.
        Assert.True((await upload.Value.UploadPartAsync(2, 4, Body, token)).IsSuccess);
        Assert.True((await upload.Value.UploadPartAsync(1, 4, Body, token)).IsSuccess);
        var completed = await upload.Value.CompleteAsync(token);

        Assert.True(completed.IsSuccess);
        Assert.Equal("joined", completed.Value.ETag);
        var complete = (CompleteMultipartUploadRequest)client.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAmazonS3.CompleteMultipartUploadAsync))
            .GetArguments()[0]!;
        Assert.Equal([1, 2], [.. complete.PartETags!.Select(part => part.PartNumber)]);
        return;

        static ValueTask<Stream> Body(CancellationToken _)
        {
            return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("data")));
        }
    }

    [Fact]
    public async Task AbortingAnS3UploadCallsAbortMultipartUploadOnceAndNotAgainOnDispose()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Substitute.For<IAmazonS3>();
        _ = client.InitiateMultipartUploadAsync(
                Arg.Any<InitiateMultipartUploadRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload-1" }));
        _ = client.AbortMultipartUploadAsync("archive", "big.bin", "upload-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AbortMultipartUploadResponse()));
        await using var service = new S3ObjectStorageService(client, _endpoint);
        var upload = await service.StartUploadAsync("/archive/big.bin", token);

        var aborted = await upload.Value.AbortAsync(token);
        await upload.Value.DisposeAsync();

        Assert.True(aborted.IsSuccess);
        // Uploaded parts keep billing until the upload is aborted, so this is not decoration; aborting
        // twice, however, would fail with NoSuchUpload.
        _ = client.Received(1)
            .AbortMultipartUploadAsync("archive", "big.bin", "upload-1", Arg.Any<CancellationToken>());
    }

    private static AmazonS3Exception NotFound()
    {
        return new AmazonS3Exception(
            "The specified key does not exist.",
            ErrorType.Sender,
            "NoSuchKey",
            "request-id",
            HttpStatusCode.NotFound);
    }

    private static AmazonS3Exception Denied()
    {
        return new AmazonS3Exception(
            "Access Denied",
            ErrorType.Sender,
            "AccessDenied",
            "request-id",
            HttpStatusCode.Forbidden);
    }
}
