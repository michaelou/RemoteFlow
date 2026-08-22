using Amazon;
using NSubstitute;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Storage;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class ObjectStorageClientFactoryTests
{
    [Theory]
    [InlineData(ProtocolType.S3, typeof(S3ObjectStorageService))]
    [InlineData(ProtocolType.AzureBlob, typeof(AzureBlobObjectStorageService))]
    public async Task TheProviderIsSelectedByProtocol(ProtocolType protocol, Type expected)
    {
        var token = TestContext.Current.CancellationToken;
        var connection = Build(protocol, storeSecret: true);
        // Base64, because an Azure account key is; S3 does not care either way.
        var factory = CreateFactory(connection, Convert.ToBase64String(new byte[32]));

        var client = await factory.CreateAsync(connection.Id, token);

        Assert.True(client.IsSuccess);
        await using var service = client.Value;
        Assert.IsType(expected, service);
    }

    [Fact]
    public async Task AMissingConnectionIsNotFound()
    {
        var token = TestContext.Current.CancellationToken;
        var connections = Substitute.For<IConnectionRepository>();
        _ = connections.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Connection?>(null));
        var factory = new ObjectStorageClientFactory(
            connections,
            Substitute.For<IObjectStorageSecretProvider>(),
            [new S3ObjectStorageProvider()]);

        var client = await factory.CreateAsync(Guid.NewGuid(), token);

        Assert.True(client.IsFailure);
        Assert.Equal(SftpError.NotFound, client.Failure.Error);
    }

    [Fact]
    public async Task AnSshConnectionIsRefusedRatherThanGivenAStorageClient()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = Build(ProtocolType.Sftp, storeSecret: false);
        var factory = CreateFactory(connection, secret: null);

        var client = await factory.CreateAsync(connection.Id, token);

        Assert.True(client.IsFailure);
        Assert.Equal(SftpError.NotSupported, client.Failure.Error);
    }

    [Fact]
    public async Task WithNoStoredKeyTheFactoryRefusesRatherThanLettingTheSdkFindOne()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = Build(ProtocolType.S3, storeSecret: false);
        var factory = CreateFactory(connection, secret: null);

        var client = await factory.CreateAsync(connection.Id, token);

        // This is the line that keeps ~/.aws/credentials, AWS_* environment variables and the EC2/ECS
        // metadata endpoints out of the picture: with no stored key there is nothing to fall back to.
        Assert.True(client.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, client.Failure.Error);
        Assert.Contains("secret access key", client.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialOfTheWrongKindIsNotUsedAsAStorageKey()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = Build(ProtocolType.S3, storeSecret: false);
        var credential = CredentialRef.Create(
            CredentialKind.Password,
            CredentialStoreKeys.ForConnection(connection.Id, CredentialKind.Password),
            "test-store");
        _ = connection.SetCredential(credential.Value, SystemGuidProvider.Instance);
        var provider = new RecordingProvider("test-store", "an-ssh-password");
        var secrets = new ConnectionObjectStorageSecretProvider([provider]);

        var secret = await secrets.GetSecretKeyAsync(connection, token);

        Assert.Null(secret);
    }

    [Fact]
    public void TheS3ProviderRefusesAnEmptySecretAndSilencesTheSdksOwnLogging()
    {
        var endpoint = new ObjectStorageEndpoint(
            ProtocolType.S3,
            "s3.eu-west-2.amazonaws.com",
            443,
            "AKIAEXAMPLE",
            "eu-west-2",
            null,
            false,
            null,
            null);
        var provider = new S3ObjectStorageProvider();

        var refused = provider.Create(endpoint, ReadOnlyMemory<char>.Empty);
        var created = provider.Create(endpoint, "a-secret-key".AsMemory());

        Assert.True(refused.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, refused.Failure.Error);
        Assert.True(created.IsSuccess);
        // RedactingLoggerProvider cannot redact what it never sees, so the SDK's own response log is off.
        Assert.Equal(ResponseLoggingOption.Never, AWSConfigs.LoggingConfig.LogResponses);
        Assert.Equal(LoggingOptions.None, AWSConfigs.LoggingConfig.LogTo);
        Assert.False(AWSConfigs.LoggingConfig.LogMetrics);
    }

    [Fact]
    public void TheAzureProviderRejectsAKeyThatIsNotBase64WithAnActionableMessage()
    {
        var endpoint = new ObjectStorageEndpoint(
            ProtocolType.AzureBlob,
            "contoso.blob.core.windows.net",
            443,
            "contoso",
            null,
            null,
            false,
            null,
            null);

        var refused = new AzureBlobObjectStorageProvider().Create(endpoint, "not base64!".AsMemory());

        Assert.True(refused.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, refused.Failure.Error);
        Assert.Contains("Access keys", refused.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAzureProviderRefusesAnEmptySecret()
    {
        var endpoint = new ObjectStorageEndpoint(
            ProtocolType.AzureBlob,
            "contoso.blob.core.windows.net",
            443,
            "contoso",
            null,
            null,
            false,
            null,
            null);

        var refused = new AzureBlobObjectStorageProvider().Create(endpoint, ReadOnlyMemory<char>.Empty);

        Assert.True(refused.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, refused.Failure.Error);
    }

    private static ObjectStorageClientFactory CreateFactory(Connection connection, string? secret)
    {
        var connections = Substitute.For<IConnectionRepository>();
        _ = connections.GetByIdAsync(connection.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Connection?>(connection));
        var secrets = Substitute.For<IObjectStorageSecretProvider>();
        _ = secrets.GetSecretKeyAsync(connection, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(secret is null ? null : new SecretHandle(secret.AsSpan())));
        return new ObjectStorageClientFactory(
            connections,
            secrets,
            [new S3ObjectStorageProvider(), new AzureBlobObjectStorageProvider()]);
    }

    private static Connection Build(ProtocolType protocol, bool storeSecret)
    {
        var host = protocol == ProtocolType.AzureBlob
            ? "contoso.blob.core.windows.net"
            : "s3.eu-west-2.amazonaws.com";
        var connection = Connection.Create(SystemGuidProvider.Instance, "Objects", host, protocol).Value;
        _ = connection.SetDetails(
            protocol == ProtocolType.AzureBlob ? "contoso" : "AKIAEXAMPLE",
            AuthMethod.None,
            null,
            EnvironmentKind.Unspecified,
            null,
            SystemGuidProvider.Instance);
        var storage = ObjectStorageOptions.Default().Configure(region: "eu-west-2");
        _ = connection.SetOptions(
            SshOptions.Default(),
            SftpOptions.Default(),
            RdpOptions.Default(),
            storage.Value,
            SystemGuidProvider.Instance);
        if (storeSecret)
        {
            var credential = CredentialRef.Create(
                CredentialKind.StorageSecretKey,
                CredentialStoreKeys.ForConnection(connection.Id, CredentialKind.StorageSecretKey),
                "test-store");
            _ = connection.SetCredential(credential.Value, SystemGuidProvider.Instance);
        }

        return connection;
    }

    private sealed class RecordingProvider(string name, string secret) : ICredentialProvider
    {
        public string Name { get; } = name;

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(new SecretHandle(secret.AsSpan()));
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
