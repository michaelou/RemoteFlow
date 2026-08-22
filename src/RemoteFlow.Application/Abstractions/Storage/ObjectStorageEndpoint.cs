using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>Everything an adapter needs to reach an account, resolved from a connection. <c>Host</c> and
/// <c>Port</c> on the connection are the real service endpoint — derived by the editor, but stored and
/// true — so a sovereign-cloud account is reached by hand-editing the host rather than by a new field.
/// </summary>
public sealed record ObjectStorageEndpoint(
    ProtocolType Protocol,
    string Host,
    int Port,
    string AccessKeyId,
    string? Region,
    Uri? ServiceUrl,
    bool UsePathStyleAddressing,
    string? Container,
    string? RootPrefix)
{
    public const int DefaultPort = 443;

    private const string _awsSuffix = "amazonaws.com";

    private const string _azureBlobSuffix = "blob.core.windows.net";

    /// <summary>Where browsing starts. A connection pinned to one container is rooted inside it, and the
    /// adapter never lists buckets; without one, the root lists the account's containers.</summary>
    public string RootPath => Container is null
        ? ObjectStoragePath.Root
        : ObjectStoragePath.Combine(ObjectStoragePath.Root, Container);

    /// <summary>The endpoint host the editor should put in the host box, or null when the fields it is
    /// derived from are not filled in yet. The editor overwrites the box only while the user has not
    /// hand-edited it, the same rule the port box already follows.</summary>
    public static string? DeriveHost(
        ProtocolType protocol,
        string? region,
        string? accessKeyId,
        string? serviceUrl)
    {
        if (!string.IsNullOrWhiteSpace(serviceUrl) &&
            Uri.TryCreate(serviceUrl.Trim(), UriKind.Absolute, out var custom))
        {
            return custom.Authority;
        }

        var normalizedRegion = region?.Trim();
        var normalizedAccount = accessKeyId?.Trim();
        return protocol switch
        {
            ProtocolType.S3 => string.IsNullOrEmpty(normalizedRegion)
                ? $"s3.{_awsSuffix}"
                : $"s3.{normalizedRegion}.{_awsSuffix}",
            // Azure addresses the storage account, and the account name is the identifier the user typed.
            ProtocolType.AzureBlob => string.IsNullOrEmpty(normalizedAccount)
                ? null
                : $"{normalizedAccount}.{_azureBlobSuffix}",
            ProtocolType.Ssh => null,
            ProtocolType.Sftp => null,
            ProtocolType.Rdp => null,
            _ => null,
        };
    }

    public static SftpResult<ObjectStorageEndpoint> Create(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.Protocol.IsObjectStorage())
        {
            return SftpResult<ObjectStorageEndpoint>.Fail(
                SftpError.NotSupported,
                $"'{connection.Name}' is not an object-storage connection.");
        }

        if (string.IsNullOrWhiteSpace(connection.Username))
        {
            return SftpResult<ObjectStorageEndpoint>.Fail(
                SftpError.PermissionDenied,
                connection.Protocol == ProtocolType.S3
                    ? "The connection has no access key ID."
                    : "The connection has no storage account name.");
        }

        var options = connection.ObjectStorage;
        Uri? serviceUrl = null;
        return options.ServiceUrl is { } raw && !Uri.TryCreate(raw, UriKind.Absolute, out serviceUrl)
            ? SftpResult<ObjectStorageEndpoint>.Fail(
                SftpError.InvalidPath,
                $"'{raw}' is not a usable service endpoint.")
            : SftpResult<ObjectStorageEndpoint>.Success(new ObjectStorageEndpoint(
                connection.Protocol,
                connection.Host,
                connection.Port,
                connection.Username.Trim(),
                options.Region,
                serviceUrl,
                options.UsePathStyleAddressing,
                options.Container,
                options.RootPrefix));
    }

    /// <summary>Turns a path the UI holds into the container and key the provider is asked about, applying
    /// the connection's root prefix and refusing to escape a pinned container.</summary>
    public SftpResult<(string? Container, string Key)> Resolve(string path)
    {
        var (container, key) = ObjectStoragePath.Split(path);
        if (Container is not null)
        {
            if (container is not null && !string.Equals(container, Container, StringComparison.Ordinal))
            {
                return SftpResult<(string?, string)>.Fail(
                    SftpError.InvalidPath,
                    $"This connection is scoped to '{Container}'.");
            }

            container ??= Container;
        }

        if (RootPrefix is { Length: > 0 } prefix)
        {
            key = key.Length == 0 ? prefix : prefix + ObjectStoragePath.Separator + key;
        }

        return SftpResult<(string?, string)>.Success((container, key));
    }

    /// <summary>The inverse of <see cref="Resolve"/>: turns a container and provider key back into the
    /// account-rooted path the UI holds, hiding the connection's root prefix again.</summary>
    public string ToPath(string? container, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var relative = key;
        if (RootPrefix is { Length: > 0 } prefix)
        {
            if (relative.Equals(prefix, StringComparison.Ordinal))
            {
                relative = string.Empty;
            }
            else if (relative.StartsWith(prefix + ObjectStoragePath.Separator, StringComparison.Ordinal))
            {
                relative = relative[(prefix.Length + 1)..];
            }
        }

        var path = container is null
            ? ObjectStoragePath.Root
            : ObjectStoragePath.Combine(ObjectStoragePath.Root, container);
        return relative.Length == 0 ? path : ObjectStoragePath.Combine(path, relative);
    }
}
