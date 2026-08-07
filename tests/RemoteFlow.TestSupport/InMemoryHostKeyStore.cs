using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.TestSupport;

public sealed class InMemoryHostKeyStore : IHostKeyStore
{
    private readonly Lock _lock = new();
    private readonly List<HostKey> _keys = [];

    public Task<HostKey?> GetAsync(
        string host,
        int port,
        string keyAlgorithm,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(_keys.SingleOrDefault(item =>
                string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) &&
                item.Port == port &&
                string.Equals(item.KeyAlgorithm, keyAlgorithm, StringComparison.Ordinal)));
        }
    }

    public Task<IReadOnlyList<HostKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<HostKey>>([.. _keys]);
        }
    }

    public Task<IReadOnlyList<HostKey>> ListForHostAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<HostKey>>([.. _keys.Where(item =>
                string.Equals(item.Host, host, StringComparison.OrdinalIgnoreCase) && item.Port == port)]);
        }
    }

    public Task AddAsync(HostKey hostKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_keys.Any(item =>
                string.Equals(item.Host, hostKey.Host, StringComparison.OrdinalIgnoreCase) &&
                item.Port == hostKey.Port &&
                string.Equals(item.KeyAlgorithm, hostKey.KeyAlgorithm, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A host key already exists for this host, port, and algorithm.");
            }

            _keys.Add(hostKey);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(HostKey hostKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var index = _keys.FindIndex(item => item.Id == hostKey.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("The host key does not exist.");
            }

            _keys[index] = hostKey;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _ = _keys.RemoveAll(item => item.Id == id);
        }

        return Task.CompletedTask;
    }
}
