using System.Globalization;
using RemoteFlow.Application.Abstractions.Sftp;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

#pragma warning disable IDE0046 // Result mapping is clearer as explicit guard clauses.

namespace RemoteFlow.Infrastructure.Ssh;

internal sealed class SshNetSftpService(
    Func<CancellationToken, SftpClient> clientFactory,
    TimeSpan operationTimeout) : ISftpService
{
    private readonly Func<CancellationToken, SftpClient> _clientFactory =
        clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly TimeSpan _operationTimeout = operationTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;
    private int _disposed;

    public async Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<RemoteFileInfo>();
            await foreach (var entry in client.ListDirectoryAsync(
                SftpPath.Normalize(path),
                cancellationToken).ConfigureAwait(false))
            {
                entries.Add(ToRemoteFileInfo(entry));
            }

            return SftpResult<IReadOnlyList<RemoteFileInfo>>.Success(entries);
        }
        catch (Exception exception)
        {
            return Failure<IReadOnlyList<RemoteFileInfo>>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<RemoteFileInfo?>> StatAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var normalized = SftpPath.Normalize(path);
            if (!client.Exists(normalized))
            {
                return SftpResult<RemoteFileInfo?>.Success(null);
            }

            var attributes = await client.GetAttributesAsync(normalized, cancellationToken).ConfigureAwait(false);
            return SftpResult<RemoteFileInfo?>.Success(ToRemoteFileInfo(normalized, attributes));
        }
        catch (Exception exception)
        {
            return Failure<RemoteFileInfo?>(exception, cancellationToken);
        }
    }

    public Task<SftpResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async client =>
            await client.CreateDirectoryAsync(SftpPath.Normalize(path), cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    public Task<SftpResult> RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async client =>
            await client.RenameFileAsync(
                SftpPath.Normalize(sourcePath),
                SftpPath.Normalize(destinationPath),
                cancellationToken).ConfigureAwait(false),
            cancellationToken);
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
            return SftpResult.Fail(SftpError.NotFound, $"The remote path '{path}' was not found.");
        }

        return await ExecuteAsync(async client =>
        {
            var normalized = SftpPath.Normalize(path);
            if (stat.Value.IsDirectory && !stat.Value.IsSymlink)
            {
                if (recursive)
                {
                    await DeleteDirectoryRecursiveAsync(client, normalized, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await client.DeleteDirectoryAsync(normalized, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await client.DeleteFileAsync(normalized, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<SftpResult> SetPermissionsAsync(
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(client => Task.Run(() =>
            client.ChangePermissions(SftpPath.Normalize(path), checked((short)(int)mode)),
            cancellationToken), cancellationToken);
    }

    public async Task<SftpResult<string>> GetRealPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var normalized = SftpPath.Normalize(path);
            if (normalized == "~" || normalized.StartsWith("~/", StringComparison.Ordinal))
            {
                normalized = client.WorkingDirectory.TrimEnd('/') + normalized[1..];
            }
            else if (normalized[0] != '/')
            {
                normalized = SftpPath.Combine(client.WorkingDirectory, normalized);
            }

            return SftpResult<string>.Success(SftpPath.Normalize(normalized));
        }
        catch (Exception exception)
        {
            return Failure<string>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return SftpResult<Stream>.Success(client.OpenRead(SftpPath.Normalize(path)));
        }
        catch (Exception exception)
        {
            return Failure<Stream>(exception, cancellationToken);
        }
    }

    public async Task<SftpResult<Stream>> OpenWriteAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return SftpResult<Stream>.Success(client.OpenWrite(SftpPath.Normalize(path)));
        }
        catch (Exception exception)
        {
            return Failure<Stream>(exception, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client?.Dispose();
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<SftpResult> ExecuteAsync(
        Func<SftpClient, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            await operation(client).ConfigureAwait(false);
            return SftpResult.Success();
        }
        catch (Exception exception)
        {
            return Failure(exception, cancellationToken);
        }
    }

    private async Task<SftpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_client is { IsConnected: true } connected)
        {
            return connected;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true } existing)
            {
                return existing;
            }

            _client?.Dispose();
            var client = _clientFactory(cancellationToken);
            client.OperationTimeout = _operationTimeout;
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
            return client;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private static async Task DeleteDirectoryRecursiveAsync(
        SftpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        await foreach (var entry in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (entry.Name is "." or "..")
            {
                continue;
            }

            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                await DeleteDirectoryRecursiveAsync(client, entry.FullName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.DeleteFileAsync(entry.FullName, cancellationToken).ConfigureAwait(false);
            }
        }

        await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static SftpResult Failure(Exception exception, CancellationToken cancellationToken)
    {
        var failure = MapFailure(exception, cancellationToken);
        return SftpResult.Fail(failure.Error, failure.Message);
    }

    private static SftpResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        var failure = MapFailure(exception, cancellationToken);
        return SftpResult<T>.Fail(failure.Error, failure.Message);
    }

    private static SftpFailure MapFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return new(SftpError.Cancelled, "The SFTP operation was cancelled.");
        }

        if (exception is SftpPathNotFoundException)
        {
            return new(SftpError.NotFound, "The remote path was not found.");
        }

        if (exception is SftpPermissionDeniedException)
        {
            return new(SftpError.PermissionDenied, "Permission was denied by the remote server.");
        }

        var message = exception.Message;
        if (message.Contains("not a directory", StringComparison.OrdinalIgnoreCase))
        {
            return new(SftpError.NotDirectory, "The remote path is not a directory.");
        }

        if (message.Contains("exist", StringComparison.OrdinalIgnoreCase))
        {
            return new(SftpError.AlreadyExists, "The remote path already exists.");
        }

        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no space", StringComparison.OrdinalIgnoreCase))
        {
            return new(SftpError.QuotaExceeded, "The remote filesystem has no available quota or space.");
        }

        return new(SftpError.Unknown, string.IsNullOrWhiteSpace(message) ? "The SFTP operation failed." : message);
    }

    private static RemoteFileInfo ToRemoteFileInfo(ISftpFile entry)
    {
        return new RemoteFileInfo(
            entry.Name,
            entry.FullName,
            entry.Length,
            new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero),
            ToMode(entry.Attributes),
            entry.UserId.ToString(CultureInfo.InvariantCulture),
            entry.GroupId.ToString(CultureInfo.InvariantCulture),
            entry.IsDirectory,
            entry.IsSymbolicLink,
            null);
    }

    private static RemoteFileInfo ToRemoteFileInfo(string path, SftpFileAttributes attributes)
    {
        return new RemoteFileInfo(
            SftpPath.GetName(path),
            path,
            attributes.Size,
            new DateTimeOffset(attributes.LastWriteTimeUtc, TimeSpan.Zero),
            ToMode(attributes),
            attributes.UserId.ToString(CultureInfo.InvariantCulture),
            attributes.GroupId.ToString(CultureInfo.InvariantCulture),
            attributes.IsDirectory,
            attributes.IsSymbolicLink,
            null);
    }

    private static UnixFileMode ToMode(SftpFileAttributes attributes)
    {
        var mode = (UnixFileMode)0;
        if (attributes.OwnerCanRead) { mode |= UnixFileMode.UserRead; }
        if (attributes.OwnerCanWrite) { mode |= UnixFileMode.UserWrite; }
        if (attributes.OwnerCanExecute) { mode |= UnixFileMode.UserExecute; }
        if (attributes.GroupCanRead) { mode |= UnixFileMode.GroupRead; }
        if (attributes.GroupCanWrite) { mode |= UnixFileMode.GroupWrite; }
        if (attributes.GroupCanExecute) { mode |= UnixFileMode.GroupExecute; }
        if (attributes.OthersCanRead) { mode |= UnixFileMode.OtherRead; }
        if (attributes.OthersCanWrite) { mode |= UnixFileMode.OtherWrite; }
        if (attributes.OthersCanExecute) { mode |= UnixFileMode.OtherExecute; }
        if (attributes.IsUIDBitSet) { mode |= UnixFileMode.SetUser; }
        if (attributes.IsGroupIDBitSet) { mode |= UnixFileMode.SetGroup; }
        if (attributes.IsStickyBitSet) { mode |= UnixFileMode.StickyBit; }
        return mode;
    }
}

#pragma warning restore IDE0046
