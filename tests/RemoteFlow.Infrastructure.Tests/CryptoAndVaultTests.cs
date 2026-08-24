using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Services.Backup;
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

    /// <summary>The unlockable face of the vault, used by the startup unlock flow. It exists because a wrong
    /// passphrase is an ordinary thing for a person to do, and the layer that asks cannot name
    /// <c>VaultUnlockException</c>.</summary>
    [Fact]
    public async Task TryUnlockCreatesAVaultThenOpensItAndRejectsAWrongPassphrase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);

        using (var created = CreateVault(paths))
        {
            // Nothing on disk yet: the first unlock is what brings a vault into being.
            Assert.False(created.Exists);
            Assert.False(created.IsUnlocked);

            Assert.Equal(
                VaultUnlockOutcome.Unlocked,
                await created.TryUnlockAsync("first-Passphrase9!".AsMemory(), cancellationToken));
            Assert.True(created.IsUnlocked);
            Assert.True(created.Exists);
            await created.SetAsync("remoteflow/test/key", "a secret".AsMemory(), "Test", cancellationToken);
        }

        using var reopened = CreateVault(paths);
        Assert.True(reopened.Exists);
        Assert.False(reopened.IsUnlocked);

        Assert.Equal(
            VaultUnlockOutcome.IncorrectPassphrase,
            await reopened.TryUnlockAsync("not-the-Passphrase9!".AsMemory(), cancellationToken));
        Assert.False(reopened.IsUnlocked);

        Assert.Equal(
            VaultUnlockOutcome.Unlocked,
            await reopened.TryUnlockAsync("first-Passphrase9!".AsMemory(), cancellationToken));
        using var secret = await reopened.GetAsync("remoteflow/test/key", cancellationToken);
        Assert.NotNull(secret);
        Assert.Equal("a secret", secret.Secret.ToString());
    }

    /// <summary>An empty passphrase reaches UnlockAsync as an ArgumentException, which is a programming
    /// error rather than an answer. The result-returning face has to absorb it: the prompt guards against
    /// empty input, but the vault must not throw out of a retry loop if anything ever gets past it.</summary>
    [Fact]
    public async Task TryUnlockTreatsAnEmptyPassphraseAsAWrongOne()
    {
        using var directory = TemporaryDirectory.Create();
        using var vault = CreateVault(TestAppPaths.Under(directory.Path));

        var outcome = await vault.TryUnlockAsync(
            ReadOnlyMemory<char>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(VaultUnlockOutcome.IncorrectPassphrase, outcome);
        Assert.False(vault.IsUnlocked);
    }

    /// <summary>The startup flow with real parts: the real selector choosing the real encrypted vault, and
    /// the real coordinator opening it. Everything except the dialog, which is the one piece a test cannot
    /// be. Proves a vault is created on first run and reopened on the next launch with the same passphrase.</summary>
    [Fact]
    public async Task TheUnlockFlowCreatesAVaultOnFirstRunAndReopensItOnTheNext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var settings = new InMemorySettingsStore();
        // Forced so the test does not depend on whether this machine happens to have a working keyring.
        await settings.Set(SettingKeys.ForceFileVault, true, cancellationToken);

        using (var vault = CreateVault(paths))
        {
            var prompt = new ScriptedPrompt("vault-Passphrase9!");
            using var service = new VaultUnlockService(
                new CredentialProviderSelector(settings, [vault]), prompt);

            var status = await service.EnsureUnlockedAsync(cancellationToken);

            Assert.True(status.IsUsable);
            Assert.True(status.WasPrompted);
            Assert.True(Assert.Single(prompt.Requests).IsNewVault);
            Assert.True(vault.IsUnlocked);
            await vault.SetAsync("remoteflow/test/key", "a secret".AsMemory(), "Test", cancellationToken);
        }

        // A second launch: same files, new objects, and this time the vault already exists.
        using var reopened = CreateVault(paths);
        var secondPrompt = new ScriptedPrompt("wrong-Passphrase9!", "vault-Passphrase9!");
        using var secondService = new VaultUnlockService(
            new CredentialProviderSelector(settings, [reopened]), secondPrompt);

        var second = await secondService.EnsureUnlockedAsync(cancellationToken);

        Assert.True(second.IsUsable);
        Assert.Equal(2, secondPrompt.Requests.Count);
        Assert.False(secondPrompt.Requests[0].IsNewVault);
        Assert.NotNull(secondPrompt.Requests[1].Problem);
        using var secret = await reopened.GetAsync("remoteflow/test/key", cancellationToken);
        Assert.NotNull(secret);
        Assert.Equal("a secret", secret.Secret.ToString());
    }

    /// <summary>Declining leaves the session running with the vault shut. The credential store then reports
    /// the situation rather than throwing, which is what the Backup page reads.</summary>
    [Fact]
    public async Task DecliningTheUnlockLeavesTheVaultShutAndReadable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.ForceFileVault, true, cancellationToken);
        using var vault = CreateVault(TestAppPaths.Under(directory.Path));
        using var service = new VaultUnlockService(
            new CredentialProviderSelector(settings, [vault]), new ScriptedPrompt());

        var status = await service.EnsureUnlockedAsync(cancellationToken);

        Assert.False(status.IsUsable);
        Assert.False(vault.IsUnlocked);
        Assert.NotNull(status.Problem);

        // The passphrase store used by automatic backup reports this rather than letting it escape.
        var passphrases = new AutoBackupPassphraseStore(
            new CredentialProviderSelector(settings, [vault]), [vault]);
        var state = await passphrases.InspectAsync(cancellationToken);

        Assert.False(state.IsUsable);
        Assert.Equal("The credential vault is locked.", state.Problem);
    }

    private sealed class ScriptedPrompt(params string[] answers) : IVaultUnlockPrompt
    {
        private int _next;

        public List<VaultUnlockPromptRequest> Requests { get; } = [];

        public ValueTask<VaultUnlockPromptResult?> PromptAsync(
            VaultUnlockPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_next >= answers.Length
                ? null
                : new VaultUnlockPromptResult(new SecretHandle(answers[_next++])));
        }
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
