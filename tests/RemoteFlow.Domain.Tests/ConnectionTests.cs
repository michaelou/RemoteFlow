using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class ConnectionTests
{
    private static readonly DateTimeOffset _fixedUtc = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsMissingName(string? name)
    {
        var result = Connection.Create(GuidProvider(), name, "example.com", 22);

        Assert.True(result.IsFailure);
        Assert.Equal("connection.name", result.Error.Code);
    }

    [Fact]
    public void CreateRejectsNameLongerThanOneHundredCharacters()
    {
        var result = Connection.Create(GuidProvider(), new string('a', 101), "example.com", 22);

        Assert.True(result.IsFailure);
        Assert.Equal("connection.name", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsMissingHost(string? host)
    {
        var result = Connection.Create(GuidProvider(), "Server", host, 22);

        Assert.True(result.IsFailure);
        Assert.Equal("connection.host", result.Error.Code);
    }

    [Fact]
    public void CreateRejectsHostLongerThanTwoHundredFiftyFiveCharacters()
    {
        var result = Connection.Create(GuidProvider(), "Server", new string('a', 256), 22);

        Assert.True(result.IsFailure);
        Assert.Equal("connection.host", result.Error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65_536)]
    [InlineData(int.MaxValue)]
    public void CreateRejectsPortOutsideTcpRange(int port)
    {
        var result = Connection.Create(GuidProvider(), "Server", "example.com", port);

        Assert.True(result.IsFailure);
        Assert.Equal("connection.port", result.Error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(65_535)]
    public void CreateAcceptsPortInsideTcpRange(int port)
    {
        var result = Connection.Create(GuidProvider(), "Server", "example.com", port);

        Assert.True(result.IsSuccess);
        Assert.Equal(port, result.Value.Port);
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, 22)]
    [InlineData(ProtocolType.Sftp, 22)]
    [InlineData(ProtocolType.Rdp, 3389)]
    public void CreateUsesProtocolDefaultPort(ProtocolType protocol, int expectedPort)
    {
        var result = Connection.Create(GuidProvider(), "Server", "example.com", protocol);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedPort, result.Value.Port);
    }

    [Fact]
    public void CreateNormalizesRequiredTextAndUtcTimestamp()
    {
        var offsetTime = _fixedUtc.ToOffset(TimeSpan.FromHours(3));

        var connection = Connection.Create(
            GuidProvider(),
            "  Production  ",
            "  db.example.com  ",
            22,
            ProtocolType.Ssh,
            offsetTime).Value;

        Assert.Equal("Production", connection.Name);
        Assert.Equal("db.example.com", connection.Host);
        Assert.Equal(_fixedUtc, connection.CreatedUtc);
        Assert.Equal(connection.CreatedUtc, connection.ModifiedUtc);
    }

    [Fact]
    public void CreateAlwaysMaterializesOwnedOptionGroups()
    {
        var connection = ValidConnection();

        Assert.NotNull(connection.Credential);
        Assert.NotNull(connection.Ssh);
        Assert.NotNull(connection.Sftp);
        Assert.NotNull(connection.Rdp);
        Assert.True(connection.Credential.IsEmpty);
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, true)]
    [InlineData(ProtocolType.Sftp, true)]
    [InlineData(ProtocolType.Rdp, false)]
    public void SupportsSftpDependsOnPrimaryProtocol(ProtocolType protocol, bool expected)
    {
        var connection = Connection.Create(GuidProvider(), "Server", "host", 22, protocol).Value;

        Assert.Equal(expected, connection.SupportsSftp);
    }

    [Fact]
    public void RenameRejectsBlankNameWithoutMutatingConnection()
    {
        var connection = ValidConnection();

        var result = connection.Rename(" ", GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("Server", connection.Name);
    }

    [Fact]
    public void RenameUpdatesConcurrencyStampAndModifiedTime()
    {
        var connection = ValidConnection();
        var oldStamp = connection.ConcurrencyStamp;
        var modified = _fixedUtc.AddMinutes(5);

        var result = connection.Rename("Renamed", GuidProvider(), modified);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", connection.Name);
        Assert.NotEqual(oldStamp, connection.ConcurrencyStamp);
        Assert.Equal(modified, connection.ModifiedUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void ChangeEndpointRejectsInvalidPortWithoutPartialMutation(int port)
    {
        var connection = ValidConnection();

        var result = connection.ChangeEndpoint("other", port, ProtocolType.Rdp, GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("host", connection.Host);
        Assert.Equal(22, connection.Port);
        Assert.Equal(ProtocolType.Ssh, connection.Protocol);
    }

    [Fact]
    public void SetDetailsRejectsNotesLongerThanFourThousandCharacters()
    {
        var connection = ValidConnection();

        var result = connection.SetDetails(
            "user",
            AuthMethod.Password,
            new string('a', 4_001),
            EnvironmentKind.Production,
            null,
            GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Null(connection.Username);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GG0000")]
    public void SetDetailsRejectsInvalidColor(string color)
    {
        var connection = ValidConnection();

        var result = connection.SetDetails(null, AuthMethod.None, null, EnvironmentKind.Unspecified, color, GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Null(connection.ColorOverrideHex);
    }

    [Theory]
    [InlineData("#aabbcc", "#AABBCC")]
    [InlineData("#11223344", "#11223344")]
    public void SetDetailsAcceptsAndNormalizesColor(string color, string expected)
    {
        var connection = ValidConnection();

        var result = connection.SetDetails(
            " alice ",
            AuthMethod.Password,
            " note ",
            EnvironmentKind.Production,
            color,
            GuidProvider());

        Assert.True(result.IsSuccess);
        Assert.Equal("alice", connection.Username);
        Assert.Equal("note", connection.Notes);
        Assert.Equal(expected, connection.ColorOverrideHex);
        Assert.Equal(EnvironmentKind.Production, connection.Environment);
    }

    [Fact]
    public void SetOptionsRejectsNullOwnedObjects()
    {
        var connection = ValidConnection();

        _ = Assert.Throws<ArgumentNullException>(() => connection.SetOptions(
            null!,
            SftpOptions.Default(),
            RdpOptions.Default(),
            GuidProvider()));
    }

    [Fact]
    public void AddTagCreatesExplicitJoinAndRejectsDuplicate()
    {
        var connection = ValidConnection();
        var tagId = Guid.CreateVersion7();

        var first = connection.AddTag(tagId);
        var duplicate = connection.AddTag(tagId);

        Assert.True(first.IsSuccess);
        Assert.Equal(connection.Id, first.Value.ConnectionId);
        Assert.Equal(tagId, first.Value.TagId);
        Assert.True(duplicate.IsFailure);
        _ = Assert.Single(connection.Tags);
    }

    [Fact]
    public void RemoveTagReturnsWhetherTagWasPresent()
    {
        var connection = ValidConnection();
        var tagId = Guid.CreateVersion7();
        _ = connection.AddTag(tagId);

        Assert.True(connection.RemoveTag(tagId));
        Assert.False(connection.RemoveTag(tagId));
        Assert.Empty(connection.Tags);
    }

    [Fact]
    public void SystemGuidProviderGeneratesVersionSevenKeys()
    {
        var value = SystemGuidProvider.Instance.NewGuid();

        Assert.Equal('7', value.ToString("D")[14]);
    }

    private static Connection ValidConnection()
    {
        return Connection.Create(GuidProvider(), "Server", "host", 22, createdUtc: _fixedUtc).Value;
    }

    private static SystemGuidProvider GuidProvider()
    {
        return SystemGuidProvider.Instance;
    }
}
