using System.Security.Cryptography;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class SecureRandom : ISecureRandom
{
    public byte[] GetBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RandomNumberGenerator.GetBytes(count);
    }
}
