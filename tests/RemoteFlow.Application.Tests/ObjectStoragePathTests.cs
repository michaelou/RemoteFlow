using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class ObjectStoragePathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/bucket", "/bucket")]
    [InlineData("bucket/logs", "/bucket/logs")]
    [InlineData("/bucket//logs///2026", "/bucket/logs/2026")]
    [InlineData("\\bucket\\logs", "/bucket/logs")]
    [InlineData("/bucket/./logs", "/bucket/logs")]
    [InlineData("/bucket/logs/../2026", "/bucket/2026")]
    [InlineData("/../..", "/")]
    public void NormalizeRootsAndCollapses(string input, string expected)
    {
        Assert.Equal(expected, ObjectStoragePath.Normalize(input.Length == 0 ? "/" : input));
    }

    [Theory]
    // The whole reason this type is not SftpPath: SftpPath.Normalize strips the trailing slash, and here
    // "a/" is a prefix marker while "a" is an object.
    [InlineData("/bucket/logs/", "/bucket/logs/")]
    [InlineData("/bucket/logs", "/bucket/logs")]
    public void NormalizeKeepsALoadBearingTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, ObjectStoragePath.Normalize(input));
    }

    [Fact]
    public void SftpPathWouldHaveStrippedThatTrailingSlash()
    {
        Assert.Equal("/bucket/logs", SftpPath.Normalize("/bucket/logs/"));
        Assert.Equal("/bucket/logs/", ObjectStoragePath.Normalize("/bucket/logs/"));
    }

    [Theory]
    [InlineData("/", null, "")]
    [InlineData("/bucket", "bucket", "")]
    [InlineData("/bucket/logs", "bucket", "logs")]
    [InlineData("/bucket/logs/2026/app.log", "bucket", "logs/2026/app.log")]
    [InlineData("/bucket/logs/", "bucket", "logs/")]
    public void SplitSeparatesTheContainerFromTheKey(string path, string? container, string key)
    {
        Assert.Equal((container, key), ObjectStoragePath.Split(path));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/bucket", "bucket")]
    [InlineData("/bucket/logs/2026", "2026")]
    [InlineData("/bucket/logs/2026/", "2026")]
    public void GetNameIgnoresATrailingSlash(string path, string expected)
    {
        Assert.Equal(expected, ObjectStoragePath.GetName(path));
    }

    [Theory]
    [InlineData("/", null)]
    [InlineData("/bucket", "/")]
    [InlineData("/bucket/logs", "/bucket")]
    [InlineData("/bucket/logs/2026/", "/bucket/logs")]
    public void GetParentStopsAtTheAccountRoot(string path, string? expected)
    {
        Assert.Equal(expected, ObjectStoragePath.GetParent(path));
    }

    [Fact]
    public void CombineAndAsPrefixBehaveOnTheEdges()
    {
        Assert.Equal("/bucket/logs", ObjectStoragePath.Combine("/bucket", "logs"));
        Assert.Equal("/bucket/logs", ObjectStoragePath.Combine("/bucket/", "logs"));
        Assert.Equal("logs/", ObjectStoragePath.AsPrefix("logs"));
        Assert.Equal("logs/", ObjectStoragePath.AsPrefix("logs/"));
        Assert.Equal(string.Empty, ObjectStoragePath.AsPrefix(string.Empty));
        Assert.True(ObjectStoragePath.IsRoot("/"));
        Assert.False(ObjectStoragePath.IsRoot("/bucket"));
    }
}

