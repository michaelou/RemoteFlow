using Azure.Storage.Blobs.Specialized;
using RemoteFlow.Infrastructure.Storage;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class AzureBlockBlobUploadTests
{
    [Fact]
    public async Task TheLimitsAreTheSdksOwnAndAzureHasNoMinimumBlockSize()
    {
        var blob = Blob();
        await using var upload = new AzureBlockBlobUpload(blob, "/archive/big.bin");

        Assert.Equal(1, upload.MinimumPartSize);
        Assert.Equal(blob.BlockBlobMaxStageBlockLongBytes, upload.MaximumPartSize);
        Assert.Equal(blob.BlockBlobMaxBlocks, upload.MaximumPartCount);
        // Azure's limits are much wider than S3's, which is exactly why a caller should read them off the
        // upload rather than assume S3's five-mebibyte floor.
        Assert.True(upload.MaximumPartCount > 10_000);
    }

    [Fact]
    public async Task AbortIsANoOpThatStillReportsSuccessAndTouchesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var upload = new AzureBlockBlobUpload(Blob(), "/archive/big.bin");

        var aborted = await upload.AbortAsync(token);
        var again = await upload.AbortAsync(token);

        // Azure has no abort call: an uncommitted block list is invisible, unbilled, and expires after
        // seven days. Reporting failure here would make every cancelled transfer look broken, and calling
        // the service would need a network round trip that achieves nothing.
        Assert.True(aborted.IsSuccess);
        Assert.True(again.IsSuccess);
    }

    [Fact]
    public async Task AnOutOfRangePartNumberIsRejectedBeforeAnythingIsStaged()
    {
        var token = TestContext.Current.CancellationToken;
        await using var upload = new AzureBlockBlobUpload(Blob(), "/archive/big.bin");

        var refused = await upload.UploadPartAsync(
            upload.MaximumPartCount + 1,
            1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream()),
            token);

        Assert.True(refused.IsFailure);
        Assert.Equal(Application.Abstractions.Sftp.SftpError.QuotaExceeded, refused.Failure.Error);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => upload.UploadPartAsync(
            0,
            1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream()),
            token));
    }

    /// <summary>Constructed from a URL, with no credential and no request: nothing here reaches the
    /// network.</summary>
    private static BlockBlobClient Blob()
    {
        return new BlockBlobClient(new Uri("https://contoso.blob.core.windows.net/archive/big.bin"));
    }
}
