using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Platform;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Security.Crypto;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class CryptoAndVaultTests
{
    private static readonly PassphraseKdfParameters _fastParameters = new(32, 1, 1);

    [Fact]
    public void VaultDefaultsMatchRequiredArgon2idCost()
    {
        Assert.Equal(64 * 1024, PassphraseKdfParameters.VaultDefault.MemorySizeKiB);
        Assert.Equal(3, PassphraseKdfParameters.VaultDefault.Iterations);
        Assert.Equal(1, PassphraseKdfParameters.VaultDefault.Parallelism);
    }

    [Fact]
    public void Argon2idMatchesRfc9106KnownAnswer()
    {
        var actual = Argon2idPassphraseKdf.DeriveKeyKnownAnswer(
            Enumerable.Repeat((byte)0x01, 32).ToArray(),
            Enumerable.Repeat((byte)0x02, 16).ToArray(),
            Enumerable.Repeat((byte)0x03, 8).ToArray(),
            Enumerable.Repeat((byte)0x04, 12).ToArray(),
            new PassphraseKdfParameters(32, 3, 4),
            32);

        Assert.Equal(
            Convert.FromHexString("0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659"),
            actual);
    }

    [Fact]
    public void Aes256GcmMatchesKnownAnswerAndDecrypts()
    {
        var cipher = new AesGcmAuthenticatedCipher();
        var key = new byte[32];
        var nonce = new byte[12];
        var plaintext = new byte[16];
        var ciphertext = new byte[16];
        var tag = new byte[16];

        cipher.Encrypt(key, nonce, plaintext, [], ciphertext, tag);

        Assert.Equal(Convert.FromHexString("CEA7403D4D606B6E074EC5D3BAF39D18"), ciphertext);
        Assert.Equal(Convert.FromHexString("D0D1C8A799996BF0265B98B5D48AB919"), tag);
        var decrypted = new byte[plaintext.Length];
        cipher.Decrypt(key, nonce, ciphertext, tag, [], decrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task VaultPersistsRoundTripsUpdatesAndDeletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var key = "remoteflow/connection/11111111-1111-1111-1111-111111111111/password";
        using (var first = CreateVault(paths))
        {
            await first.UnlockAsync("vault passphrase".AsMemory(), cancellationToken);
            Assert.Null(await first.GetAsync(key, cancellationToken));
            await first.SetAsync(key, "first secret".AsMemory(), "Test", cancellationToken);
            await first.SetAsync(key, "updated secret".AsMemory(), "Test", cancellationToken);
        }

        using var reopened = CreateVault(paths, new PassphraseKdfParameters(64, 2, 1));
        await reopened.UnlockAsync("vault passphrase".AsMemory(), cancellationToken);
        using (var retrieved = await reopened.GetAsync(key, cancellationToken))
        {
            Assert.NotNull(retrieved);
            Assert.Equal("updated secret", retrieved.Secret.ToString());
        }

        await reopened.DeleteAsync(key, cancellationToken);
        Assert.Null(await reopened.GetAsync(key, cancellationToken));
    }

    [Fact]
    public async Task WrongPassphraseHasSingleNonRevealingFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        using (var vault = CreateVault(paths))
        {
            await vault.UnlockAsync("correct passphrase".AsMemory(), cancellationToken);
            await vault.SetAsync("record-one", "alpha".AsMemory(), "Test", cancellationToken);
            await vault.SetAsync("record-two", "beta".AsMemory(), "Test", cancellationToken);
        }

        using var reopened = CreateVault(paths);
        var exception = await Assert.ThrowsAsync<VaultUnlockException>(() =>
            reopened.UnlockAsync("wrong passphrase".AsMemory(), cancellationToken));

        Assert.Equal("The credential vault could not be unlocked.", exception.Message);
        Assert.DoesNotContain("record", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alpha", exception.Message, StringComparison.Ordinal);
        Assert.False(reopened.IsUnlocked);
    }

    [Fact]
    public async Task EverySingleByteMutationIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        string vaultPath;
        using (var vault = CreateVault(paths))
        {
            await vault.UnlockAsync("tamper passphrase".AsMemory(), cancellationToken);
            await vault.SetAsync("record", "tamper-resistant secret".AsMemory(), "Test", cancellationToken);
            vaultPath = vault.VaultPath;
        }

        var original = await File.ReadAllBytesAsync(vaultPath, cancellationToken);
        try
        {
            for (var index = 0; index < original.Length; index++)
            {
                var mutated = original.ToArray();
                mutated[index] ^= 0x01;
                await File.WriteAllBytesAsync(vaultPath, mutated, cancellationToken);
                using var reopened = CreateVault(paths);
                _ = await Assert.ThrowsAsync<VaultUnlockException>(() =>
                    reopened.UnlockAsync("tamper passphrase".AsMemory(), cancellationToken));
            }
        }
        finally
        {
            await File.WriteAllBytesAsync(vaultPath, original, cancellationToken);
        }
    }

    [Fact]
    public async Task HeaderKdfParametersOverrideNewVaultDefaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        using (var original = CreateVault(paths, new PassphraseKdfParameters(32, 2, 1)))
        {
            await original.UnlockAsync("parameters".AsMemory(), cancellationToken);
            await original.SetAsync("key", "value".AsMemory(), "Test", cancellationToken);
        }

        using var reopened = CreateVault(paths, new PassphraseKdfParameters(128, 4, 2));
        await reopened.UnlockAsync("parameters".AsMemory(), cancellationToken);
        using var result = await reopened.GetAsync("key", cancellationToken);
        Assert.NotNull(result);
        Assert.Equal("value", result.Secret.ToString());
    }

    [Fact]
    public async Task DerivedKeyBufferIsZeroedOnDispose()
    {
        using var directory = TemporaryDirectory.Create();
        var vault = CreateVault(TestAppPaths.Under(directory.Path));
        await vault.UnlockAsync("zero-on-dispose".AsMemory(), TestContext.Current.CancellationToken);
        var observedKeyBuffer = vault.KeyMemoryForTesting;
        Assert.Contains(observedKeyBuffer.ToArray(), value => value != 0);

        vault.Dispose();

        Assert.All(observedKeyBuffer.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task VaultFileModeIsExactlyOwnerReadWrite()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are unavailable on Windows.");
            return;
        }

        using var directory = TemporaryDirectory.Create();
        using var vault = CreateVault(TestAppPaths.Under(directory.Path));
        await vault.UnlockAsync("file mode".AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(vault.VaultPath));
    }

    [Fact]
    public async Task MissingLibSecretSelectsVaultAndSurfacesBanner()
    {
        var settings = new InMemorySettingsStore();
        var state = new CredentialSecurityState();
        ICredentialProvider[] providers =
        [
            new StubProvider("libsecret", isAvailable: false),
            new StubProvider("file-vault", isAvailable: true),
        ];
        var selector = new CredentialProviderSelector(settings, providers, CredentialPlatform.Linux, state);

        var selected = await selector.SelectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("file-vault", selected.Name);
        Assert.True(state.IsKeyringUnavailable);
        Assert.Equal(CredentialSecurityState.KeyringUnavailableBanner, state.BannerMessage);
    }

    [Fact]
    public async Task DeliberatelyForcedVaultDoesNotReportKeyringDowngrade()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.ForceFileVault, true, cancellationToken);
        var state = new CredentialSecurityState();
        ICredentialProvider[] providers = [new StubProvider("file-vault", isAvailable: true)];
        var selector = new CredentialProviderSelector(settings, providers, CredentialPlatform.Linux, state);

        _ = await selector.SelectAsync(cancellationToken);

        Assert.False(state.IsKeyringUnavailable);
        Assert.Null(state.BannerMessage);
    }

    private static EncryptedFileVaultProvider CreateVault(
        IAppPaths paths,
        PassphraseKdfParameters? parameters = null)
    {
        return new EncryptedFileVaultProvider(
            paths,
            new Argon2idPassphraseKdf(),
            new AesGcmAuthenticatedCipher(),
            new SecureRandom(),
            parameters ?? _fastParameters);
    }

    private sealed class StubProvider(string name, bool isAvailable) : ICredentialProvider
    {
        public string Name { get; } = name;

        public bool IsAvailable { get; } = isAvailable;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
