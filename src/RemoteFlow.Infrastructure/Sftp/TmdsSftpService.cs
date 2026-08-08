using System.Globalization;
using RemoteFlow.Application.Abstractions.Sftp;
using Tmds.Ssh;
using AppSftpError = RemoteFlow.Application.Abstractions.Sftp.SftpError;

#pragma warning disable IDE0046 // Result mapping is clearer as explicit guard clauses.
#pragma warning disable IDE0072 // Unknown protocol status values intentionally use the fallback arm.

namespace RemoteFlow.Infrastructure.Sftp;

internal sealed class TmdsSftpService : ISftpService
{
    private readonly SftpClient _client;
    private int _disposed;

    public TmdsSftpService(SshClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = new SftpClient(client, new SftpClientOptions());
    }

    public async Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            var normalized = SftpPath.Normalize(path);
            var entries = new List<RemoteFileInfo>();
            await foreach (var entry in _client.GetDirectoryEntriesAsync(
                normalized,
                Materialize,
                new Tmds.Ssh.EnumerationOptions
                {
                    FollowFileLinks = false,
                    FollowDirectoryLinks = false,
                }).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var target = entry.FileType == UnixFileType.SymbolicLink
                    ? await _client.GetLinkTargetAsync(entry.Path, cancellationToken).ConfigureAwait(false)
                    : null;
                entries.Add(ToRemoteFileInfo(entry, target));
            }

