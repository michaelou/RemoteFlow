namespace RemoteFlow.Infrastructure.Security;

public class VaultException : CredentialProviderException
{
    public VaultException(string message)
        : base(message)
    {
    }

    public VaultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class VaultLockedException : VaultException
{
    public VaultLockedException()
        : base("The credential vault is locked.")
    {
    }
}

public sealed class VaultUnlockException : VaultException
{
    public VaultUnlockException()
        : base("The credential vault could not be unlocked.")
    {
    }
}
