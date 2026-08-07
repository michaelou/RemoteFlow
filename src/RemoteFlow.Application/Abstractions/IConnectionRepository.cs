using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

public interface IConnectionRepository
{
    Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Connection>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Connection connection, CancellationToken cancellationToken = default);

    Task UpdateAsync(Connection connection, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> AddTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default);

    Task<bool> RemoveTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default);
}
