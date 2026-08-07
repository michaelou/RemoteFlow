using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

public interface IHostKeyStore
{
    Task<HostKey?> GetAsync(
        string host,
        int port,
        string keyAlgorithm,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostKey>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostKey>> ListForHostAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);

    Task AddAsync(HostKey hostKey, CancellationToken cancellationToken = default);

    Task UpdateAsync(HostKey hostKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
