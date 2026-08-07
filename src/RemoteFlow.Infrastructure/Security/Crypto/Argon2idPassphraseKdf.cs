using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteFlow.Infrastructure.Security.Crypto;

public sealed class Argon2idPassphraseKdf : IPassphraseKdf
{
    public byte[] DeriveKey(
        ReadOnlySpan<char> passphrase,
        ReadOnlySpan<byte> salt,
        PassphraseKdfParameters parameters,
        int outputLength)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLength);
        if (salt.Length < 8)
        {
            throw new ArgumentException("Argon2id salts must contain at least 8 bytes.", nameof(salt));
        }

        var passwordBytes = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        try
        {
            _ = Encoding.UTF8.GetBytes(passphrase, passwordBytes);
            return DeriveKeyCore(passwordBytes, salt, parameters, outputLength, [], []);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static byte[] DeriveKeyKnownAnswer(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> knownSecret,
        ReadOnlySpan<byte> associatedData,
        PassphraseKdfParameters parameters,
        int outputLength)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        return DeriveKeyCore(password, salt, parameters, outputLength, knownSecret, associatedData);
    }

    private static byte[] DeriveKeyCore(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        PassphraseKdfParameters parameters,
        int outputLength,
        ReadOnlySpan<byte> knownSecret,
        ReadOnlySpan<byte> associatedData)
    {
        var passwordCopy = password.ToArray();
        var saltCopy = salt.ToArray();
        var knownSecretCopy = knownSecret.ToArray();
        var associatedDataCopy = associatedData.ToArray();
        try
        {
            using var argon2 = new Argon2id(passwordCopy)
            {
                Salt = saltCopy,
                KnownSecret = knownSecretCopy,
                AssociatedData = associatedDataCopy,
                MemorySize = parameters.MemorySizeKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon2.GetBytes(outputLength);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordCopy);
            CryptographicOperations.ZeroMemory(saltCopy);
            CryptographicOperations.ZeroMemory(knownSecretCopy);
            CryptographicOperations.ZeroMemory(associatedDataCopy);
        }
    }
}
