using System.Security.Cryptography;

namespace RemoteFlow.Infrastructure.Security.Crypto;

public sealed class AesGcmAuthenticatedCipher : IAuthenticatedCipher
{
    public int KeySize => 32;

    public int NonceSize => 12;

    public int TagSize => 16;

    public void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        ValidateBuffers(key, nonce, plaintext.Length, ciphertext.Length, tag.Length);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    public void Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData,
        Span<byte> plaintext)
    {
        ValidateBuffers(key, nonce, ciphertext.Length, plaintext.Length, tag.Length);
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
    }

    private void ValidateBuffers(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        int inputLength,
        int outputLength,
        int tagLength)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"AES-256-GCM requires a {KeySize}-byte key.", nameof(key));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"AES-GCM requires a {NonceSize}-byte nonce.", nameof(nonce));
        }

        if (tagLength != TagSize)
        {
            throw new ArgumentException($"AES-GCM requires a {TagSize}-byte authentication tag.", nameof(tagLength));
        }

        if (inputLength != outputLength)
        {
            throw new ArgumentException("AES-GCM input and output buffers must have equal lengths.", nameof(outputLength));
        }
    }
}
