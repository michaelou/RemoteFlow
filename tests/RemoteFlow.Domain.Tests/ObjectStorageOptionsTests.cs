using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class ObjectStorageOptionsTests
{
    [Fact]
    public void DefaultLeavesEveryStringNullAndTheOneBoolFalse()
    {
        var options = ObjectStorageOptions.Default();

        Assert.Null(options.Region);
        Assert.Null(options.ServiceUrl);
        Assert.Null(options.Container);
        Assert.Null(options.RootPrefix);
        Assert.Null(options.LocalDownloadPath);
        Assert.False(options.UsePathStyleAddressing);
    }

    [Theory]
    // The loose rule, deliberately: Azure forbids dots and S3 allows them, so intersecting the two would
    // reject a name the user's own provider accepts.
    [InlineData("logs", true)]
    [InlineData("my.bucket.name", true)]
    [InlineData("my-bucket-1", true)]
    [InlineData("ab", false)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData(".dotted", false)]
    [InlineData("Upper", false)]
    [InlineData("under_score", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainerNameFollowsTheLooseRule(string? name, bool expected)
    {
        Assert.Equal(expected, ObjectStorageOptions.IsValidContainerName(name));
    }

    [Fact]
    public void ContainerNameAcceptsSixtyThreeCharactersAndRejectsSixtyFour()
    {
        Assert.True(ObjectStorageOptions.IsValidContainerName(new string('a', 63)));
        Assert.False(ObjectStorageOptions.IsValidContainerName(new string('a', 64)));
    }

    [Fact]
    public void ConfigureNormalizesAndKeepsEveryField()
    {
        var configured = ObjectStorageOptions.Default().Configure(
            region: "  eu-west-2 ",
            serviceUrl: " https://minio.example.test ",
            usePathStyleAddressing: true,
            container: " archive ",
            rootPrefix: "\\logs\\2026\\",
            localDownloadPath: " /tmp/objects ");

        Assert.True(configured.IsSuccess);
        Assert.Equal("eu-west-2", configured.Value.Region);
        Assert.Equal("https://minio.example.test", configured.Value.ServiceUrl);
        Assert.True(configured.Value.UsePathStyleAddressing);
        Assert.Equal("archive", configured.Value.Container);
        Assert.Equal("logs/2026", configured.Value.RootPrefix);
        Assert.Equal("/tmp/objects", configured.Value.LocalDownloadPath);
    }

    [Fact]
    public void ConfigureRejectsANonHttpEndpoint()
    {
        var configured = ObjectStorageOptions.Default().Configure(serviceUrl: "ftp://storage.example.test");

        Assert.True(configured.IsFailure);
        Assert.Equal("storage.service_url", configured.Error.Code);
    }

    [Fact]
    public void ConfigureRejectsAnInvalidContainerName()
    {
        var configured = ObjectStorageOptions.Default().Configure(container: "Not Valid");

        Assert.True(configured.IsFailure);
        Assert.Equal("storage.container", configured.Error.Code);
    }

    [Theory]
    [InlineData(ProtocolType.S3, 443)]
    [InlineData(ProtocolType.AzureBlob, 443)]
    public void ObjectStorageProtocolsDefaultToTheHttpsPort(ProtocolType protocol, int expected)
    {
        Assert.Equal(expected, protocol.GetDefaultPort());
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, false)]
    [InlineData(ProtocolType.Sftp, false)]
    [InlineData(ProtocolType.Rdp, false)]
    [InlineData(ProtocolType.S3, true)]
    [InlineData(ProtocolType.AzureBlob, true)]
    public void IsObjectStorageAnswersForEveryProtocol(ProtocolType protocol, bool expected)
    {
        Assert.Equal(expected, protocol.IsObjectStorage());
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, "SSH")]
    [InlineData(ProtocolType.Sftp, "SFTP")]
    [InlineData(ProtocolType.Rdp, "RDP")]
    [InlineData(ProtocolType.S3, "S3")]
    // The point of the whole helper: ToString().ToUpperInvariant() would say "AZUREBLOB".
    [InlineData(ProtocolType.AzureBlob, "Azure Blob")]
    public void DisplayNameIsReadableForEveryProtocol(ProtocolType protocol, string expected)
    {
        Assert.Equal(expected, protocol.GetDisplayName());
    }
}
