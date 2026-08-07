using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.TestSupport;

public sealed class ConnectionBuilder
{
    private string _name = "Test server";
    private string _host = "server.example.com";
    private ProtocolType _protocol = ProtocolType.Ssh;
    private IGuidProvider _guidProvider = new FakeGuidProvider();
    private DateTimeOffset _createdUtc = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public ConnectionBuilder WithName(string name) { _name = name; return this; }

    public ConnectionBuilder WithHost(string host) { _host = host; return this; }

    public ConnectionBuilder WithProtocol(ProtocolType protocol) { _protocol = protocol; return this; }

    public ConnectionBuilder WithGuidProvider(IGuidProvider guidProvider) { _guidProvider = guidProvider; return this; }

    public ConnectionBuilder CreatedAt(DateTimeOffset createdUtc) { _createdUtc = createdUtc; return this; }

    public Connection Build()
    {
        return Connection.Create(_guidProvider, _name, _host, _protocol, _createdUtc).Value;
    }
}

public sealed class FolderBuilder
{
    private string _name = "Servers";
    private Folder? _parent;
    private IGuidProvider _guidProvider = new FakeGuidProvider();
    private readonly DateTimeOffset _createdUtc = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public FolderBuilder WithName(string name) { _name = name; return this; }

    public FolderBuilder Under(Folder parent) { _parent = parent; return this; }

    public FolderBuilder WithGuidProvider(IGuidProvider guidProvider) { _guidProvider = guidProvider; return this; }

    public Folder Build()
    {
        return Folder.Create(_guidProvider, _name, _parent, createdUtc: _createdUtc).Value;
    }
}

public sealed class TagBuilder
{
    private string _name = "Production";
    private string? _colorHex;
    private IGuidProvider _guidProvider = new FakeGuidProvider();

    public TagBuilder WithName(string name) { _name = name; return this; }

    public TagBuilder WithColor(string? colorHex) { _colorHex = colorHex; return this; }

    public TagBuilder WithGuidProvider(IGuidProvider guidProvider) { _guidProvider = guidProvider; return this; }

    public Tag Build()
    {
        return Tag.Create(_guidProvider, _name, _colorHex, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)).Value;
    }
}

public sealed class HostKeyBuilder
{
    private string _host = "server.example.com";
    private int _port = 22;
    private string _algorithm = "ssh-ed25519";
    private IGuidProvider _guidProvider = new FakeGuidProvider();

    public HostKeyBuilder WithHost(string host) { _host = host; return this; }

    public HostKeyBuilder WithPort(int port) { _port = port; return this; }

    public HostKeyBuilder WithAlgorithm(string algorithm) { _algorithm = algorithm; return this; }

    public HostKeyBuilder WithGuidProvider(IGuidProvider guidProvider) { _guidProvider = guidProvider; return this; }

    public HostKey Build()
    {
        return HostKey.Create(
            _guidProvider,
            _host,
            _port,
            _algorithm,
            "AAAAC3NzaC1lZDI1NTE5AAAA",
            "SHA256:test-fingerprint",
            HostKeyTrust.Trusted,
            HostKeySource.UserAccepted,
            seenUtc: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)).Value;
    }
}
