using RemoteFlow.Application.Abstractions.Ssh;
using Renci.SshNet;

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

    public async Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<SftpEntry>();
        await foreach (var entry in client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SftpEntry(
                entry.Name,
                entry.FullName,
                entry.IsDirectory,
                entry.Length,
                entry.LastWriteTimeUtc));
        }

        return entries;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return client.OpenRead(path);
    }

    public async Task<Stream> OpenWriteAsync(
        string path,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return !overwrite && client.Exists(path)
            ? throw new IOException($"The remote file '{path}' already exists.")
            : client.OpenWrite(path);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var attributes = client.GetAttributes(path);
        if (attributes.IsDirectory)
        {
            await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
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
}
