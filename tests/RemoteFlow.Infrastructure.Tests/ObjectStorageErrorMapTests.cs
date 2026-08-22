using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Azure;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Infrastructure.Storage;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class ObjectStorageErrorMapTests
{
    [Theory]
    [InlineData("NoSuchBucket", HttpStatusCode.NotFound, SftpError.NotFound)]
    [InlineData("NoSuchKey", HttpStatusCode.NotFound, SftpError.NotFound)]
    [InlineData("NoSuchUpload", HttpStatusCode.NotFound, SftpError.NotFound)]
    [InlineData("AccessDenied", HttpStatusCode.Forbidden, SftpError.PermissionDenied)]
    [InlineData("InvalidAccessKeyId", HttpStatusCode.Forbidden, SftpError.PermissionDenied)]
    [InlineData("SignatureDoesNotMatch", HttpStatusCode.Forbidden, SftpError.PermissionDenied)]
    [InlineData("ExpiredToken", HttpStatusCode.BadRequest, SftpError.PermissionDenied)]
    [InlineData("BucketAlreadyExists", HttpStatusCode.Conflict, SftpError.AlreadyExists)]
    [InlineData("BucketAlreadyOwnedByYou", HttpStatusCode.Conflict, SftpError.AlreadyExists)]
    // 412 on a ranged GET means "the object changed under you, restart the transfer".
    [InlineData("PreconditionFailed", HttpStatusCode.PreconditionFailed, SftpError.PreconditionFailed)]
    [InlineData("EntityTooLarge", HttpStatusCode.BadRequest, SftpError.QuotaExceeded)]
    [InlineData("QuotaExceeded", HttpStatusCode.BadRequest, SftpError.QuotaExceeded)]
    [InlineData("InvalidBucketName", HttpStatusCode.BadRequest, SftpError.InvalidPath)]
    [InlineData("KeyTooLongError", HttpStatusCode.BadRequest, SftpError.InvalidPath)]
    [InlineData("InvalidRange", HttpStatusCode.RequestedRangeNotSatisfiable, SftpError.InvalidPath)]
    [InlineData("NotImplemented", HttpStatusCode.NotImplemented, SftpError.NotSupported)]
    [InlineData("MethodNotAllowed", HttpStatusCode.MethodNotAllowed, SftpError.NotSupported)]
    [InlineData("SlowDown", HttpStatusCode.ServiceUnavailable, SftpError.ConnectionLost)]
    [InlineData("RequestTimeout", HttpStatusCode.BadRequest, SftpError.ConnectionLost)]
    [InlineData("InternalError", HttpStatusCode.InternalServerError, SftpError.ConnectionLost)]
    [InlineData("ServiceUnavailable", HttpStatusCode.ServiceUnavailable, SftpError.ConnectionLost)]
    public void EveryDocumentedS3ErrorCodeMapsToItsFailure(
        string errorCode,
        HttpStatusCode status,
        SftpError expected)
    {
        var exception = Build("boom", errorCode, status);

        Assert.Equal(expected, ObjectStorageErrorMap.FromS3(exception).Error);
    }

    [Theory]
    // No error code at all: the status code alone has to answer.
    [InlineData(HttpStatusCode.BadRequest, SftpError.InvalidPath)]
    [InlineData(HttpStatusCode.Unauthorized, SftpError.PermissionDenied)]
    [InlineData(HttpStatusCode.Forbidden, SftpError.PermissionDenied)]
    [InlineData(HttpStatusCode.NotFound, SftpError.NotFound)]
    [InlineData(HttpStatusCode.Gone, SftpError.NotFound)]
    [InlineData(HttpStatusCode.Conflict, SftpError.AlreadyExists)]
    [InlineData(HttpStatusCode.PreconditionFailed, SftpError.PreconditionFailed)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SftpError.QuotaExceeded)]
    [InlineData(HttpStatusCode.InsufficientStorage, SftpError.QuotaExceeded)]
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable, SftpError.InvalidPath)]
    [InlineData(HttpStatusCode.TooManyRequests, SftpError.ConnectionLost)]
    [InlineData(HttpStatusCode.NotImplemented, SftpError.NotSupported)]
    [InlineData(HttpStatusCode.BadGateway, SftpError.ConnectionLost)]
    [InlineData(HttpStatusCode.GatewayTimeout, SftpError.ConnectionLost)]
    [InlineData(HttpStatusCode.MovedPermanently, SftpError.Unknown)]
    public void AnS3StatusCodeAloneStillMaps(HttpStatusCode status, SftpError expected)
    {
        var exception = Build("boom", errorCode: null, status);

        Assert.Equal(expected, ObjectStorageErrorMap.FromS3(exception).Error);
    }

    [Theory]
    [InlineData("BlobNotFound", 404, SftpError.NotFound)]
    [InlineData("ContainerNotFound", 404, SftpError.NotFound)]
    [InlineData("ResourceNotFound", 404, SftpError.NotFound)]
    [InlineData("AccountNotFound", 404, SftpError.NotFound)]
    [InlineData("AuthenticationFailed", 403, SftpError.PermissionDenied)]
    [InlineData("AuthorizationFailure", 403, SftpError.PermissionDenied)]
    [InlineData("AuthorizationPermissionMismatch", 403, SftpError.PermissionDenied)]
    [InlineData("InsufficientAccountPermissions", 403, SftpError.PermissionDenied)]
    [InlineData("AccountIsDisabled", 403, SftpError.PermissionDenied)]
    [InlineData("BlobAlreadyExists", 409, SftpError.AlreadyExists)]
    [InlineData("ContainerAlreadyExists", 409, SftpError.AlreadyExists)]
    [InlineData("ConditionNotMet", 412, SftpError.PreconditionFailed)]
    [InlineData("InvalidResourceName", 400, SftpError.InvalidPath)]
    [InlineData("InvalidUri", 400, SftpError.InvalidPath)]
    [InlineData("OutOfRangeInput", 400, SftpError.InvalidPath)]
    [InlineData("InvalidInput", 400, SftpError.InvalidPath)]
    [InlineData("InvalidRange", 416, SftpError.InvalidPath)]
    [InlineData("AccountIsOverQuota", 403, SftpError.QuotaExceeded)]
    [InlineData("RequestBodyTooLarge", 413, SftpError.QuotaExceeded)]
    [InlineData("UnsupportedHttpVerb", 405, SftpError.NotSupported)]
    [InlineData("FeatureVersionMismatch", 409, SftpError.NotSupported)]
    [InlineData("BlobTypeNotSupported", 409, SftpError.NotSupported)]
    [InlineData("ServerBusy", 503, SftpError.ConnectionLost)]
    [InlineData("InternalError", 500, SftpError.ConnectionLost)]
    [InlineData("OperationTimedOut", 500, SftpError.ConnectionLost)]
    public void EveryDocumentedAzureErrorCodeMapsToItsFailure(string errorCode, int status, SftpError expected)
    {
        var exception = new RequestFailedException(status, "boom", errorCode, innerException: null);

        Assert.Equal(expected, ObjectStorageErrorMap.FromAzure(exception).Error);
    }

    [Theory]
    [InlineData(404, SftpError.NotFound)]
    [InlineData(412, SftpError.PreconditionFailed)]
    [InlineData(503, SftpError.ConnectionLost)]
    public void AnAzureStatusCodeAloneStillMaps(int status, SftpError expected)
    {
        var exception = new RequestFailedException(status, "boom");

        Assert.Equal(expected, ObjectStorageErrorMap.FromAzure(exception).Error);
    }

    [Fact]
    public void AnUnknownS3ErrorCodePreservesTheProvidersOwnMessage()
    {
        var exception = Build("The bucket policy denies this action.", "SomeFutureCode", HttpStatusCode.Ambiguous);

        var failure = ObjectStorageErrorMap.FromS3(exception);

        Assert.Equal(SftpError.Unknown, failure.Error);
        Assert.Equal("The bucket policy denies this action.", failure.Message);
    }

    [Fact]
    public void AnUnknownAzureErrorCodePreservesTheProvidersOwnMessage()
    {
        var exception = new RequestFailedException(300, "Something the SDK explained better than we could.");

        var failure = ObjectStorageErrorMap.FromAzure(exception);

        Assert.Equal(SftpError.Unknown, failure.Error);
        Assert.Contains(
            "Something the SDK explained better than we could.",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(TaskCanceledException))]
    public void CancellationMapsToCancelled(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(
            SftpError.Cancelled,
            ObjectStorageErrorMap.FromException(exception, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void ACancelledTokenMapsToCancelledEvenWhenTheExceptionSaysNothing()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var failure = ObjectStorageErrorMap.FromException(new InvalidOperationException("boom"), source.Token);

        Assert.Equal(SftpError.Cancelled, failure.Error);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), SftpError.ConnectionLost)]
    [InlineData(typeof(IOException), SftpError.ConnectionLost)]
    [InlineData(typeof(InvalidOperationException), SftpError.Unknown)]
    public void TransportFailuresAndAnythingElseStillGetAFailure(Type exceptionType, SftpError expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(
            expected,
            ObjectStorageErrorMap.FromException(exception, TestContext.Current.CancellationToken).Error);
    }

    private static AmazonS3Exception Build(string message, string? errorCode, HttpStatusCode status)
    {
        return new AmazonS3Exception(message, ErrorType.Sender, errorCode, "request-id", status);
    }
}
