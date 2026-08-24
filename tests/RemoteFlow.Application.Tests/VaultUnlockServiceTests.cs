using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class VaultUnlockServiceTests
{
    /// <summary>The common case on Windows and macOS, and on Linux with a working keyring. Prompting there
    /// would be a modal dialog at every launch asking about something the OS already handled.</summary>
    [Fact]
    public async Task AProviderThatOpensItselfIsNeverPromptedFor()
    {
        var prompt = new RecordingPrompt();
        using var service = new VaultUnlockService(new FixedSelector(new PlainProvider()), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.False(status.WasPrompted);
        Assert.Empty(prompt.Requests);
    }

    [Fact]
    public async Task AVaultThatIsAlreadyOpenIsNotAskedAboutAgain()
    {
        var prompt = new RecordingPrompt();
        var vault = new FakeVault("right") { IsUnlocked = true };
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.False(status.WasPrompted);
        Assert.Empty(prompt.Requests);
    }

    [Fact]
    public async Task ALockedVaultIsOpenedWithTheEnteredPassphrase()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt("right");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.True(status.WasPrompted);
        Assert.True(vault.IsUnlocked);
        _ = Assert.Single(prompt.Requests);
    }

    [Fact]
    public async Task AWrongPassphraseIsRetriedAndTheReasonIsCarriedIntoTheNextAsk()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt("wrong", "right");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.Equal(2, prompt.Requests.Count);
        Assert.Null(prompt.Requests[0].Problem);
        Assert.Equal(1, prompt.Requests[0].Attempt);
        Assert.Contains("did not unlock", prompt.Requests[1].Problem!, StringComparison.Ordinal);
        Assert.Equal(2, prompt.Requests[1].Attempt);
    }

    [Fact]
    public async Task RetryingStopsAfterTheAttemptLimitRatherThanLoopingForever()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt("no", "no", "no", "no", "no");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Equal(VaultUnlockService.MaximumAttempts, prompt.Requests.Count);
        Assert.Contains("stayed locked", status.Problem!, StringComparison.Ordinal);
    }

    /// <summary>Declining is a real answer, not an error. RemoteFlow still runs; it just cannot remember
    /// secrets this session, and says so.</summary>
    [Fact]
    public async Task DecliningLeavesTheAppRunningWithAnExplanation()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt();
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.True(status.WasPrompted);
        Assert.False(vault.IsUnlocked);
        Assert.Contains("not unlocked", status.Problem, StringComparison.Ordinal);
    }

    /// <summary>A vault that does not exist yet is created by the first unlock, so the user is inventing a
    /// passphrase rather than recalling one — a different question, and the prompt has to be told which.</summary>
    [Fact]
    public async Task AMissingVaultIsAnnouncedAsANewOne()
    {
        var vault = new FakeVault("brand-new") { Exists = false };
        var prompt = new RecordingPrompt("brand-new");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.True(status.IsUsable);
        Assert.True(Assert.Single(prompt.Requests).IsNewVault);
    }

    [Fact]
    public async Task DecliningToCreateAVaultSaysSoInItsOwnWords()
    {
        var vault = new FakeVault("brand-new") { Exists = false };
        using var service = new VaultUnlockService(new FixedSelector(vault), new RecordingPrompt());

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Contains("no credential vault has been set up", status.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreadable file is not something retyping fixes, so it must not consume three attempts
    /// asking the user to try again.</summary>
    [Fact]
    public async Task AFailureThatIsNotThePassphraseStopsImmediately()
    {
        var vault = new FakeVault("right") { AlwaysFail = true };
        var prompt = new RecordingPrompt("right", "right", "right");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        _ = Assert.Single(prompt.Requests);
        Assert.Contains("could not be opened", status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectorThatThrowsIsReportedRatherThanCrashingStartup()
    {
        var prompt = new RecordingPrompt();
        using var service = new VaultUnlockService(new ThrowingSelector(), prompt);

        var status = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.False(status.IsUsable);
        Assert.Empty(prompt.Requests);
        Assert.Contains("no credential store", status.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Startup and a page both wanting credentials must not stack two dialogs asking the same
    /// question — and the second caller should find the vault already open.</summary>
    [Fact]
    public async Task ConcurrentCallersProduceOnlyOnePrompt()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt("right", "right");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        var results = await Task.WhenAll(
            service.EnsureUnlockedAsync(TestContext.Current.CancellationToken),
            service.EnsureUnlockedAsync(TestContext.Current.CancellationToken));

        Assert.All(results, status => Assert.True(status.IsUsable));
        _ = Assert.Single(prompt.Requests);
    }

    [Fact]
    public async Task TheEnteredPassphraseIsDisposedAfterUse()
    {
        var vault = new FakeVault("right");
        var prompt = new RecordingPrompt("right");
        using var service = new VaultUnlockService(new FixedSelector(vault), prompt);

        _ = await service.EnsureUnlockedAsync(TestContext.Current.CancellationToken);

        Assert.All(prompt.Issued, handle => Assert.True(handle.IsDisposed));
    }

    private sealed class FixedSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    private sealed class ThrowingSelector : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("No credential store is configured.");
        }
    }

    /// <summary>A provider the operating system opens for you — the shape of every store except the file
    /// vault, and the reason the check is "does it implement ICredentialVault" rather than a name match.</summary>
    private sealed class PlainProvider : ICredentialProvider
    {
        public string Name => "libsecret";

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVault(string correctPassphrase) : ICredentialProvider, ICredentialVault
    {
        public string Name => "file-vault";

        public bool IsAvailable => true;

        public bool IsUnlocked { get; set; }

        public bool Exists { get; init; } = true;

        public bool AlwaysFail { get; init; }

        public Task<VaultUnlockOutcome> TryUnlockAsync(
            ReadOnlyMemory<char> passphrase,
            CancellationToken cancellationToken = default)
        {
            if (AlwaysFail)
            {
                return Task.FromResult(VaultUnlockOutcome.Failed);
            }

            if (!string.Equals(new string(passphrase.Span), correctPassphrase, StringComparison.Ordinal))
            {
                return Task.FromResult(VaultUnlockOutcome.IncorrectPassphrase);
            }

            IsUnlocked = true;
            return Task.FromResult(VaultUnlockOutcome.Unlocked);
        }

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers with each supplied passphrase in turn, then declines — so a test that runs past its
    /// script ends with a cancelled prompt rather than an endless loop.</summary>
    private sealed class RecordingPrompt(params string[] answers) : IVaultUnlockPrompt
    {
        private int _next;

        public List<VaultUnlockPromptRequest> Requests { get; } = [];

        public List<SecretHandle> Issued { get; } = [];

        public ValueTask<VaultUnlockPromptResult?> PromptAsync(
            VaultUnlockPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_next >= answers.Length)
            {
                return ValueTask.FromResult<VaultUnlockPromptResult?>(null);
            }

            var handle = new SecretHandle(answers[_next++]);
            Issued.Add(handle);
            return ValueTask.FromResult<VaultUnlockPromptResult?>(new VaultUnlockPromptResult(handle));
        }
    }
}
