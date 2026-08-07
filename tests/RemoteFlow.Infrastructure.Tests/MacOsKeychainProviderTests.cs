using System.Diagnostics;
using System.Reflection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Security;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

[Trait("Platform", "macOS")]
public sealed class MacOsKeychainProviderTests
{
    [Fact]
    public void ProviderAvailabilityMatchesOperatingSystem()
    {
        var provider = new MacOsKeychainProvider();

        Assert.Equal(OperatingSystem.IsMacOS(), provider.IsAvailable);
        Assert.Equal("macos-keychain", provider.Name);
    }

    [Fact]
    public void ProviderDoesNotDependOnTheExternalProcessRunner()
    {
        var type = typeof(MacOsKeychainProvider);
        Assert.DoesNotContain(
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => typeof(IProcessRunner).IsAssignableFrom(field.FieldType));
        Assert.All(
            type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.DoesNotContain(
                constructor.GetParameters(),
                parameter => typeof(IProcessRunner).IsAssignableFrom(parameter.ParameterType)));
    }

    [Fact]
    public async Task RoundTripsUpdatesMissingAndDeletes()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires macOS Keychain.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MacOsKeychainProvider();
        var key = $"remoteflow/tests/{Guid.NewGuid():D}";
        await provider.DeleteAsync(key, cancellationToken);
        Assert.Null(await provider.GetAsync(key, cancellationToken));

        try
        {
            await provider.SetAsync(key, "first-value".AsMemory(), "RemoteFlow test", cancellationToken);
            using (var first = await provider.GetAsync(key, cancellationToken))
            {
                Assert.NotNull(first);
                Assert.Equal("first-value", first.Secret.ToString());
            }

            await provider.SetAsync(key, "updated-value".AsMemory(), "RemoteFlow test", cancellationToken);
            using var updated = await provider.GetAsync(key, cancellationToken);
            Assert.NotNull(updated);
            Assert.Equal("updated-value", updated.Secret.ToString());
        }
        finally
        {
            await provider.DeleteAsync(key, cancellationToken);
        }

        Assert.Null(await provider.GetAsync(key, cancellationToken));
    }

    [Fact]
    public async Task RepeatedReadsDoNotLeakCoreFoundationObjects()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires macOS Keychain.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MacOsKeychainProvider();
        var key = $"remoteflow/tests/{Guid.NewGuid():D}";
        await provider.SetAsync(key, "leak-check".AsMemory(), "RemoteFlow test", cancellationToken);
        try
        {
            using (var warmup = await provider.GetAsync(key, cancellationToken))
            {
                Assert.NotNull(warmup);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var before = process.PrivateMemorySize64;
            for (var iteration = 0; iteration < 1_000; iteration++)
            {
                using var secret = await provider.GetAsync(key, cancellationToken);
                Assert.NotNull(secret);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            process.Refresh();
            var growth = process.PrivateMemorySize64 - before;
            Assert.True(growth < 32 * 1024 * 1024, $"Private memory grew by {growth} bytes.");
        }
        finally
        {
            await provider.DeleteAsync(key, cancellationToken);
        }
    }
}
