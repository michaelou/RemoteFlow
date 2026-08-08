using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Backup;
using RemoteFlow.Infrastructure.Security.Crypto;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class CredentialEnvelopeTests
{
    [Fact]
    public async Task RoundTripRestoresUsableCredentialIntoTargetProvider()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "S3cret! \U0001F680".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(connections.Length);

        var encrypted = await envelope.EncryptAsync(connections, manifest, "Strong!Pass123".AsMemory(), token);
        await using var prepared = await envelope.PrepareImportAsync(
            encrypted, manifest, null, connections, "Strong!Pass123".AsMemory(), token);
        await prepared.StoreAsync(token);

        var reference = prepared.References[Ids.First];
        Assert.Equal("target", reference.StoreProvider);
        using var restored = await target.GetAsync(reference.StoreKey, token);
        Assert.NotNull(restored);
        Assert.Equal("S3cret! \U0001F680", restored.Secret.ToString());
    }

    [Fact]
    public async Task WrongPassphraseUsesGenericFailureWithoutRecordDetails()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "secret".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(1);
        var encrypted = await envelope.EncryptAsync(connections, manifest, "Right!Pass123".AsMemory(), token);

        var exception = await Assert.ThrowsAsync<BackupCredentialException>(() =>
            envelope.PrepareImportAsync(encrypted, manifest, null, connections, "Wrong!Pass123".AsMemory(), token));

        Assert.DoesNotContain("record", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("count", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("valid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CiphertextAndManifestTamperingBothFailAuthentication()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "secret".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(1);
        var encrypted = await envelope.EncryptAsync(connections, manifest, "Strong!Pass123".AsMemory(), token);
        var tamperedCiphertext = MutateRecordField(encrypted, 0, "ciphertext");

        _ = await Assert.ThrowsAsync<BackupCredentialException>(() =>
            envelope.PrepareImportAsync(tamperedCiphertext, manifest, null, connections, "Strong!Pass123".AsMemory(), token));

        var wrongManifestHash = SHA256.HashData("tampered manifest"u8);
        _ = await Assert.ThrowsAsync<BackupCredentialException>(() =>
            envelope.PrepareImportAsync(encrypted, manifest, wrongManifestHash, connections, "Strong!Pass123".AsMemory(), token));
    }

    [Fact]
    public async Task SwappingEncryptedRecordsBetweenConnectionIdsFails()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("first-key", "first-secret".AsMemory(), "First", token);
        await source.SetAsync("second-key", "second-secret".AsMemory(), "Second", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "first-key"), Connection(Ids.Second, "second-key") };
        var manifest = Manifest(2);
        var encrypted = await envelope.EncryptAsync(connections, manifest, "Strong!Pass123".AsMemory(), token);
        var document = JsonNode.Parse(encrypted)!.AsObject();
        var records = document["records"]!.AsArray();
        foreach (var field in new[] { "nonce", "ciphertext", "tag" })
        {
            var first = records[0]![field]!.GetValue<string>();
            records[0]![field] = records[1]![field]!.GetValue<string>();
            records[1]![field] = first;
        }
        var swapped = JsonSerializer.SerializeToUtf8Bytes(document);

        _ = await Assert.ThrowsAsync<BackupCredentialException>(() =>
            envelope.PrepareImportAsync(swapped, manifest, null, connections, "Strong!Pass123".AsMemory(), token));
    }

    [Fact]
    public async Task ManifestKdfParametersOverrideDefaultsAndKnownAnswerIsStable()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "known-secret".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(1, memory: 8, iterations: 1, parallelism: 1);

        var encrypted = await envelope.EncryptAsync(connections, manifest, "Known!Pass123".AsMemory(), token);
        var digest = Convert.ToHexString(SHA256.HashData(encrypted));
        await using var prepared = await envelope.PrepareImportAsync(
            encrypted, manifest, null, connections, "Known!Pass123".AsMemory(), token);

        // The envelope pins "\n" as its line ending, so this digest is the same on every host. The
        // previous value was the CRLF form Windows produced before that was pinned.
        Assert.Equal("33E5393B12A5340DD5F49E99C8A180452F6B0B834F6759E7341359A56512463E", digest);
        Assert.Contains(Ids.First, prepared.References.Keys);
    }

    [Fact]
    public async Task StandardZipManifestHashAuthenticatesCredentialEnvelope()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "zip-secret".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(1);
        var encrypted = await envelope.EncryptAsync(connections, manifest, "Strong!Pass123".AsMemory(), token);
        var archive = new BackupArchive(manifest, connections, [], [], [], [], [], encrypted);
        var directory = Path.Combine(Path.GetTempPath(), $"RemoteFlow-Credentials-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.zip");
        try
        {
            var serializer = new ZipBackupArchiveSerializer();
            await serializer.WriteAsync(path, archive, token);
            var read = await serializer.ReadAsync(path, token);

            await using var prepared = await envelope.PrepareImportAsync(
                read.EncryptedCredentials!,
                read.Manifest,
                read.ManifestHash,
                read.Connections,
                "Strong!Pass123".AsMemory(),
                token);

            Assert.Contains(Ids.First, prepared.References.Keys);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task IndentedBackupJsonUsesLineFeedsOnEveryHost()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new MemoryCredentialProvider("source");
        var target = new MemoryCredentialProvider("target");
        await source.SetAsync("source-key", "newline-secret".AsMemory(), "Source", token);
        var envelope = CreateEnvelope(source, target);
        var connections = new[] { Connection(Ids.First, "source-key") };
        var manifest = Manifest(1);
        var encrypted = await envelope.EncryptAsync(connections, manifest, "Strong!Pass123".AsMemory(), token);

        Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);

        var archive = new BackupArchive(manifest, connections, [], [], [], [], [], encrypted);
        var directory = Path.Combine(Path.GetTempPath(), $"RemoteFlow-Credentials-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.zip");
        try
        {
            await new ZipBackupArchiveSerializer().WriteAsync(path, archive, token);
            using var zip = ZipFile.OpenRead(path);
            var entry = Assert.Single(zip.Entries, item => item.FullName == BackupFormat.ManifestEntry);
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);

            Assert.DoesNotContain("\r\n", await reader.ReadToEndAsync(token), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CredentialEnvelope CreateEnvelope(
        MemoryCredentialProvider source,
        MemoryCredentialProvider target)
    {
        return new CredentialEnvelope(
            new Argon2idPassphraseKdf(),
            new AesGcmAuthenticatedCipher(),
            new SequenceRandom(),
            new StaticSelector(target),
            [source, target]);
    }

    private static BackupManifest Manifest(int connectionCount, int memory = 8, int iterations = 1, int parallelism = 1)
    {
        return new BackupManifest(
            1,
            "test",
            DateTimeOffset.UnixEpoch,
            null,
            new BackupEntityCounts(connectionCount, 0, 0, 0, 0, 0),
            true,
            new BackupCredentialKdf(
                "argon2id",
                memory,
                iterations,
                parallelism,
                Convert.ToBase64String(Enumerable.Range(1, 16).Select(value => (byte)value).ToArray())));
    }

    private static BackupConnection Connection(Guid id, string storeKey)
    {
        return new BackupConnection(
            id, $"Connection {id.ToString("N")[..4]}", "example.test", 22, ProtocolType.Ssh, "admin",
            AuthMethod.Password, null, null, false, EnvironmentKind.Unspecified, null, null, Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            new BackupCredentialReference(CredentialKind.Password, storeKey, "source", DateTimeOffset.UnixEpoch),
            new BackupSshOptions(null, "xterm-256color", null, null, null, HostKeyPolicy.Strict, true),
            new BackupSftpOptions(null, null, false, false),
            new BackupRdpOptions(null, false, null, null, false, true, false));
    }

    private static byte[] MutateRecordField(byte[] encrypted, int recordIndex, string field)
    {
        var document = JsonNode.Parse(encrypted)!.AsObject();
        var value = document["records"]![recordIndex]![field]!.GetValue<string>();
        var replacement = value[0] == 'A' ? $"B{value[1..]}" : $"A{value[1..]}";
        document["records"]![recordIndex]![field] = replacement;
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }

    private static class Ids
    {
        public static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    }

    private sealed class SequenceRandom : ISecureRandom
    {
        private byte _next = 1;

        public byte[] GetBytes(int count)
        {
            var result = new byte[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = _next++;
            }

            return result;
        }
    }

    private sealed class StaticSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(provider);
        }
    }

    private sealed class MemoryCredentialProvider(string name) : ICredentialProvider
    {
        private readonly Dictionary<string, char[]> _values = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public bool IsAvailable => true;

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(storeKey, out var value) ? new SecretHandle(value) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[storeKey] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            if (_values.Remove(storeKey, out var value))
            {
                CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()));
            }

            return Task.CompletedTask;
        }
    }
}
