using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Domain.Common;

public sealed record RemoteFlowError(RemoteFlowErrorKind Kind, string Code, string Message)
{
    public static RemoteFlowError Validation(string code, string message)
    {
        return new(RemoteFlowErrorKind.Validation, code, message);
    }

    public static RemoteFlowError NotFound(string code, string message)
    {
        return new(RemoteFlowErrorKind.NotFound, code, message);
    }

    public static RemoteFlowError Unavailable(string code, string message)
    {
        return new(RemoteFlowErrorKind.Unavailable, code, message);
    }
}

public sealed class Result<T>
{
    internal Result(T value)
    {
        SuccessfulValue = value;
        FailureError = null;
        IsSuccess = true;
    }

    internal Result(RemoteFlowError error)
    {
        SuccessfulValue = default;
        FailureError = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? SuccessfulValue!
        : throw new InvalidOperationException("A failed result has no value.");

    public RemoteFlowError Error => !IsSuccess
        ? FailureError!
        : throw new InvalidOperationException("A successful result has no error.");

    private T? SuccessfulValue { get; }

    private RemoteFlowError? FailureError { get; }

#pragma warning disable CA1000 // Result<T>.Success/Failure is the conventional, discoverable factory API.
    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public static Result<T> Failure(RemoteFlowError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }
#pragma warning restore CA1000
}
