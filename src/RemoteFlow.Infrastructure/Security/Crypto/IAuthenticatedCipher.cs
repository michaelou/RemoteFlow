namespace RemoteFlow.Infrastructure.Security.Crypto;

public interface IAuthenticatedCipher
{
    int KeySize { get; }

    int NonceSize { get; }

    int TagSize { get; }

    void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        Span<byte> ciphertext,
        Span<byte> tag);

    void Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData,
        Span<byte> plaintext);
}
