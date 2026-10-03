using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.Domain.Entities;
using RemoteFlow.UI.Navigation;

namespace RemoteFlow.UI.Services;

public interface IDockerWorkspaceSessionFactory
{
    Task<DockerWorkspaceSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

/// <summary>One SSH connection held open for the Docker page. Every list, action and log on the page is a
/// separate exec channel on it, so a refresh does not pay for a handshake.</summary>
public sealed class DockerWorkspaceSession(
    Connection definition,
    ISshConnection connection,
    IDockerHost docker) : IAsyncDisposable
{
    public Connection Definition { get; } = definition;

    public IDockerHost Docker { get; } = docker;

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The SFTP page's factory with a Docker host in place of an SFTP channel: the same connection
/// lookup, the same credentials, the same host-key policy.</summary>
public sealed class DockerWorkspaceSessionFactory(
    IConnectionRepository connections,
    ISshAuthenticationMaterialProvider authentication,
    ISshTransport transport,
    IRecentConnectionStore recent,
    IClock clock) : IDockerWorkspaceSessionFactory
{
    public async Task<DockerWorkspaceSession> OpenAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        if (!definition.SupportsSftp)
        {
            throw new InvalidOperationException("Docker is managed over SSH, and the selected connection is not an SSH connection.");
        }

        var materials = await authentication.CreateAsync(definition, cancellationToken).ConfigureAwait(false);
        var connected = await transport.ConnectAsync(new SshConnectRequest
        {
            Host = definition.Host,
            Port = definition.Port,
            Username = definition.Username ?? throw new InvalidOperationException("The SSH username is required."),
            AuthenticationMethods = materials,
            HostKeyPolicy = definition.Ssh.HostKeyPolicy,
            KeepAliveInterval = TimeSpan.FromSeconds(definition.Ssh.KeepAliveSeconds ?? 30),
            OperationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken).ConfigureAwait(false);
        if (connected.IsFailure)
        {
            throw new InvalidOperationException(connected.Failure.Message);
        }

        try
        {
            var session = new DockerWorkspaceSession(
                definition,
                connected.Value,
                new DockerCliHost(connected.Value, definition.Username));
            await recent.RecordOpenedAsync(definition.Id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await connected.Value.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Opens a terminal tab whose shell is inside a container.</summary>
public interface IContainerShellOpener
{
    Task OpenAsync(
        Guid connectionId,
        string connectionName,
        string containerId,
        string containerName,
        CancellationToken cancellationToken = default);
}

/// <summary>A container shell is an ordinary SSH terminal session that types <c>docker exec -it</c> once it is
/// up, so it gets the terminal page's tabs, grid, search and reconnect for free. The navigation service is
/// looked up when it is used rather than injected: it is built from the page registrations, and the Docker
/// page is one of them.</summary>
public sealed class ContainerShellOpener(ISessionManager sessions, IServiceProvider services) : IContainerShellOpener
{
    public async Task OpenAsync(
        Guid connectionId,
        string connectionName,
        string containerId,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        var command = DockerCli.ShellCommand(containerId);
        services.GetRequiredService<INavigationService>().Navigate("terminals");
        _ = await sessions.OpenAsync(
            connectionId,
            new SessionOpenOptions
            {
                Title = $"{connectionName} › {containerName}",
                StartupCommand = command,
            },
            cancellationToken).ConfigureAwait(true);
    }
}
