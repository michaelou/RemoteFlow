using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Validation;

public sealed record ConnectionInput(
    string? Name,
    string? Host,
    int Port,
    ProtocolType Protocol = ProtocolType.Ssh,
    string? Username = null,
    AuthMethod AuthMethod = AuthMethod.None,
    string? Notes = null,
    Guid? FolderId = null,
    EnvironmentKind Environment = EnvironmentKind.Unspecified,
    string? ColorOverrideHex = null,
    string? PrivateKeyPath = null,
    // Trust on first use, not the domain's Strict default: Strict rejects any host whose key is not
    // already stored and offers no way to store one, so it can only be chosen deliberately.
    HostKeyPolicy HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse);

public static class ConnectionValidator
{
    public static IReadOnlyList<RemoteFlowError> Validate(ConnectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<RemoteFlowError>();

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(RemoteFlowError.Validation("connection.name", "Enter a name for the connection."));
        }

        if (string.IsNullOrWhiteSpace(input.Host))
        {
            errors.Add(RemoteFlowError.Validation("connection.host", "Enter a host name or IP address."));
        }

        if (input.Port is < 1 or > 65_535)
        {
            errors.Add(RemoteFlowError.Validation("connection.port", "Enter a port between 1 and 65535."));
        }

        if (!Enum.IsDefined(input.Protocol))
        {
            errors.Add(RemoteFlowError.Validation("connection.protocol", "Choose a supported connection protocol."));
        }

        if (!Enum.IsDefined(input.AuthMethod))
        {
            errors.Add(RemoteFlowError.Validation("connection.auth_method", "Choose a supported authentication method."));
        }

        if (!Enum.IsDefined(input.Environment))
        {
            errors.Add(RemoteFlowError.Validation("connection.environment", "Choose a supported environment."));
        }

        if (!Enum.IsDefined(input.HostKeyPolicy))
        {
            errors.Add(RemoteFlowError.Validation("connection.host_key_policy", "Choose a supported host key policy."));
        }

        if (input.Protocol is ProtocolType.Ssh or ProtocolType.Sftp &&
            input.AuthMethod != AuthMethod.None &&
            string.IsNullOrWhiteSpace(input.Username))
        {
            errors.Add(RemoteFlowError.Validation(
                "connection.username",
                "Enter a username for this SSH or SFTP connection."));
        }

        if (input.AuthMethod == AuthMethod.PrivateKey && string.IsNullOrWhiteSpace(input.PrivateKeyPath))
        {
            errors.Add(RemoteFlowError.Validation(
                "connection.private_key_path",
                "Choose a private key file for private-key authentication."));
        }

        return errors;
    }
}
