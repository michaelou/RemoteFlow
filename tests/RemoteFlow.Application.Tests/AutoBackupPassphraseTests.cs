using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services.Backup;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class AutoBackupPassphraseTests
{
    /// <summary>The store key is a compatibility surface: change it and every stored passphrase is orphaned,
    /// silently, with the only symptom being backups that start reporting Blocked.</summary>
    [Fact]
    public async Task SetStoresUnderTheDocumentedKey()
    {
        var provider = new RecordingCredentialProvider();
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);

        var result = await store.SetAsync(
            "correct-horse-Battery9!".AsMemory(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("remoteflow/auto-backup/passphrase", AutoBackupPassphraseStore.StoreKey);
        Assert.Equal(AutoBackupPassphraseStore.StoreKey, Assert.Single(provider.Stored.Keys));
    }

    /// <summary>Drives the automatic and manual gates from one table. If the extraction of the strength rule
    /// out of BackupService ever changes behaviour, this is what notices.</summary>
    [Theory]
    [InlineData("correct-horse-Battery9!", true)]
    [InlineData("Sh0rt!", false)]                       // under twelve characters
    [InlineData("alllowercaseletters", false)]          // one category
    [InlineData("alllowercase1234567", false)]          // two categories
    [InlineData("AllUpperLower12345", true)]            // three categories
    [InlineData("", false)]
    public async Task SetRejectsAWeakPassphraseWithTheSameRuleAsExport(string passphrase, bool expected)
    {
        var provider = new RecordingCredentialProvider();
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);

        var result = await store.SetAsync(passphrase.AsMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.IsSuccess);
        Assert.Equal(expected, PassphrasePolicy.IsStrong(passphrase));
        if (!expected)
        {
            Assert.Equal("autobackup.weak_passphrase", result.Error.Code);
        }
    }

    [Fact]
    public async Task InspectReportsWhatIsStoredWithoutRevealingIt()
    {
        var provider = new RecordingCredentialProvider();
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);
        var token = TestContext.Current.CancellationToken;

        Assert.False((await store.InspectAsync(token)).HasPassphrase);

        _ = await store.SetAsync("correct-horse-Battery9!".AsMemory(), token);

        Assert.True((await store.InspectAsync(token)).HasPassphrase);
    }

    /// <summary>There is no stored provider name to look up — putting one in the settings row would leak a
    /// machine-local fact into every exported archive — so a passphrase written before the user switched
    /// vaults has to be found by looking.</summary>
    [Fact]
    public async Task GetFindsThePassphraseStoredUnderADifferentProvider()
    {
        var token = TestContext.Current.CancellationToken;
        var oldProvider = new RecordingCredentialProvider("libsecret");
        var newProvider = new RecordingCredentialProvider("file vault");
        await oldProvider.SetAsync(AutoBackupPassphraseStore.StoreKey, "correct-horse-Battery9!".AsMemory(), "x", token);
        var store = new AutoBackupPassphraseStore(new FixedSelector(newProvider), [newProvider, oldProvider]);

        using var handle = await store.GetAsync(token);

        Assert.NotNull(handle);
        Assert.Equal("correct-horse-Battery9!", new string(handle.Secret.Span));
    }

    [Fact]
    public async Task ClearRemovesItFromEveryAvailableProvider()
    {
        var token = TestContext.Current.CancellationToken;
        var first = new RecordingCredentialProvider("libsecret");
        var second = new RecordingCredentialProvider("file vault");
        await first.SetAsync(AutoBackupPassphraseStore.StoreKey, "correct-horse-Battery9!".AsMemory(), "x", token);
        await second.SetAsync(AutoBackupPassphraseStore.StoreKey, "correct-horse-Battery9!".AsMemory(), "x", token);
        var store = new AutoBackupPassphraseStore(new FixedSelector(second), [first, second]);

        await store.ClearAsync(token);

        Assert.Empty(first.Stored);
        Assert.Empty(second.Stored);
        Assert.False((await store.InspectAsync(token)).HasPassphrase);
    }

    /// <summary>The Windows report: a perfectly good credential manager blamed for a locked file vault that
    /// was merely sitting in the provider list. EncryptedFileVaultProvider reports IsAvailable on every
    /// platform, including ones where nothing ever opens it, so scanning "other providers" for a passphrase
    /// reached it and turned its lock into the selected store's problem.</summary>
    [Fact]
    public async Task ALockedVaultElsewhereInTheListIsNotTheSelectedStoresProblem()
    {
        var token = TestContext.Current.CancellationToken;
        var windows = new RecordingCredentialProvider("windows-credman");
        var lockedVault = new LockedVaultProvider();
        var store = new AutoBackupPassphraseStore(new FixedSelector(windows), [windows, lockedVault]);

        var state = await store.InspectAsync(token);

        Assert.True(state.IsUsable);
        Assert.Null(state.Problem);
        Assert.False(state.HasPassphrase);
        // Never even asked: reading a locked vault can only throw.
        Assert.False(lockedVault.WasRead);
    }

    [Fact]
    public async Task AWorkingStoreStillFindsItsPassphraseAlongsideALockedVault()
    {
        var token = TestContext.Current.CancellationToken;
        var windows = new RecordingCredentialProvider("windows-credman");
        await windows.SetAsync(AutoBackupPassphraseStore.StoreKey, "correct-horse-Battery9!".AsMemory(), "x", token);
        var store = new AutoBackupPassphraseStore(
            new FixedSelector(windows), [windows, new LockedVaultProvider()]);

        var state = await store.InspectAsync(token);
        using var handle = await store.GetAsync(token);

        Assert.True(state.HasPassphrase);
        Assert.True(state.IsUsable);
        Assert.NotNull(handle);
    }

    /// <summary>When the locked vault IS the selected store, its state is read directly rather than inferred
    /// from a failed lookup — which is both more reliable and the only case that should report a problem.</summary>
    [Fact]
    public async Task ALockedVaultThatIsTheSelectedStoreIsReportedWithoutBeingRead()
    {
        var token = TestContext.Current.CancellationToken;
        var lockedVault = new LockedVaultProvider();
        var store = new AutoBackupPassphraseStore(new FixedSelector(lockedVault), [lockedVault]);

        var state = await store.InspectAsync(token);

        Assert.False(state.IsUsable);
        Assert.Equal("The credential vault is locked.", state.Problem);
        Assert.False(lockedVault.WasRead);
    }

    /// <summary>A provider throwing on read used to travel out of this store and out of the Backup page.
    /// Credential providers are platform integrations that throw types declared in a layer this one cannot
    /// reference, so the catch has to be broad — and the failure is not evidence about the store's health,
    /// only about that one read, so it reads as "nothing stored" rather than "store broken".</summary>
    [Fact]
    public async Task AProviderThatThrowsOnReadNeverThrowsOutOfTheStore()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new RecordingCredentialProvider { ThrowOnGet = new VaultIsLockedException() };
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);

        Assert.Null(await store.GetAsync(token));

        var state = await store.InspectAsync(token);

        Assert.False(state.HasPassphrase);
        Assert.True(state.IsUsable);
    }

    /// <summary>"Locked" and "not set" must not collapse into one answer: only one of them is fixed by
    /// typing a new passphrase, and offering that to somebody with a locked vault wastes their time. The
    /// difference is read from the vault's own state, never guessed from a failed lookup.</summary>
    [Fact]
    public async Task AnEmptyStoreAndALockedVaultAreDifferentAnswers()
    {
        var token = TestContext.Current.CancellationToken;
        var plain = new RecordingCredentialProvider();
        var locked = new LockedVaultProvider();

        var emptyState = await new AutoBackupPassphraseStore(new FixedSelector(plain), [plain])
            .InspectAsync(token);
        var lockedState = await new AutoBackupPassphraseStore(new FixedSelector(locked), [locked])
            .InspectAsync(token);

        Assert.True(emptyState.IsUsable);
        Assert.False(emptyState.HasPassphrase);
        Assert.False(lockedState.IsUsable);
        Assert.NotEqual(emptyState.Problem, lockedState.Problem);
    }

    [Fact]
    public async Task SavingIntoAnUnusableStoreFailsWithTheProvidersOwnReason()
    {
        var provider = new RecordingCredentialProvider { ThrowOnSet = new VaultIsLockedException() };
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);

        var result = await store.SetAsync(
            "correct-horse-Battery9!".AsMemory(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("autobackup.passphrase_store_unavailable", result.Error.Code);
        Assert.Contains("locked", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearingKeepsGoingWhenOneProviderRefuses()
    {
        var token = TestContext.Current.CancellationToken;
        var stubborn = new RecordingCredentialProvider("libsecret") { ThrowOnDelete = new VaultIsLockedException() };
        var cooperative = new RecordingCredentialProvider("file vault");
        await cooperative.SetAsync(AutoBackupPassphraseStore.StoreKey, "correct-horse-Battery9!".AsMemory(), "x", token);
        var store = new AutoBackupPassphraseStore(new FixedSelector(cooperative), [stubborn, cooperative]);

        await store.ClearAsync(token);

        Assert.Empty(cooperative.Stored);
    }

    /// <summary>Stands in for the infrastructure layer's VaultLockedException, which the Application layer
    /// cannot reference — which is the whole reason the catch there has to be broad.</summary>
    private sealed class VaultIsLockedException : Exception
    {
        public VaultIsLockedException()
            : base("The credential vault is locked.")
        {
        }
    }

    /// <summary>Stands in for EncryptedFileVaultProvider: available on every platform, and throwing on every
    /// read until something unlocks it.</summary>
    private sealed class LockedVaultProvider : ICredentialProvider, ICredentialVault
    {
        public bool WasRead { get; private set; }

        public string Name => "file-vault";

        public bool IsAvailable => true;

        public bool IsUnlocked => false;

        public bool Exists => true;

        public Task<VaultUnlockOutcome> TryUnlockAsync(
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(VaultUnlockOutcome.IncorrectPassphrase);
        }

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            throw new InvalidOperationException("The credential vault is locked.");
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The credential vault is locked.");
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The credential vault is locked.");
        }
    }

    private sealed class FixedSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    private sealed class RecordingCredentialProvider(string name = "test keyring") : ICredentialProvider
    {
        public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

        public Exception? ThrowOnGet { get; init; }

        public Exception? ThrowOnSet { get; init; }

        public Exception? ThrowOnDelete { get; init; }

        public string Name => name;

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return ThrowOnGet is not null
                ? throw ThrowOnGet
                : Task.FromResult(Stored.TryGetValue(storeKey, out var secret) ? new SecretHandle(secret) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSet is not null)
            {
                throw ThrowOnSet;
            }

            Stored[storeKey] = new string(secret.Span);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete is not null)
            {
                throw ThrowOnDelete;
            }

            _ = Stored.Remove(storeKey);
            return Task.CompletedTask;
        }
    }
}
