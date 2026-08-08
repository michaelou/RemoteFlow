using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteFlow.Application.Abstractions.Backup;

namespace RemoteFlow.Infrastructure.Backup;

public sealed class ZipBackupArchiveSerializer : IBackupArchiveSerializer
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task WriteAsync(
        string path,
        BackupArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(archive);
        ValidateArchive(archive);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new BackupArchiveException("The backup destination does not have a parent directory.");
        _ = Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonEntryAsync(zip, BackupFormat.ManifestEntry, archive.Manifest, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.ConnectionsEntry, archive.Connections, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.FoldersEntry, archive.Folders, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.TagsEntry, archive.Tags, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.ConnectionTagsEntry, archive.ConnectionTags, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.SettingsEntry, archive.Settings, cancellationToken);
                await WriteJsonEntryAsync(zip, BackupFormat.HostKeysEntry, archive.HostKeys, cancellationToken);
                if (archive.EncryptedCredentials is not null)
                {
                    var entry = zip.CreateEntry(BackupFormat.CredentialsEntry, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(archive.EncryptedCredentials, cancellationToken);
                }
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            throw new BackupArchiveException(
                $"The backup could not be written to '{fullPath}'. No partial archive was kept.",
                exception);
        }
    }

    public async Task<BackupArchive> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var manifest = await ReadRequiredJsonEntryAsync<BackupManifest>(
                zip,
                BackupFormat.ManifestEntry,
                cancellationToken);
            if (manifest.FormatVersion != BackupFormat.CurrentVersion)
            {
                throw new BackupArchiveException(
                    $"Backup format version {manifest.FormatVersion} is not supported. " +
                    $"This version of RemoteFlow supports format version {BackupFormat.CurrentVersion}.");
            }

            var connections = await ReadOptionalJsonEntryAsync<BackupConnection>(
                zip,
                BackupFormat.ConnectionsEntry,
                cancellationToken);
            var folders = await ReadOptionalJsonEntryAsync<BackupFolder>(
                zip,
                BackupFormat.FoldersEntry,
                cancellationToken);
            var tags = await ReadOptionalJsonEntryAsync<BackupTag>(zip, BackupFormat.TagsEntry, cancellationToken);
            var connectionTags = await ReadOptionalJsonEntryAsync<BackupConnectionTag>(
                zip,
                BackupFormat.ConnectionTagsEntry,
                cancellationToken);
            var settings = await ReadOptionalJsonEntryAsync<BackupSetting>(
                zip,
                BackupFormat.SettingsEntry,
                cancellationToken);
            var hostKeys = await ReadOptionalJsonEntryAsync<BackupHostKey>(
                zip,
                BackupFormat.HostKeysEntry,
                cancellationToken);
            var encryptedCredentials = await ReadOptionalBinaryEntryAsync(
                zip,
                BackupFormat.CredentialsEntry,
                cancellationToken);

            var archive = new BackupArchive(
                manifest,
                connections,
                folders,
                tags,
                connectionTags,
                settings,
                hostKeys,
                encryptedCredentials);
            ValidateArchive(archive);
            return archive;
        }
        catch (BackupArchiveException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new BackupArchiveException("The backup is not a valid or complete zip archive.", exception);
        }
        catch (JsonException exception)
        {
            throw new BackupArchiveException(
                $"The backup contains invalid JSON near '{exception.Path ?? "an unknown property"}'.",
                exception);
        }
        catch (IOException exception)
        {
            throw new BackupArchiveException($"The backup '{path}' could not be read.", exception);
        }
    }

    private static void ValidateArchive(BackupArchive archive)
    {
        if (archive.Manifest.FormatVersion != BackupFormat.CurrentVersion)
        {
            throw new BackupArchiveException(
                $"Backup format version {archive.Manifest.FormatVersion} is not supported. " +
                $"This version of RemoteFlow supports format version {BackupFormat.CurrentVersion}.");
        }

        if (archive.Manifest.Counts != archive.ActualCounts)
        {
            throw new BackupArchiveException("The backup manifest counts do not match the archive contents.");
        }

        if (archive.Manifest.IncludesCredentials != (archive.EncryptedCredentials is not null))
        {
            throw new BackupArchiveException(
                "The backup manifest credential flag does not match the credentials.enc entry.");
        }

        if (archive.Manifest.IncludesCredentials && archive.Manifest.CredentialKdf is null)
        {
            throw new BackupArchiveException("The backup manifest is missing credential KDF parameters.");
        }
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive zip,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, value, _jsonOptions, cancellationToken);
    }

    private static async Task<T> ReadRequiredJsonEntryAsync<T>(
        ZipArchive zip,
        string name,
        CancellationToken cancellationToken)
    {
        var entry = GetSingleEntry(zip, name)
            ?? throw new BackupArchiveException($"The backup is missing the required '{name}' entry.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
            ?? throw new BackupArchiveException($"The backup entry '{name}' is empty.");
    }

    private static async Task<IReadOnlyList<T>> ReadOptionalJsonEntryAsync<T>(
        ZipArchive zip,
        string name,
        CancellationToken cancellationToken)
    {
        var entry = GetSingleEntry(zip, name);
        if (entry is null)
        {
            return [];
        }

        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, _jsonOptions, cancellationToken)
            ?? throw new BackupArchiveException($"The backup entry '{name}' is empty.");
    }

    private static async Task<byte[]?> ReadOptionalBinaryEntryAsync(
        ZipArchive zip,
        string name,
        CancellationToken cancellationToken)
    {
        var entry = GetSingleEntry(zip, name);
        if (entry is null)
        {
            return null;
        }

        await using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static ZipArchiveEntry? GetSingleEntry(ZipArchive zip, string name)
    {
        var matches = zip.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new BackupArchiveException($"The backup contains duplicate '{name}' entries."),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
