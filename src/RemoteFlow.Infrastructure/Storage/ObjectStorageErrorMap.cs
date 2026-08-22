using Amazon.S3;
using Azure;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Infrastructure.Storage;

/// <summary>Turns provider exceptions into the shared failure type. Status code first, because both
/// services document HTTP semantics they honour for every operation; the error code narrows the cases
/// where one status covers two different things. An error nobody has mapped keeps the provider's own
/// message rather than being flattened into a generic one — that message is often the only clue.</summary>
public static class ObjectStorageErrorMap
{
    public static SftpFailure FromException(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => Cancelled(),
            AmazonS3Exception s3 => FromS3(s3),
            RequestFailedException azure => FromAzure(azure),
            _ when cancellationToken.IsCancellationRequested => Cancelled(),
            HttpRequestException http => new SftpFailure(
                SftpError.ConnectionLost,
                Describe(http.Message, "The storage service could not be reached.")),
            IOException io => new SftpFailure(
                SftpError.ConnectionLost,
                Describe(io.Message, "The connection to the storage service was lost.")),
            _ => new SftpFailure(SftpError.Unknown, Describe(exception.Message, "The storage operation failed.")),
        };
    }

    public static SftpFailure FromS3(AmazonS3Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = Describe(exception.Message, "The S3 request failed.");
        var byCode = exception.ErrorCode switch
        {
            "NoSuchBucket" or "NoSuchKey" or "NoSuchUpload" or "NotFound" => SftpError.NotFound,
            "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "ExpiredToken" or
                "InvalidToken" or "AccountProblem" => SftpError.PermissionDenied,
            "BucketAlreadyExists" or "BucketAlreadyOwnedByYou" => SftpError.AlreadyExists,
            "PreconditionFailed" => SftpError.PreconditionFailed,
            "EntityTooLarge" or "EntityTooSmall" or "QuotaExceeded" => SftpError.QuotaExceeded,
            "InvalidBucketName" or "KeyTooLongError" or "InvalidArgument" => SftpError.InvalidPath,
            "InvalidRange" => SftpError.InvalidPath,
            "NotImplemented" or "MethodNotAllowed" => SftpError.NotSupported,
            "SlowDown" or "RequestTimeout" or "InternalError" or "ServiceUnavailable" => SftpError.ConnectionLost,
            _ => (SftpError?)null,
        };

        return new SftpFailure(byCode ?? FromStatus(exception.StatusCode), message);
    }

    public static SftpFailure FromAzure(RequestFailedException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = Describe(exception.Message, "The Azure Blob request failed.");
        var byCode = exception.ErrorCode switch
        {
            "BlobNotFound" or "ContainerNotFound" or "ResourceNotFound" or "AccountNotFound" => SftpError.NotFound,
            "AuthenticationFailed" or "AuthorizationFailure" or "AuthorizationPermissionMismatch" or
                "InsufficientAccountPermissions" or "AccountIsDisabled" => SftpError.PermissionDenied,
            "BlobAlreadyExists" or "ContainerAlreadyExists" => SftpError.AlreadyExists,
            "ConditionNotMet" => SftpError.PreconditionFailed,
            "InvalidResourceName" or "InvalidUri" or "OutOfRangeInput" or "InvalidInput" or
                "InvalidRange" => SftpError.InvalidPath,
            "AccountIsOverQuota" or "RequestBodyTooLarge" => SftpError.QuotaExceeded,
            "UnsupportedHttpVerb" or "FeatureVersionMismatch" or "BlobTypeNotSupported" => SftpError.NotSupported,
            "ServerBusy" or "InternalError" or "OperationTimedOut" => SftpError.ConnectionLost,
            _ => (SftpError?)null,
        };

        return new SftpFailure(byCode ?? FromStatus(exception.Status), message);
    }

    public static SftpFailure Cancelled()
    {
        return new SftpFailure(SftpError.Cancelled, "The storage operation was cancelled.");
    }

    private static SftpError FromStatus(System.Net.HttpStatusCode status)
    {
        return FromStatus((int)status);
    }

    private static SftpError FromStatus(int status)
    {
        return status switch
        {
            400 => SftpError.InvalidPath,
            401 or 403 => SftpError.PermissionDenied,
            404 or 410 => SftpError.NotFound,
            409 => SftpError.AlreadyExists,
            412 => SftpError.PreconditionFailed,
            413 or 507 => SftpError.QuotaExceeded,
            416 => SftpError.InvalidPath,
            429 => SftpError.ConnectionLost,
            501 or 405 => SftpError.NotSupported,
            >= 500 => SftpError.ConnectionLost,
            _ => SftpError.Unknown,
        };
    }

    private static string Describe(string? message, string fallback)
    {
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }
}
