using System.IO.Compression;
using System.Text;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Backup;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class BackupArchiveFormatTests
{
    [Fact]
    public async Task V1ArchiveRoundTripsEveryPropertyAndStableId()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "round-trip.zip");
        var expected = CreateArchive();
        var serializer = new ZipBackupArchiveSerializer();

        await serializer.WriteAsync(path, expected, TestContext.Current.CancellationToken);
        var actual = await serializer.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Manifest, actual.Manifest);
        Assert.Equal(expected.Connections, actual.Connections);
        Assert.Equal(expected.Folders, actual.Folders);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.ConnectionTags, actual.ConnectionTags);
        Assert.Equal(expected.Settings, actual.Settings);
        Assert.Equal(expected.HostKeys, actual.HostKeys);
        Assert.Equal(expected.EncryptedCredentials, actual.EncryptedCredentials);
        Assert.Equal(expected.Connections[0].Id, actual.Connections[0].Id);
        Assert.Equal(expected.Folders[0].Id, actual.Folders[0].Id);
        Assert.Equal(expected.Tags[0].Id, actual.Tags[0].Id);
    }

    [Fact]
    public async Task StandardZipContainsAllPlaintextEntriesAndNoCredentialEntry()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "standard.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, CreateArchive(), TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

        Assert.All(BackupFormat.PlaintextEntries, entry => Assert.Contains(entry, names));
        Assert.DoesNotContain(BackupFormat.CredentialsEntry, names);
    }

    [Fact]
    public async Task UnknownManifestFieldIsIgnored()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "unknown-field.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, CreateArchive(), TestContext.Current.CancellationToken);
        RewriteManifest(path, element => element.Replace(
            "\"formatVersion\": 1,",
            "\"formatVersion\": 1, \"futureMinorField\": { \"enabled\": true },",
            StringComparison.Ordinal));

        var archive = await serializer.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(BackupFormat.CurrentVersion, archive.Manifest.FormatVersion);
    }

    [Fact]
    public async Task UnknownFormatVersionIsRefusedClearly()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "future.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, CreateArchive(), TestContext.Current.CancellationToken);
        RewriteManifest(path, element => element.Replace(
            "\"formatVersion\": 1",
            "\"formatVersion\": 999",
            StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() =>
            serializer.ReadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaintextEntriesNeverContainKnownSecretMaterial()
    {
        const string knownSecret = "known-password-that-must-not-leak";
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "no-secrets.zip");
        var serializer = new ZipBackupArchiveSerializer();
        var archive = CreateArchive();
        await serializer.WriteAsync(path, archive, TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith(".enc", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var plaintext = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(knownSecret, plaintext, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDomainEntityHasAnExplicitBackupDecision()
    {
        var entityTypes = typeof(Connection).Assembly.GetTypes()
            .Where(type => type.IsClass && type.IsPublic && type.Namespace == typeof(Connection).Namespace)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var coveredTypes = BackupFormat.DomainEntityCoverage.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(entityTypes, coveredTypes);
    }

    [Fact]
    public async Task CommittedGoldenV1ArchiveImportsCleanly()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "backup-v1-golden.zip");
        var serializer = new ZipBackupArchiveSerializer();

        var archive = await serializer.ReadAsync(fixture, TestContext.Current.CancellationToken);

        Assert.Equal(BackupFormat.CurrentVersion, archive.Manifest.FormatVersion);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), archive.Connections.Single().Id);
        Assert.Equal("Unicode 🚀", archive.Connections.Single().Notes);
    }

    [Fact]
    public async Task AV1ArchiveWithNoObjectStorageFieldReadsBackWithDefaults()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "backup-v1-golden.zip");
        var serializer = new ZipBackupArchiveSerializer();

        var archive = await serializer.ReadAsync(fixture, token);

        // The parameter is optional with a default, not required: that is what keeps this committed
        // archive importing without being regenerated.
        Assert.Null(archive.Connections.Single().ObjectStorage);
    }

    [Fact]
    public async Task TheCommittedGoldenArchiveWasNotRegenerated()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "backup-v1-golden.zip");

        using var archive = System.IO.Compression.ZipFile.OpenRead(fixture);
        var connections = archive.GetEntry(BackupFormat.ConnectionsEntry);
        Assert.NotNull(connections);
        using var reader = new StreamReader(connections.Open(), Encoding.UTF8);
        var json = await reader.ReadToEndAsync(token);

        // A regenerated archive would carry the new field. Its absence is the compatibility promise being
        // kept rather than re-asserted.
        Assert.DoesNotContain("objectStorage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage_", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStorageConnectionRoundTripsThroughTheArchive()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = new TemporaryArchiveDirectory();
        var path = Path.Combine(directory.Path, "storage.zip");
        var serializer = new ZipBackupArchiveSerializer();
        var source = CreateArchive();
        var connection = source.Connections[0] with
        {
            Protocol = ProtocolType.S3,
            ObjectStorage = new BackupObjectStorageOptions(
                "eu-west-2",
                "https://minio.example.test",
                true,
                "archive",
                "logs/2026",
                "C:/Objects"),
        };
        var archive = source with { Connections = [connection] };

        await serializer.WriteAsync(path, archive, token);
        var read = await serializer.ReadAsync(path, token);

        Assert.Equal(connection.ObjectStorage, read.Connections.Single().ObjectStorage);
        Assert.Equal(ProtocolType.S3, read.Connections.Single().Protocol);
    }

    /// <summary>Backup enums serialise as camelCase strings, not integers, so an older RemoteFlow reading
    /// an archive that names a protocol it does not know fails the whole import rather than that one
    /// connection. The ignore-unknown-fields rule covers unknown fields, not unknown enum members. This is
    /// accepted and documented, and the failure message is pinned here — the alternative, an
    /// <c>Unsupported</c> fallback member, would silently import a connection nobody can open.</summary>
    [Fact]
    public async Task AnArchiveNamingAnUnknownProtocolIsRefusedWithAMessageNamingTheProperty()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = new TemporaryArchiveDirectory();
        var path = Path.Combine(directory.Path, "future.zip");
        var serializer = new ZipBackupArchiveSerializer();
        await serializer.WriteAsync(path, CreateArchive(), token);
        RewriteProtocol(path, "gcs");

        var exception = await Assert.ThrowsAsync<BackupArchiveException>(() => serializer.ReadAsync(path, token));

        Assert.Equal("The backup contains invalid JSON near '$[0].protocol'.", exception.Message);
        _ = Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public void ProtocolNamesAreWrittenAsCamelCaseStrings()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        };

        Assert.Equal("\"s3\"", System.Text.Json.JsonSerializer.Serialize(ProtocolType.S3, options));
        Assert.Equal("\"azureBlob\"", System.Text.Json.JsonSerializer.Serialize(ProtocolType.AzureBlob, options));
    }

    private static void RewriteProtocol(string path, string protocol)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            path,
            System.IO.Compression.ZipArchiveMode.Update);
        var entry = archive.GetEntry(BackupFormat.ConnectionsEntry)!;
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
        {
            json = reader.ReadToEnd();
        }

        json = json.Replace("\"protocol\": \"ssh\"", $"\"protocol\": \"{protocol}\"", StringComparison.Ordinal);
        entry.Delete();
        var replacement = archive.CreateEntry(BackupFormat.ConnectionsEntry);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(json);
    }

    private sealed class TemporaryArchiveDirectory : IDisposable
    {
        public TemporaryArchiveDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RemoteFlow.BackupFormat",
                Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private static BackupArchive CreateArchive()
    {
        var connectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var folderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var tagId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var created = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var connections = new[]
        {
            new BackupConnection(
                connectionId,
                "Production shell",
                "example.test",
                22,
                ProtocolType.Ssh,
                "operator",
                AuthMethod.Password,
                "Unicode 🚀",
                folderId,
                true,
                EnvironmentKind.Production,
                "#112233",
                7,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                created,
                created.AddMinutes(1),
                new BackupCredentialReference(CredentialKind.Password, "credential-key", "test-store", created),
                new BackupSshOptions(30, "xterm-256color", "C:/keys/id_ed25519", "tmux", "/srv", HostKeyPolicy.Strict, true),
                new BackupSftpOptions("/srv", "C:/Downloads", true, true),
                new BackupRdpOptions("EXAMPLE", false, 1920, 1080, false, true, false)),
        };
        var folders = new[]
        {
            new BackupFolder(
                folderId,
                "Production",
                null,
                "/Production",
                0,
                2,
                true,
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                created,
                created),
        };
        var tags = new[] { new BackupTag(tagId, "Critical 🚨", "#FF0000", created) };
        var connectionTags = new[] { new BackupConnectionTag(connectionId, tagId) };
        var settings = new[] { new BackupSetting("terminal.fontSize", "14", created) };
        var hostKeys = new[]
        {
            new BackupHostKey(
                Guid.Parse("66666666-6666-6666-6666-666666666666"),
                "example.test",
                22,
                "ssh-ed25519",
                "AAAAC3NzaC1lZDI1NTE5AAAAIGoldenFixturePublicKey",
                "SHA256:golden-fixture-fingerprint",
                HostKeyTrust.Trusted,
                HostKeySource.Pinned,
                "Pinned by test",
                created,
                created.AddMinutes(2)),
        };
        var counts = new BackupEntityCounts(
            connections.Length,
            folders.Length,
            tags.Length,
            connectionTags.Length,
            settings.Length,
            hostKeys.Length);
        var manifest = new BackupManifest(1, "1.0.0-test", created, "fixture-machine", counts, false);
        return new BackupArchive(manifest, connections, folders, tags, connectionTags, settings, hostKeys);
    }

    private static void RewriteManifest(string path, Func<string, string> rewrite)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(BackupFormat.ManifestEntry) ?? throw new InvalidDataException("Missing manifest.");
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
        {
            json = reader.ReadToEnd();
        }

        entry.Delete();
        var replacement = zip.CreateEntry(BackupFormat.ManifestEntry);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
        writer.Write(rewrite(json));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RemoteFlow-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
