using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class HostKeyVerifierTests
{
    private static readonly DateTimeOffset _firstSeen =
        new(2026, 8, 8, 1, 2, 3, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "AAAAC3NzaC1lZDI1NTE5AAAAIENmy7jU5FFM72fNnlrgouvnM+mSS78tiqawk7xyrMQN",
        "SHA256:gJUU1tpfem56VEvTV6HB9j5bL9uBBHWNKW9hOuebEB4")]
    [InlineData(
        "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBKw5ate8V5yM7vqzFvNfnRnKa6MTLMLFz6Yfr2qbKZV+b+/v93+6mVbGO+YWoZXcY8ZMeCwXXOwr0wXZlh1dibg=",
        "SHA256:0JG4JNH4EG8waZDWPl6reRJYp01mLqgagGShR96MFMk")]
    [InlineData(
        "AAAAB3NzaC1yc2EAAAADAQABAAABgQCXbTvQGkIwmyeZ8Lj4QrHiUkcCfthmWtXwZVSI1qr9W6FZJgc5di/qxisezcDw/++nJC8LcPwb3DyLH+l/QtsQ3QGAsb/bLSjhkY+VPjocX++Y+1Vr0qYCj8XtwiwpGasW+qiXg9l1TPJadYhRaP00AQtPMqjdZFyhP/oTN100jaECLOpk7KCOHWySel0NWr9tTJCgj9p76Vg3HLQA5XEK1BKm6GsthBN2HmV2EeKBggWYWHMP5ftLbPNZ4vY6BUO94e4PrKTAqvGtZAz9jKeHO1lg7wbHPA0A9k4eWi3mrw0bszapwBnLnAQGaFgTkJy+NgWiB1g+Is43rsxzMHWPm6TK3Dqiv5RvGXXEGEyUzRKMxwDYMBPhWG6LS7LoC+wlADpO28ul9mpA0Ka52D6GyhQDe4TxG7M0AZs3aLoijjihYWYFa4FFgW7M7uvwhcduz1LZDieq+cFQ960TyNmp7QkZvJUgxjTIjaHupAqRwPncTG8j8h4Kh3maEXHDXx8=",
        "SHA256:OVrKDSVAPJ+sGLFS/HHzFRpkiSQ2umL3JfblkrgdM00")]
    public void FingerprintMatchesOpenSshGoldenValues(string publicKeyBase64, string expected)
    {
        Assert.Equal(expected, HostKeyFingerprint.FormatSha256(Convert.FromBase64String(publicKeyBase64)));
    }

    [Fact]
    public async Task TofuPromptsOnceThenUpdatesOnlyLastSeen()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryHostKeyStore();
        var prompt = new RecordingPrompt(true);
        var clock = new FakeClock(_firstSeen);
        var verifier = CreateVerifier(store, prompt, clock);
        var request = Request(Key(1), HostKeyPolicy.TrustOnFirstUse);

        var first = await verifier.VerifyAsync(request, token);
        var initial = Assert.Single(await store.ListAsync(token));
        clock.Advance(TimeSpan.FromHours(2));
        var second = await verifier.VerifyAsync(request, token);
        var observed = Assert.Single(await store.ListAsync(token));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(_firstSeen, initial.FirstSeenUtc);
        Assert.Equal(_firstSeen, observed.FirstSeenUtc);
        Assert.Equal(clock.UtcNow, observed.LastSeenUtc);
        Assert.Equal(HostKeySource.UserAccepted, observed.Source);
    }

    [Theory]
    [InlineData(HostKeyPolicy.Strict)]
    [InlineData(HostKeyPolicy.TrustOnFirstUse)]
    public async Task ChangedKeyForKnownAlgorithmHardFails(HostKeyPolicy policy)
    {
        var token = TestContext.Current.CancellationToken;
        var store = await StoreWithAsync(Key(1), cancellationToken: token);
        var verifier = CreateVerifier(store, new RecordingPrompt(false));

        var result = await verifier.VerifyAsync(Request(Key(2), policy), token);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.HostKeyMismatch, result.Failure.Error);
        Assert.Equal(Convert.ToBase64String(Key(1).PublicKey), Assert.Single(await store.ListAsync(token)).PublicKeyBase64);
    }

    [Fact]
    public async Task AcceptAnyReplacesChangedKeyAndFlagsRecord()
    {
        var token = TestContext.Current.CancellationToken;
        var store = await StoreWithAsync(Key(1), cancellationToken: token);
        var verifier = CreateVerifier(store, new RecordingPrompt(false));

        var result = await verifier.VerifyAsync(Request(Key(2), HostKeyPolicy.AcceptAny), token);
        var saved = Assert.Single(await store.ListAsync(token));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsFlagged);
        Assert.Equal(HostKeySource.AcceptAny, saved.Source);
        Assert.NotNull(saved.Comment);
        Assert.Equal(Convert.ToBase64String(Key(2).PublicKey), saved.PublicKeyBase64);
        Assert.Equal(_firstSeen, saved.FirstSeenUtc);
    }

    [Fact]
    public async Task NewAlgorithmForKnownHostIsAcceptedAsRotationWithoutPrompt()
    {
        var token = TestContext.Current.CancellationToken;
        var store = await StoreWithAsync(Key(1), cancellationToken: token);
        var prompt = new RecordingPrompt(false);
        var verifier = CreateVerifier(store, prompt);
        var rotated = new HostKeyInfo("ecdsa-sha2-nistp256", [9, 8, 7, 6], "ignored");

        var result = await verifier.VerifyAsync(Request(rotated, HostKeyPolicy.Strict), token);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(2, (await store.ListAsync(token)).Count);
        Assert.Equal(
            HostKeySource.AlgorithmRotation,
            Assert.Single(
                await store.ListAsync(token),
                item => item.KeyAlgorithm == rotated.Algorithm).Source);
    }

    [Fact]
    public async Task StrictRejectsUnknownHostWithoutPersisting()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryHostKeyStore();
        var verifier = CreateVerifier(store, new RecordingPrompt(true));

        var result = await verifier.VerifyAsync(Request(Key(1), HostKeyPolicy.Strict), token);

        Assert.Equal(SshError.HostKeyUnknown, result.Failure.Error);
        Assert.Empty(await store.ListAsync(token));
    }

    [Fact]
    public async Task AcceptAnyUnknownHostRequiresRequestPolicyAndPersistsFlag()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryHostKeyStore();
        var verifier = CreateVerifier(store, new RecordingPrompt(false));

        var result = await verifier.VerifyAsync(Request(Key(1), HostKeyPolicy.AcceptAny), token);
        var saved = Assert.Single(await store.ListAsync(token));

        Assert.True(result.Value.IsFlagged);
        Assert.Equal(HostKeySource.AcceptAny, saved.Source);
        Assert.Contains("without identity verification", saved.Comment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostKeyPolicy.Strict)]
    [InlineData(HostKeyPolicy.TrustOnFirstUse)]
    [InlineData(HostKeyPolicy.AcceptAny)]
    public async Task RevokedKeyIsRejectedUnderEveryPolicy(HostKeyPolicy policy)
    {
        var token = TestContext.Current.CancellationToken;
        var store = await StoreWithAsync(
            Key(1),
            HostKeyTrust.Revoked,
            cancellationToken: token);
        var verifier = CreateVerifier(store, new RecordingPrompt(true));

        var result = await verifier.VerifyAsync(Request(Key(1), policy), token);

        Assert.Equal(SshError.HostKeyRevoked, result.Failure.Error);
    }

    [Fact]
    public async Task ConcurrentTofuVerificationPromptsAndInsertsOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var store = new InMemoryHostKeyStore();
        var prompt = new RecordingPrompt(true);
        var verifier = CreateVerifier(store, prompt);
        var request = Request(Key(1), HostKeyPolicy.TrustOnFirstUse);

        var results = await Task.WhenAll(
            verifier.VerifyAsync(request, token),
            verifier.VerifyAsync(request, token));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, prompt.CallCount);
        _ = Assert.Single(await store.ListAsync(token));
    }

    private static HostKeyVerifier CreateVerifier(
        InMemoryHostKeyStore store,
        IHostKeyPrompt prompt,
        FakeClock? clock = null)
    {
        return new HostKeyVerifier(
            store,
            prompt,
            clock ?? new FakeClock(_firstSeen),
            new FakeGuidProvider());
    }

    private static async Task<InMemoryHostKeyStore> StoreWithAsync(
        HostKeyInfo key,
        HostKeyTrust trust = HostKeyTrust.Trusted,
        CancellationToken cancellationToken = default)
    {
        var store = new InMemoryHostKeyStore();
        var entity = HostKey.Create(
            new FakeGuidProvider(),
            "server.test",
            22,
            key.Algorithm,
            Convert.ToBase64String(key.PublicKey),
            HostKeyFingerprint.FormatSha256(key.PublicKey),
            trust,
            HostKeySource.UserAccepted,
            seenUtc: _firstSeen).Value;
        await store.AddAsync(entity, cancellationToken);
        return store;
    }

    private static HostKeyVerificationRequest Request(HostKeyInfo key, HostKeyPolicy policy)
    {
        return new("server.test", 22, key, policy);
    }

    private static HostKeyInfo Key(byte suffix)
    {
        return new("ssh-ed25519", [1, 2, 3, suffix], "caller-supplied-fingerprint-is-ignored");
    }

    private sealed class RecordingPrompt(bool response) : IHostKeyPrompt
    {
        public int CallCount { get; private set; }

        public ValueTask<bool> ConfirmTrustAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(response);
        }
    }
}
