using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.UI.Services;

public interface ISftpWorkspaceSessionFactory
{
    Task<SftpWorkspaceSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

public sealed class SftpWorkspaceSession(
    Connection definition,
    ISshConnection connection,
    ISftpService sftp) : IAsyncDisposable
{
    public Connection Definition { get; } = definition;

    public ISftpService Sftp { get; } = sftp;

    public async ValueTask DisposeAsync()
    {
        await Sftp.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class SftpWorkspaceSessionFactory(
    IConnectionRepository connections,
    ISshAuthenticationMaterialProvider authentication,
    ISshTransport transport,
    IRecentConnectionStore recent,
    IClock clock) : ISftpWorkspaceSessionFactory
{
    public async Task<SftpWorkspaceSession> OpenAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await connections.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection '{connectionId}' was not found.");
        if (!definition.SupportsSftp)
        {
            throw new InvalidOperationException("The selected connection does not support SFTP.");
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
            var session = new SftpWorkspaceSession(definition, connected.Value, connected.Value.OpenSftp());
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
