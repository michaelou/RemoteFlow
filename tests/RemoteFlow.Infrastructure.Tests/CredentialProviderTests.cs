using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class CredentialProviderTests
{
    [Fact]
    public void SecretHandleZeroesItsBackingBufferOnDispose()
    {
        var handle = new SecretHandle("sensitive-value".AsSpan());
        var observedBuffer = handle.Secret;

        handle.Dispose();

        Assert.True(handle.IsDisposed);
        Assert.Empty(handle.Secret.ToArray());
        Assert.All(observedBuffer.ToArray(), character => Assert.Equal('\0', character));
    }

    [Theory]
    [InlineData(CredentialPlatform.Windows, "windows-credman")]
    [InlineData(CredentialPlatform.MacOS, "macos-keychain")]
    [InlineData(CredentialPlatform.Linux, "libsecret")]
    public async Task SelectorChoosesProviderForPlatform(CredentialPlatform platform, string expectedName)
    {
        var settings = new InMemorySettingsStore();
        ICredentialProvider[] providers =
        [
            new RecordingCredentialProvider("windows-credman"),
            new RecordingCredentialProvider("macos-keychain"),
            new RecordingCredentialProvider("libsecret"),
            new RecordingCredentialProvider("file-vault"),
        ];
        var selector = new CredentialProviderSelector(settings, providers, platform);

        var selected = await selector.SelectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedName, selected.Name);
    }

    [Fact]
    public async Task SelectorHonorsForceFileVault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.ForceFileVault, true, cancellationToken);
        ICredentialProvider[] providers =
        [
            new RecordingCredentialProvider("windows-credman"),
            new RecordingCredentialProvider("file-vault"),
        ];
        var selector = new CredentialProviderSelector(settings, providers, CredentialPlatform.Windows);

        var selected = await selector.SelectAsync(cancellationToken);

        Assert.Equal("file-vault", selected.Name);
    }

    [Fact]
    public async Task DeleteConnectionCredentialsRemovesEveryConcreteKind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionId = Guid.NewGuid();
        var provider = new RecordingCredentialProvider("test");
        foreach (var kind in Enum.GetValues<CredentialKind>().Where(kind => kind != CredentialKind.None))
        {
            await provider.SetAsync(
                CredentialStoreKeys.ForConnection(connectionId, kind),
                "secret".AsMemory(),
                "test",
                cancellationToken);
        }

        await provider.DeleteConnectionCredentialsAsync(connectionId, cancellationToken);

        Assert.Empty(provider.Keys);
        Assert.Equal(3, provider.DeletedKeys.Count);
    }

    [Fact]
    public async Task WindowsCredentialProviderRoundTripsMissingAndDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var provider = new WindowsCredentialProvider(TestAppPaths.Under(directory.Path));
        var key = $"remoteflow/tests/{Guid.NewGuid():D}";
        await provider.DeleteAsync(key, cancellationToken);
        Assert.Null(await provider.GetAsync(key, cancellationToken));

        try
        {
            await provider.SetAsync(key, "credential-value".AsMemory(), "RemoteFlow test", cancellationToken);
            using var retrieved = await provider.GetAsync(key, cancellationToken);
            Assert.NotNull(retrieved);
            Assert.Equal("credential-value", retrieved.Secret.ToString());
        }
        finally
        {
            await provider.DeleteAsync(key, cancellationToken);
        }

        Assert.Null(await provider.GetAsync(key, cancellationToken));
    }

    [Fact]
    public async Task WindowsCredentialProviderRejectsOversizedSecretWithoutExposingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TemporaryDirectory.Create();
        var provider = new WindowsCredentialProvider(TestAppPaths.Under(directory.Path));
        var secret = new string('z', WindowsCredentialProvider.MaximumCredentialBlobBytes + 1);

        var exception = await Assert.ThrowsAsync<CredentialTooLargeException>(() => provider.SetAsync(
            $"remoteflow/tests/{Guid.NewGuid():D}",
            secret.AsMemory(),
            "RemoteFlow test",
            TestContext.Current.CancellationToken));

        Assert.Equal(secret.Length, exception.ActualBytes);
        Assert.Equal(WindowsCredentialProvider.MaximumCredentialBlobBytes, exception.MaximumBytes);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingCredentialProvider(string name) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public bool IsAvailable { get; init; } = true;

        public IReadOnlyCollection<string> Keys => _secrets.Keys;

        public List<string> DeletedKeys { get; } = [];

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.TryGetValue(storeKey, out var secret)
                ? new SecretHandle(secret.AsSpan())
                : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[storeKey] = secret.ToString();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedKeys.Add(storeKey);
            _ = _secrets.Remove(storeKey);
            return Task.CompletedTask;
        }
    }
}
