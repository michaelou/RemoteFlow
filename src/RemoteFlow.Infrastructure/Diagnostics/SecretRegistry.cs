using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Diagnostics;

public sealed class SecretRegistry : ISecretRegistry
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    public void Register(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length < 4)
        {
            throw new ArgumentException("Secret markers must contain at least four characters.", nameof(secret));
        }

        lock (_lock)
        {
            _ = _secrets.Add(secret);
        }
    }

    public IReadOnlyList<string> GetSecrets()
    {
        lock (_lock)
        {
            return [.. _secrets];
        }
    }
}
