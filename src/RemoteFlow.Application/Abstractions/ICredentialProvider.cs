using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RemoteFlow.Application.Abstractions;

public interface ICredentialProvider
{
    string Name { get; }

    bool IsAvailable { get; }

    Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default);

    Task SetAsync(
        string storeKey,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default);
}

public sealed class SecretHandle : IDisposable
{
    private char[]? _buffer;

    public SecretHandle(ReadOnlySpan<char> secret)
    {
        _buffer = secret.ToArray();
    }

    public ReadOnlyMemory<char> Secret => _buffer ?? ReadOnlyMemory<char>.Empty;

    public bool IsDisposed => _buffer is null;

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }
}
