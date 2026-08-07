namespace RemoteFlow.Infrastructure.Security;

public class CredentialProviderException : Exception
{
    public CredentialProviderException(string message)
        : base(message)
    {
    }

    public CredentialProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CredentialTooLargeException(int actualBytes, int maximumBytes)
    : CredentialProviderException(
        $"The credential is {actualBytes} bytes, exceeding the provider limit of {maximumBytes} bytes.")
{
    public int ActualBytes { get; } = actualBytes;

    public int MaximumBytes { get; } = maximumBytes;
}

public sealed class CredentialAccessDeclinedException(string message) : CredentialProviderException(message)
{
}
