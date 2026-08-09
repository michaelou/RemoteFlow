using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class EmbeddedRdpSessionProviderTests
{
    [Fact]
    public async Task ApplicationRegistrationUsesExternalClientFallback()
    {
        var services = new ServiceCollection();

        _ = services.AddRemoteFlowApplication();

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IEmbeddedRdpSessionProvider));
        var provider = Assert.IsType<NoEmbeddedRdpSessionProvider>(descriptor.ImplementationInstance);
        Assert.False(provider.SupportsEmbeddedSessions);

        var connection = new ConnectionBuilder().WithProtocol(ProtocolType.Rdp).Build();
        var result = await provider.CreateAsync(connection, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(RemoteFlowErrorKind.Unavailable, result.Error.Kind);
        Assert.Contains("external RDP client", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonRdpConnectionReturnsTypedFailure()
    {
        var connection = new ConnectionBuilder().WithProtocol(ProtocolType.Ssh).Build();

        var result = await NoEmbeddedRdpSessionProvider.Instance.CreateAsync(
            connection,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(RemoteFlowErrorKind.Validation, result.Error.Kind);
        Assert.Equal("embedded_rdp.not_an_rdp_connection", result.Error.Code);
    }
}
