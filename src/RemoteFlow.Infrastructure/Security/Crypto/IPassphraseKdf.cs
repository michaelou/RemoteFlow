namespace RemoteFlow.Infrastructure.Security.Crypto;

public sealed record PassphraseKdfParameters(int MemorySizeKiB, int Iterations, int Parallelism)
{
    public static PassphraseKdfParameters VaultDefault { get; } = new(64 * 1024, 3, 1);

    public void Validate()
    {
        if (MemorySizeKiB is < 8 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MemorySizeKiB), "Memory size must be between 8 KiB and 1 GiB.");
        }

        if (Iterations is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations), "Iterations must be between 1 and 100.");
        }

        if (Parallelism is < 1 or > 64 || MemorySizeKiB < 8 * Parallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), "Parallelism is invalid for the selected memory size.");
        }
    }
}

public interface IPassphraseKdf
{
    byte[] DeriveKey(
        ReadOnlySpan<char> passphrase,
        ReadOnlySpan<byte> salt,
        PassphraseKdfParameters parameters,
        int outputLength);
}
