using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;

namespace RemoteFlow.Domain.Entities;

public sealed class Connection
{
    private readonly List<ConnectionTag> _tags = [];

    private Connection()
    {
        Name = string.Empty;
        Host = string.Empty;
        Credential = CredentialRef.None();
        Ssh = SshOptions.Default();
        Sftp = SftpOptions.Default();
        Rdp = RdpOptions.Default();
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string Host { get; private set; }

    public int Port { get; private set; }

    public ProtocolType Protocol { get; private set; }

    public string? Username { get; private set; }

    public AuthMethod AuthMethod { get; private set; }

    public string? Notes { get; private set; }

    public Guid? FolderId { get; private set; }

    public bool IsFavorite { get; private set; }

    public EnvironmentKind Environment { get; private set; }

    public string? ColorOverrideHex { get; private set; }

    public int? SortOrder { get; private set; }

    public Guid ConcurrencyStamp { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset ModifiedUtc { get; private set; }

    public CredentialRef Credential { get; private set; }

    public SshOptions Ssh { get; private set; }

    public SftpOptions Sftp { get; private set; }

    public RdpOptions Rdp { get; private set; }

    public ICollection<ConnectionTag> Tags => _tags;

    public bool SupportsSftp => Protocol is ProtocolType.Ssh or ProtocolType.Sftp;

    public static Result<Connection> Create(
        IGuidProvider guidProvider,
        string? name,
        string? host,
        ProtocolType protocol = ProtocolType.Ssh,
        DateTimeOffset? createdUtc = null)
    {
        return !Enum.IsDefined(protocol)
            ? Result<Connection>.Failure(RemoteFlowError.Validation("connection.protocol", "The protocol is invalid."))
            : Create(guidProvider, name, host, protocol.GetDefaultPort(), protocol, createdUtc);
    }

    public static Result<Connection> Create(
        IGuidProvider guidProvider,
        string? name,
        string? host,
        int port,
        ProtocolType protocol = ProtocolType.Ssh,
        DateTimeOffset? createdUtc = null)
    {
        var normalizedName = DomainValidation.Required(name, 100, "connection.name", out var error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        var normalizedHost = DomainValidation.Required(host, 255, "connection.host", out error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        if (port is < 1 or > 65_535)
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation(
                "connection.port",
                "The port must be between 1 and 65535."));
        }

        if (!Enum.IsDefined(protocol))
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation("connection.protocol", "The protocol is invalid."));
        }

        var now = DomainValidation.Utc(createdUtc ?? DateTimeOffset.UtcNow);
        return Result<Connection>.Success(new Connection
        {
            Id = DomainValidation.NewRequiredGuid(guidProvider),
            Name = normalizedName!,
            Host = normalizedHost!,
            Port = port,
            Protocol = protocol,
            AuthMethod = AuthMethod.None,
            Environment = EnvironmentKind.Unspecified,
            ConcurrencyStamp = DomainValidation.NewRequiredGuid(guidProvider),
            CreatedUtc = now,
            ModifiedUtc = now,
        });
    }

    public Result<Connection> Rename(string? name, IGuidProvider guidProvider, DateTimeOffset? modifiedUtc = null)
    {
        var normalized = DomainValidation.Required(name, 100, "connection.name", out var error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        Name = normalized!;
        Touch(guidProvider, modifiedUtc);
        return Result<Connection>.Success(this);
    }

    public Result<Connection> ChangeEndpoint(
        string? host,
        int port,
        ProtocolType protocol,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        var normalized = DomainValidation.Required(host, 255, "connection.host", out var error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        if (port is < 1 or > 65_535)
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation("connection.port", "The port must be between 1 and 65535."));
        }

        if (!Enum.IsDefined(protocol))
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation("connection.protocol", "The protocol is invalid."));
        }

        Host = normalized!;
        Port = port;
        Protocol = protocol;
        Touch(guidProvider, modifiedUtc);
        return Result<Connection>.Success(this);
    }

    public Result<Connection> SetDetails(
        string? username,
        AuthMethod authMethod,
        string? notes,
        EnvironmentKind environment,
        string? colorOverrideHex,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        if (!Enum.IsDefined(authMethod))
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation("connection.auth_method", "The authentication method is invalid."));
        }

        if (!Enum.IsDefined(environment))
        {
            return Result<Connection>.Failure(RemoteFlowError.Validation("connection.environment", "The environment is invalid."));
        }

        var normalizedNotes = DomainValidation.Optional(notes, 4_000, "connection.notes", out var error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        var normalizedColor = DomainValidation.ColorHex(colorOverrideHex, "connection.color", out error);
        if (error is not null)
        {
            return Result<Connection>.Failure(error);
        }

        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        AuthMethod = authMethod;
        Notes = normalizedNotes;
        Environment = environment;
        ColorOverrideHex = normalizedColor;
        Touch(guidProvider, modifiedUtc);
        return Result<Connection>.Success(this);
    }

    public Connection SetFolder(Guid? folderId, IGuidProvider guidProvider, DateTimeOffset? modifiedUtc = null)
    {
        FolderId = folderId;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    public Connection SetFavorite(bool isFavorite, IGuidProvider guidProvider, DateTimeOffset? modifiedUtc = null)
    {
        IsFavorite = isFavorite;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    public Connection SetSortOrder(int? sortOrder, IGuidProvider guidProvider, DateTimeOffset? modifiedUtc = null)
    {
        SortOrder = sortOrder;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    public Connection SetCredential(CredentialRef credential, IGuidProvider guidProvider, DateTimeOffset? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Credential = credential;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    public Connection SetOptions(
        SshOptions ssh,
        SftpOptions sftp,
        RdpOptions rdp,
        IGuidProvider guidProvider,
        DateTimeOffset? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(ssh);
        ArgumentNullException.ThrowIfNull(sftp);
        ArgumentNullException.ThrowIfNull(rdp);
        Ssh = ssh;
        Sftp = sftp;
        Rdp = rdp;
        Touch(guidProvider, modifiedUtc);
        return this;
    }

    public Result<ConnectionTag> AddTag(Guid tagId)
    {
        if (tagId == Guid.Empty)
        {
            return Result<ConnectionTag>.Failure(RemoteFlowError.Validation("connection_tag.tag_id", "The tag ID is required."));
        }

        if (_tags.Any(item => item.TagId == tagId))
        {
            return Result<ConnectionTag>.Failure(RemoteFlowError.Validation("connection_tag.duplicate", "The tag is already attached."));
        }

        var join = new ConnectionTag(Id, tagId);
        _tags.Add(join);
        return Result<ConnectionTag>.Success(join);
    }

    public bool RemoveTag(Guid tagId)
    {
        var item = _tags.Find(candidate => candidate.TagId == tagId);
        return item is not null && _tags.Remove(item);
    }

    private void Touch(IGuidProvider guidProvider, DateTimeOffset? modifiedUtc)
    {
        ConcurrencyStamp = DomainValidation.NewRequiredGuid(guidProvider);
        ModifiedUtc = DomainValidation.Utc(modifiedUtc ?? DateTimeOffset.UtcNow);
    }
}