public sealed class ObjectStorageEndpointTests
{
    [Theory]
    [InlineData(ProtocolType.S3, "eu-west-2", "AKIAEXAMPLE", null, "s3.eu-west-2.amazonaws.com")]
    [InlineData(ProtocolType.S3, null, "AKIAEXAMPLE", null, "s3.amazonaws.com")]
    [InlineData(ProtocolType.AzureBlob, null, "contoso", null, "contoso.blob.core.windows.net")]
    [InlineData(ProtocolType.AzureBlob, null, null, null, null)]
    // A custom endpoint wins outright: its authority is the host, port and all.
    [InlineData(ProtocolType.S3, "eu-west-2", "minio", "http://minio.example.test:9000", "minio.example.test:9000")]
    [InlineData(ProtocolType.Ssh, "eu-west-2", "operator", null, null)]
    public void DeriveHostAnswersFromTheFieldsTheEditorHas(
        ProtocolType protocol,
        string? region,
        string? identifier,
        string? serviceUrl,
        string? expected)
    {
        Assert.Equal(expected, ObjectStorageEndpoint.DeriveHost(protocol, region, identifier, serviceUrl));
    }

    [Fact]
    public void CreateRefusesANonStorageProtocol()
    {
        var connection = Build(ProtocolType.Sftp, "operator");

        var endpoint = ObjectStorageEndpoint.Create(connection);

        Assert.True(endpoint.IsFailure);
        Assert.Equal(SftpError.NotSupported, endpoint.Failure.Error);
    }

    [Fact]
    public void CreateRefusesAConnectionWithNoIdentifier()
    {
        var connection = Build(ProtocolType.AzureBlob, username: null);

        var endpoint = ObjectStorageEndpoint.Create(connection);

        Assert.True(endpoint.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, endpoint.Failure.Error);
        Assert.Contains("storage account name", endpoint.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootPathIsTheAccountUntilAContainerIsPinned()
    {
        Assert.Equal("/", ObjectStorageEndpoint.Create(Build(ProtocolType.S3, "AKIA")).Value.RootPath);
        Assert.Equal(
            "/archive",
            ObjectStorageEndpoint.Create(Build(ProtocolType.S3, "AKIA", container: "archive")).Value.RootPath);
    }

    [Fact]
    public void ResolveRefusesToLeaveAPinnedContainer()
    {
        var endpoint = ObjectStorageEndpoint.Create(Build(ProtocolType.S3, "AKIA", container: "archive")).Value;

        var resolved = endpoint.Resolve("/other/logs");

        Assert.True(resolved.IsFailure);
        Assert.Equal(SftpError.InvalidPath, resolved.Failure.Error);
        Assert.Contains("archive", resolved.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAndToPathHideTheRootPrefixBothWays()
    {
        var endpoint = ObjectStorageEndpoint
            .Create(Build(ProtocolType.S3, "AKIA", container: "archive", rootPrefix: "logs/2026")).Value;

        var resolved = endpoint.Resolve("/archive/app.log");

        Assert.True(resolved.IsSuccess);
        Assert.Equal(("archive", "logs/2026/app.log"), resolved.Value);
        Assert.Equal("/archive/app.log", endpoint.ToPath("archive", "logs/2026/app.log"));
        Assert.Equal("/archive", endpoint.ToPath("archive", "logs/2026"));
        Assert.Equal("/", endpoint.ToPath(null, string.Empty));
    }

    [Fact]
    public void ResolveFillsInThePinnedContainerForAnAccountRootedPath()
    {
        var endpoint = ObjectStorageEndpoint.Create(Build(ProtocolType.S3, "AKIA", container: "archive")).Value;

        Assert.Equal(("archive", string.Empty), endpoint.Resolve("/").Value);
        Assert.Equal(("archive", "logs"), endpoint.Resolve("/archive/logs").Value);
    }

    private static Connection Build(
        ProtocolType protocol,
        string? username,
        string? container = null,
        string? rootPrefix = null)
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Objects",
            "s3.eu-west-2.amazonaws.com",
            protocol).Value;
        _ = connection.SetDetails(
            username,
            AuthMethod.None,
            null,
            EnvironmentKind.Unspecified,
            null,
            SystemGuidProvider.Instance);
        var storage = ObjectStorageOptions.Default().Configure(
            region: "eu-west-2",
            container: container,
            rootPrefix: rootPrefix);
        return connection.SetOptions(
            SshOptions.Default(),
            SftpOptions.Default(),
            RdpOptions.Default(),
            storage.Value,
            SystemGuidProvider.Instance);
    }
}