            return SftpResult<IReadOnlyList<RemoteFileInfo>>.Success(entries);
        }
        catch (Exception exception)
        {
            return SftpFailureMapper.Failure<IReadOnlyList<RemoteFileInfo>>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<RemoteFileInfo?>> StatAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            var normalized = SftpPath.Normalize(path);
            var attributes = await _client.GetAttributesAsync(
                normalized,
                followLinks: false,
                filter: [],
                cancellationToken).ConfigureAwait(false);
            if (attributes is null)
            {
                return SftpResult<RemoteFileInfo?>.Success(null);
            }

            var target = attributes.FileType == UnixFileType.SymbolicLink
                ? await _client.GetLinkTargetAsync(normalized, cancellationToken).ConfigureAwait(false)
                : null;
            return SftpResult<RemoteFileInfo?>.Success(ToRemoteFileInfo(normalized, attributes, target));
        }
        catch (SftpException exception) when (exception.Error == Tmds.Ssh.SftpError.NoSuchFile)
        {
            return SftpResult<RemoteFileInfo?>.Success(null);
        }
        catch (Exception exception)
        {
            var failure = SftpFailureMapper.Failure<RemoteFileInfo?>(exception, cancellationToken);
            return failure.Failure.Error == AppSftpError.NotFound
                ? SftpResult<RemoteFileInfo?>.Success(null)
                : failure;
        }
    }

    public Task<SftpResult> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async () =>
        {
            await _client.CreateNewDirectoryAsync(
                SftpPath.Normalize(path),
                createParents: false,
                SftpClient.DefaultCreateDirectoryPermissions,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<SftpResult> RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async () =>
        {
            await _client.RenameAsync(
                SftpPath.Normalize(sourcePath),
                SftpPath.Normalize(destinationPath),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<SftpResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        var stat = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return SftpResult.Fail(stat.Failure.Error, stat.Failure.Message);
        }

        if (stat.Value is null)
        {
            return SftpResult.Fail(AppSftpError.NotFound, $"The remote path '{path}' was not found.");
        }

        return await ExecuteAsync(async () =>
        {
            var normalized = SftpPath.Normalize(path);
            if (stat.Value.IsDirectory && !stat.Value.IsSymlink)
            {
                await _client.DeleteDirectoryAsync(
                    normalized,
                    recursive,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.DeleteFileAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<SftpResult> SetPermissionsAsync(
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async () =>
        {
            await _client.SetAttributesAsync(
                SftpPath.Normalize(path),
                mode.ToUnixFilePermissions(),
                times: null,
                length: null,
                ids: null,
                extendedAttributes: null,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public async Task<SftpResult<string>> GetRealPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            var normalized = SftpPath.Normalize(path);
            var serverPath = normalized == "~"
                ? "."
                : normalized.StartsWith("~/", StringComparison.Ordinal) ? "./" + normalized[2..] : normalized;
            var realPath = await _client.GetRealPathAsync(
                serverPath,
                cancellationToken).ConfigureAwait(false);
            return SftpResult<string>.Success(realPath);
        }
        catch (Exception exception)
        {
            return SftpFailureMapper.Failure<string>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            var stream = await _client.OpenFileAsync(
                SftpPath.Normalize(path),
                FileAccess.Read,
                options: null,
                cancellationToken).ConfigureAwait(false);
            return stream is null
                ? SftpResult<Stream>.Fail(AppSftpError.NotFound, $"The remote file '{path}' was not found.")
                : SftpResult<Stream>.Success(stream);
        }
        catch (Exception exception)
        {
            return SftpFailureMapper.Failure<Stream>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<Stream>> OpenWriteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            var stream = await _client.OpenOrCreateFileAsync(
                SftpPath.Normalize(path),
                FileAccess.Write,
                new FileOpenOptions { OpenMode = OpenMode.Truncate },
                cancellationToken).ConfigureAwait(false);
            return SftpResult<Stream>.Success(stream);
        }
        catch (Exception exception)
        {
            return SftpFailureMapper.Failure<Stream>(exception, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SftpResult> ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfDisposed();
            await operation().ConfigureAwait(false);
            return SftpResult.Success();
        }
        catch (Exception exception)
        {
            return SftpFailureMapper.Failure(exception, cancellationToken);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static RemoteFileInfo ToRemoteFileInfo(MaterializedEntry entry, string? target)
    {
        return new RemoteFileInfo(
            entry.FileName,
            entry.Path,
            entry.Length,
            entry.LastWriteTime,
            entry.Permissions.ToUnixFileMode(),
            entry.Uid.ToString(CultureInfo.InvariantCulture),
            entry.Gid.ToString(CultureInfo.InvariantCulture),
            entry.FileType == UnixFileType.Directory,
            entry.FileType == UnixFileType.SymbolicLink,
            target);
    }

    private sealed record MaterializedEntry(
        string FileName,
        string Path,
        long Length,
        DateTimeOffset LastWriteTime,
        UnixFilePermissions Permissions,
        int Uid,
        int Gid,
        UnixFileType FileType);

    private static MaterializedEntry Materialize(ref SftpFileEntry entry)
    {
        return new MaterializedEntry(
            entry.FileName.ToString(),
            entry.Path.ToString(),
            entry.Length,
            entry.LastWriteTime,
            entry.Permissions,
            entry.Uid,
            entry.Gid,
            entry.FileType);
    }

    private static RemoteFileInfo ToRemoteFileInfo(
        string path,
        FileEntryAttributes attributes,
        string? target)
    {
        return new RemoteFileInfo(
            SftpPath.GetName(path),
            path,
            attributes.Length,
            attributes.LastWriteTime,
            attributes.Permissions.ToUnixFileMode(),
            attributes.Uid.ToString(CultureInfo.InvariantCulture),
            attributes.Gid.ToString(CultureInfo.InvariantCulture),
            attributes.FileType == UnixFileType.Directory,
            attributes.FileType == UnixFileType.SymbolicLink,
            target);
    }
}

internal static class SftpFailureMapper
{
    public static SftpResult Failure(Exception exception, CancellationToken cancellationToken)
    {
        var failure = Map(exception, cancellationToken);
        return SftpResult.Fail(failure.Error, failure.Message);
    }

    public static SftpResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        var failure = Map(exception, cancellationToken);
        return SftpResult<T>.Fail(failure.Error, failure.Message);
    }

    private static SftpFailure Map(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return new(AppSftpError.Cancelled, "The SFTP operation was cancelled.");
        }

        if (exception is SftpException sftpException)
        {
            return sftpException.Error switch
            {
                Tmds.Ssh.SftpError.NoSuchFile => new(AppSftpError.NotFound, "The remote path was not found."),
                Tmds.Ssh.SftpError.PermissionDenied => new(AppSftpError.PermissionDenied, "Permission was denied by the remote server."),
                Tmds.Ssh.SftpError.Unsupported => new(AppSftpError.NotSupported, "The remote server does not support this operation."),
                Tmds.Ssh.SftpError.BadMessage => new(AppSftpError.InvalidPath, "The remote path is invalid or too long."),
                _ => FromMessage(sftpException.Message),
            };
        }

        return exception is ObjectDisposedException
            ? new(AppSftpError.ConnectionLost, "The SFTP connection is closed.")
            : FromMessage(exception.Message);
    }

    private static SftpFailure FromMessage(string message)
    {
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no such file", StringComparison.OrdinalIgnoreCase))
        {
            return new(AppSftpError.NotFound, "The remote path was not found.");
        }

        if (message.Contains("not a directory", StringComparison.OrdinalIgnoreCase))
        {
            return new(AppSftpError.NotDirectory, "The remote path is not a directory.");
        }

        if (message.Contains("exist", StringComparison.OrdinalIgnoreCase))
        {
            return new(AppSftpError.AlreadyExists, "The remote path already exists.");
        }

        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no space", StringComparison.OrdinalIgnoreCase))
        {
            return new(AppSftpError.QuotaExceeded, "The remote filesystem has no available quota or space.");
        }

        return new(AppSftpError.Unknown, string.IsNullOrWhiteSpace(message) ? "The SFTP operation failed." : message);
    }
}

#pragma warning restore IDE0072
#pragma warning restore IDE0046
