using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class WindowsEmbeddedRdpSessionProviderTests
{
    [Fact]
    public void WindowsRegistrationReplacesFallbackAndReportsEmbeddedSupport()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEmbeddedRdpSessionProvider>(FallbackProvider.Instance);

        _ = services.AddRemoteFlowRdpWindows();

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IEmbeddedRdpSessionProvider));
        var provider = Assert.IsType<WindowsEmbeddedRdpSessionProvider>(descriptor.ImplementationInstance);
        Assert.True(provider.SupportsEmbeddedSessions);
    }

    private sealed class FallbackProvider : IEmbeddedRdpSessionProvider
    {
        public static FallbackProvider Instance { get; } = new();

        public bool SupportsEmbeddedSessions => false;

        public Task<Result<IEmbeddedRdpSession>> CreateAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
