using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

[Trait("Platform", "Linux")]
public sealed class LibSecretProviderTests
{
    [Fact]
    public void AvailabilityIsFalseOffLinux()
    {
        using var provider = new LibSecretProvider();
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(provider.IsAvailable);
        }
    }

    [Fact]
    public async Task DesktopKeyringRoundTripsAndDeletes()
    {
        Assert.SkipUnless(
            OperatingSystem.IsLinux() &&
            string.Equals(Environment.GetEnvironmentVariable("REMOTEFLOW_RUN_LIBSECRET_TESTS"), "1", StringComparison.Ordinal),
            "Set REMOTEFLOW_RUN_LIBSECRET_TESTS=1 in a GNOME or KDE desktop session.");
        var cancellationToken = TestContext.Current.CancellationToken;
        using var provider = new LibSecretProvider();
        Assert.True(provider.IsAvailable);
        var key = $"remoteflow/tests/{Guid.NewGuid():D}";
        await provider.DeleteAsync(key, cancellationToken);
        Assert.Null(await provider.GetAsync(key, cancellationToken));
        try
        {
            await provider.SetAsync(key, "libsecret-value".AsMemory(), "RemoteFlow test", cancellationToken);
            using var retrieved = await provider.GetAsync(key, cancellationToken);
            Assert.NotNull(retrieved);
            Assert.Equal("libsecret-value", retrieved.Secret.ToString());
        }
        finally
        {
            await provider.DeleteAsync(key, cancellationToken);
        }
    }
}
