using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.UI.Services;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class WindowsEmbeddedRdpSessionProviderTests
{
    [Fact]
    public void WindowsRegistrationReplacesFallbackAndReportsEmbeddedSupport()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEmbeddedRdpSessionProvider>(FallbackProvider.Instance);
        _ = services.AddSingleton<IEmbeddedRdpWorkspaceSessionFactory>(FallbackWorkspaceFactory.Instance);

        _ = services.AddRemoteFlowRdpWindows();

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IEmbeddedRdpSessionProvider));
        Assert.NotNull(descriptor.ImplementationFactory);
        var concrete = Assert.Single(services, service => service.ServiceType == typeof(WindowsEmbeddedRdpSessionProvider));
        Assert.NotNull(concrete.ImplementationFactory);
        var workspaceFactory = Assert.Single(
            services,
            service => service.ServiceType == typeof(IEmbeddedRdpWorkspaceSessionFactory));
        Assert.Equal(typeof(WindowsRdpWorkspaceSessionFactory), workspaceFactory.ImplementationType);
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

    private sealed class FallbackWorkspaceFactory : IEmbeddedRdpWorkspaceSessionFactory
    {
        public static FallbackWorkspaceFactory Instance { get; } = new();

        public bool IsAvailableOnCurrentPlatform => false;

        public bool SupportsEmbeddedSessions => false;

        public Task<Result<IEmbeddedRdpWorkspaceSession>> CreateAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
