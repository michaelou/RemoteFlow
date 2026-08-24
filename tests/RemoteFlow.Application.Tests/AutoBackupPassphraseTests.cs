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

    /// <summary>A locked vault used to throw straight through this store and out of the Backup page. It has
    /// to come back as a reported problem instead — credential providers are platform integrations that
    /// throw types declared in a layer this one cannot even reference, so the catch must be broad.</summary>
    [Fact]
    public async Task ALockedCredentialStoreIsReportedRatherThanThrown()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new RecordingCredentialProvider { ThrowOnGet = new VaultIsLockedException() };
        var store = new AutoBackupPassphraseStore(new FixedSelector(provider), [provider]);

        Assert.Null(await store.GetAsync(token));

        var state = await store.InspectAsync(token);

        Assert.False(state.HasPassphrase);
        Assert.False(state.IsUsable);
        Assert.Equal("The credential vault is locked.", state.Problem);
    }

    /// <summary>"Locked" and "not set" must not collapse into one answer: only one of them is fixed by
    /// typing a new passphrase, and offering that to somebody with a locked vault wastes their time.</summary>
    [Fact]
    public async Task AnEmptyStoreAndAnUnreadableOneAreDifferentAnswers()
    {
        var token = TestContext.Current.CancellationToken;
        var empty = new AutoBackupPassphraseStore(
            new FixedSelector(new RecordingCredentialProvider()), [new RecordingCredentialProvider()]);
        var locked = new RecordingCredentialProvider { ThrowOnGet = new VaultIsLockedException() };

        var emptyState = await empty.InspectAsync(token);
        var lockedState = await new AutoBackupPassphraseStore(new FixedSelector(locked), [locked])
            .InspectAsync(token);

        Assert.True(emptyState.IsUsable);
        Assert.False(emptyState.HasPassphrase);
        Assert.False(lockedState.IsUsable);
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
